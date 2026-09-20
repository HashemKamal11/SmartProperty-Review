# Authentication Model

This document records the Step 05.4 authentication infrastructure foundation, the Step 05.5B registration use case, the Step 05.5D login use case, the Step 05.5E refresh-token rotation use case, the Step 05.5F Me use case with standardized protected-endpoint responses, and the Step 05.5G logout use case. The authorization system remains deferred.

## Architecture

SmartProperty uses custom authentication built on its own identity model:

- JWT Bearer access tokens, validated by ASP.NET Core JWT Bearer authentication.
- Opaque refresh tokens, persisted only as hashes.
- The existing custom `User` model. ASP.NET Core Identity users, stores, managers, and schema are not used.

Authentication identifies the caller. Authorization (roles, permissions, workspace access) is a separate concern and is not implemented yet.

```text
Application (framework-independent)
    IPasswordHasher
    ITokenProvider  -> AccessToken, GeneratedRefreshToken
    IUserCredentialRepository
    IRefreshTokenRepository
    ICurrentUser, IDateTimeProvider (existing, reused)

Api (implementations and pipeline)
    PasswordHasher, TokenProvider, CurrentUser, DateTimeProvider, JwtOptions
    JWT Bearer registration

Persistence
    identity.user_credentials, identity.refresh_tokens
    UserCredentialRepository, RefreshTokenRepository
```

## Existing Abstractions Reused

- `ICurrentUser` is implemented at the API boundary by `CurrentUser`. No other current-user abstraction exists.
- `IDateTimeProvider` is implemented by `DateTimeProvider` and returns `DateTimeOffset.UtcNow`. The token provider uses it for issued-at and expiration times.
- `Result`, `Error`, and the CQRS interfaces are unchanged and will be used by the future authentication use cases.

## Current User

`CurrentUser` reads `HttpContext.User`:

- `IsAuthenticated` is true only when the request principal is authenticated.
- `UserId` is the Guid in the standard JWT `sub` claim. It is `null` when the request is unauthenticated. It never throws for anonymous requests.

JWT Bearer is configured with `MapInboundClaims = false` so `sub` keeps its JWT name. A bearer token authenticates only when it carries a valid subject (see Access Tokens), so an authenticated request always has a `UserId`. `CurrentUser` applies the same subject rules defensively. `CurrentUser` intentionally does not read roles, permissions, or workspace memberships.

## Credentials

Authentication secret state is kept separate from the identity profile:

- `User` (`identity.users`) holds profile and status. It has no password field and was not changed.
- `UserCredential` (`identity.user_credentials`) holds `UserId`, `PasswordHash`, `CreatedAt`, and `UpdatedAt`. `UserId` is the primary key and a one-to-one foreign key to `identity.users` (`ON DELETE RESTRICT`).

Invariants: `UserId` must not be empty, `PasswordHash` must not be empty, and `UpdatedAt` cannot move backwards. Validation happens before mutation. The database check constraint `ck_identity_user_credentials_updated_after_created` (`updated_at >= created_at`) mirrors the timestamp ordering.

`PasswordHash` stores only the encoded hash. Plaintext passwords are never stored.

## Password Hashing

`IPasswordHasher` exposes `Hash(password)`, `Verify(password, passwordHash)`, and `PerformDummyVerification(password)`.

`PerformDummyVerification` runs the same verification work as `Verify` against a throwaway hash held by the hasher and discards the result. It exists so that a login for which no stored credential can be loaded still pays the password-hashing cost (see Login Timing). It returns nothing and can never authenticate anyone. The throwaway hash is produced once per hasher instance from a non-secret constant that belongs to no account, and is never persisted, logged, or exposed. Keeping the method on the abstraction rather than the encoded hash itself keeps Application free of framework hash formats.

The implementation delegates to the framework `PasswordHasher<TUser>` from `Microsoft.Extensions.Identity.Core`, which is part of the ASP.NET Core shared framework. It uses PBKDF2 with a per-password random salt, a versioned self-describing hash format, and fixed-time comparison. Only this hashing component is used; no Identity stores, managers, or tables are introduced.

`Verify` returns the framework-neutral Application enum `PasswordVerificationStatus` (`Failed`, `Success`, `SuccessRehashNeeded`), mapped one-to-one from the framework `PasswordVerificationResult`. The framework type does not leave the API layer. `SuccessRehashNeeded` means the password matched but the stored hash should be replaced; the Login use case re-hashes it (see Login).

`Verify` fails closed on unusable persisted hashes and returns `Failed` without throwing when the stored hash is:

- `null`, empty, or whitespace (checked explicitly before calling the framework)
- not valid Base64 (the framework throws `FormatException`, which is caught)
- valid Base64 but not a recognized hash structure (the framework reports `Failed`)

Only `FormatException` is caught; other failures are not hidden. A null or empty plaintext password is still rejected with `ArgumentException`.

The hasher enforces no password policy.

## Access Tokens

- Format: JWT signed with HMAC-SHA256 (`HS256`), issued with `JsonWebTokenHandler`. JWT signing is not implemented manually.
- Claims: `sub` (User Id), `jti` (unique token id), and the standard `iss`, `aud`, `iat`, `nbf`, `exp`.
- No role, permission, platform role, or workspace membership claims are emitted. The claims strategy will be designed with authorization.
- The token provider makes no authorization decisions and performs no repository lookups.

Validation (JWT Bearer) requires:

- a valid signature from the configured signing key (`RequireSignedTokens`, `ValidateIssuerSigningKey`)
- only the `HS256` algorithm (`ValidAlgorithms`)
- a matching issuer and audience
- an expiration claim and an unexpired token (`ValidateLifetime`, `RequireExpirationTime`)
- clock skew limited to 30 seconds
- exactly one top-level `sub` member in the signed payload, whose JSON value is a string holding a non-empty Guid in the issued format (`"D"`, no surrounding whitespace)
- exactly one raw `sub` claim on the principal, holding the same Guid

The subject rule is enforced in `JwtBearerEvents.OnTokenValidated`, after signature, algorithm, issuer, audience, and lifetime validation succeed. A token whose `sub` is missing, empty, malformed, not a Guid, `Guid.Empty`, present more than once, or not a JSON string (array, object, null, number, boolean) fails authentication. The shape is checked in the signed payload itself, because the token parser collapses duplicate keys and can turn arrays into scalar claims. Escaped property names such as `sub` count as `sub`; nested `sub` properties do not. If the payload and principal subjects disagree, authentication fails. Authentication therefore never yields an authenticated principal without a user id. The hook performs no database lookups and no user status, role, or permission checks.

## Refresh Tokens

- The raw token is 64 bytes (512 bits) from `RandomNumberGenerator`, encoded as Base64Url. The client will receive this value.
- The persisted value is the lowercase hex SHA-256 hash of the raw token. It is deterministic, so a presented token can be looked up by hash.
- The raw token is never persisted, never placed in JWT claims, and should never be logged. `GeneratedRefreshToken.ToString()` omits it.

`RefreshToken` (`identity.refresh_tokens`) holds `Id`, `UserId`, `TokenHash`, `CreatedAt`, `ExpiresAt`, and `RevokedAt`.

Invariants:

- `Id` and `UserId` must not be empty.
- `TokenHash` is required and has a unique index (`ux_identity_refresh_tokens_token_hash`).
- `ExpiresAt` must be later than `CreatedAt`.
- `RevokedAt`, when set, must not be before `CreatedAt`. A token cannot be revoked twice. Validation happens before mutation.
- Database check constraints mirror the expiration and revocation rules.

`IsActive(now)` is true when the token is not revoked and not expired.

## Configuration

Settings are bound from the `Jwt` section to `JwtOptions` and validated at startup. The API fails fast when they are invalid.

| Setting | Committed value | Notes |
| --- | --- | --- |
| `Issuer` | `SmartProperty` | Technical default |
| `Audience` | `SmartProperty.Api` | Technical default |
| `SigningKey` | empty | Required. At least 32 bytes. Never committed. |
| `AccessTokenLifetime` | `00:15:00` | Technical default requiring team review |
| `RefreshTokenLifetime` | `7.00:00:00` | Technical default requiring team review |

The token lifetimes are technical defaults, not confirmed business policy.

Startup validation also rejects lifetimes that cannot be added to the current UTC time without exceeding `DateTimeOffset.MaxValue`. This is an arithmetic overflow guard only and imposes no business maximum.

Supply the signing key from a secure source, and use a high-entropy random value (for example, 64 random bytes encoded as Base64):

```text
dotnet user-secrets set "Jwt:SigningKey" "<random value>" --project src/Presentation/SmartProperty.Api
```

or the environment variable:

```text
Jwt__SigningKey=<random value>
```

Production values come from secure deployment configuration.

## Pipeline

```text
CorrelationIdMiddleware
UseExceptionHandler
UseAuthentication
UseAuthorization
MapControllers / health endpoints
```

`UseAuthentication` and `UseAuthorization` are called explicitly. Otherwise `WebApplication` automatically inserts them ahead of all application middleware, before the correlation ID and exception handling. No authorization policies, requirements, or handlers are registered. Health endpoints remain anonymous.

## User Status Rule

Only `Active` users may receive normal authenticated access. Login and Refresh must not issue tokens to `Pending`, `Suspended`, or `Deactivated` users, and Me rejects them even when they present a token issued while the account was still active.

The rule belongs to the Application authentication use cases, which load the user and check `User.Status` before calling `ITokenProvider`. The token provider does not enforce it. Login enforces it as described in Login.

## Save And Transaction Boundary

Repositories query and track entities but never call `SaveChangesAsync`. The Application abstraction `IUnitOfWork` exposes only `SaveChangesAsync` and is the single commit boundary. Its Persistence implementation delegates to `ApplicationDbContext.SaveChangesAsync` and translates a violation of a recognized unique constraint into the provider-neutral `UniqueConstraintViolationException` (see Registration). Every other failure propagates unchanged.

`IUnitOfWork` and all repositories are scoped and receive the same scoped `ApplicationDbContext`, so changes tracked through any repository within a request are committed together by one `SaveChangesAsync` call. Use cases such as Registration (`User`, `UserCredential`, `WorkspaceAccessRequest`), Login (`RefreshToken`, plus a re-hashed `UserCredential` when needed), and Refresh (the revoked `RefreshToken` and its replacement) call it once after all changes are tracked. No explicit transaction API is exposed.

`SaveChangesAsync` translates two recognized provider failures into framework-neutral Application exceptions and lets every other failure propagate unchanged: `UniqueConstraintViolationException` for a recognized unique-constraint violation, identified by SQLSTATE and constraint name, and `ConcurrencyConflictException` for a recognized optimistic-concurrency conflict, identified by EF entry metadata. The two are deliberately distinct: a constraint violation means the data was rejected, a concurrency conflict means another writer changed the row first.

## Registration

`POST /api/auth/register` is anonymous. `AuthController` maps the request body to the Application `RegisterCommand`, which `RegisterCommandHandler` handles through `ICommandHandler<RegisterCommand, RegisterResult>`.

```json
{
  "email": "user@example.com",
  "password": "...",
  "firstName": "Ada",
  "lastName": "Lovelace",
  "workspaceId": "3fa85f64-5717-4562-b3fc-2c963f66afa6"
}
```

A successful registration tracks three entities and commits them with one `IUnitOfWork.SaveChangesAsync` call, so they are saved together or not at all:

- a `User` with status `Pending`. The `User` constructor trims the names and normalizes the email (trimmed, lower-case invariant).
- its `UserCredential`, holding only the hash returned by `IPasswordHasher`. The password is hashed exactly as sent and is never persisted or returned.
- one `WorkspaceAccessRequest` with status `Pending` for the selected existing workspace.

All three use the same `IDateTimeProvider.UtcNow` timestamp. Registration does not activate the user, create a `WorkspaceMembership`, assign roles or permissions, approve the request, or issue access or refresh tokens. Workspace access is granted only by the later Platform Admin approval workflow.

Success returns `201 Created` without a `Location` header, because no endpoint can read a registration back yet:

```json
{
  "userId": "...",
  "status": "Pending",
  "workspaceId": "...",
  "workspaceAccessRequestId": "...",
  "workspaceAccessStatus": "Pending"
}
```

Checks run in this order. A failing check returns before any entity is tracked, so nothing is saved.

| Order | Condition | Status | Code |
| --- | --- | --- | --- |
| 1 | Body is not valid JSON, not an object, or has a wrongly typed value | 400 | `request.malformed` |
| 2 | Input validation fails | 422 | `validation.failed` |
| 3 | `workspaceId` does not identify an existing workspace | 404 | `workspaces.not_found` |
| 4 | The normalized email is already registered | 409 | `users.email_already_exists` |

The workspace is checked before the email, so a request with an unknown workspace reveals nothing about registered emails. Registration never creates a workspace.

Validation reports the first failing rule in `message` and returns `fieldErrors` as `null`, because `Error` cannot carry field-level errors yet. A missing or `null` member is a validation failure (422), not a malformed request.

- `email`: required; at most 320 characters after trimming; exactly one `@` between a non-empty local part and domain, with no whitespace or control characters. This is a structural check only; it does not prove that the address exists or belongs to the caller.
- `password`: required and not only whitespace; at most 128 characters.
- `firstName`, `lastName`: required; at most 100 characters after trimming.
- `workspaceId`: required and not the empty GUID.

The email and name limits match the `identity.users` column lengths.

Password policy status: no password strength policy is approved. The 128-character maximum is a defensive bound on input to password hashing, not a strength rule. Minimum length, complexity, and breached-password rules remain deferred.

Duplicate emails: the lookup uses the email as normalized by `User`, so addresses that differ only in letter case or surrounding whitespace are duplicates, and the request returns `409` with `users.email_already_exists`. The unique index `ux_identity_users_email` remains the final guarantee. Two concurrent registrations for the same new email can both pass the lookup; the database then rejects the later insert, and that request's whole `SaveChangesAsync` rolls back without partial rows. `UnitOfWork` recognizes this violation from the PostgreSQL SQLSTATE (`23505`) and constraint name, and rethrows it as `UniqueConstraintViolationException` with `PersistenceConstraint.UserEmail`. The handler catches only that exception and returns the same `409` with `users.email_already_exists`. The SQLSTATE, constraint name, and provider exception types stay inside Persistence. Any other database failure still propagates as an unexpected `500`.

## Login

`POST /api/auth/login` is anonymous. `AuthController` maps the request body to the Application `LoginCommand`, which `LoginCommandHandler` handles through `ICommandHandler<LoginCommand, LoginResult>`.

```json
{
  "email": "user@example.com",
  "password": "..."
}
```

Checks run in this order. Every failing check returns before any token is created or any change is tracked, so nothing is saved.

| Order | Condition | Status | Code |
| --- | --- | --- | --- |
| 1 | Body is not valid JSON, not an object, or has a wrongly typed value | 400 | `request.malformed` |
| 2 | Input validation fails | 422 | `validation.failed` |
| 3 | No user has the email, the user has no `UserCredential`, or the password does not verify | 401 | `authentication.invalid_credentials` |
| 4 | The password verified, but `User.Status` is not `Active` | 403 | `authentication.account_unavailable` |

Validation uses the registration bounds, so every registered email passes: `email` is required, at most 320 characters after trimming, with the same structural check as registration; `password` is required, not only whitespace, and at most 128 characters. No password strength rule is applied. A missing or `null` member is a validation failure (422).

Invalid credentials: an unknown email, a missing credential row, a wrong password, and a stored hash that is malformed (which `IPasswordHasher.Verify` reports as `Failed`) all return the same `401` body with the message `Invalid email or password.`. The response never says which case occurred. The email lookup uses `IUserRepository.GetByEmailAsync`, which applies the same normalization as `User` (trimmed, lower-case invariant).

Account status: the status is checked only after the password verifies, so a caller who does not know the password always receives the `401` above, whatever the account's status. With the correct password, `Pending`, `Suspended`, and `Deactivated` users all receive the same `403` body with the message `This account is not currently allowed to sign in.`; the exact status is not disclosed. Only `Active` users receive tokens.

Password re-hash: when verification returns `SuccessRehashNeeded` for an `Active` user, the handler hashes the supplied password again with `IPasswordHasher.Hash` and applies it through `UserCredential.ChangePasswordHash` with the login timestamp. The credential is tracked by the shared scoped `ApplicationDbContext`, so no repository update method is needed. A rejected login (wrong password or non-active account) never re-hashes.

On success the handler, in order:

1. captures the login timestamp from `IDateTimeProvider.UtcNow`
2. applies the password re-hash, when needed
3. creates the access token with `ITokenProvider.CreateAccessToken(user.Id)`
4. creates the refresh token with `ITokenProvider.CreateRefreshToken()`
5. tracks a `RefreshToken` with a new id, the user id, the generated `TokenHash`, the login timestamp as `CreatedAt`, and the generated expiration
6. calls `IUnitOfWork.SaveChangesAsync` once
7. returns the tokens only after the save succeeds

The re-hash and the new refresh token are committed together or not at all. A failure while creating tokens happens before the save, so nothing is persisted. A failed save propagates as an unexpected `500` and returns no tokens. Login does not catch or translate persistence exceptions.

Only the refresh token's hash is persisted. The raw refresh token and the access token are returned to the client and never stored. The access token carries only the identity claims described in Access Tokens: no roles, permissions, platform roles, or workspace data.

Success returns `200 OK`:

```json
{
  "accessToken": "...",
  "accessTokenExpiresAt": "2026-09-17T12:15:00+00:00",
  "refreshToken": "...",
  "refreshTokenExpiresAt": "2026-09-24T12:00:00+00:00"
}
```

Sessions: each successful login adds a new refresh token. Earlier refresh tokens are not revoked, so several sessions per user are currently allowed. Single-session enforcement, device sessions, and token families are not implemented.

Secrets: `LoginCommand` and `LoginRequest` override `ToString()` to omit the email and password; `LoginResult` and `LoginResponse` override it to omit both tokens. The `AccessToken` and `GeneratedRefreshToken` records returned by `ITokenProvider` override it to omit their token values, so neither a signed JWT nor a raw refresh token reaches a log or debugger display through a record's default `ToString()`. JSON serialization of the response is unaffected. Login writes no log entries of its own.

### Login Timing

The `401` body is identical for every invalid-credential case, and every rejected attempt now performs one password verification:

| Case | Work performed |
| --- | --- |
| Unknown email | one `PerformDummyVerification` |
| Missing credential row | one `PerformDummyVerification` |
| Wrong password | one real `Verify` |
| Correct password | one real `Verify`, no dummy verification |

The dummy verification uses the same hasher instance and configuration as a real one, so an unregistered email no longer returns without running PBKDF2. That removes the obvious bypass in which an attacker distinguished registered from unregistered emails by an order-of-magnitude response-time difference. The dummy path runs only where no credential was loaded: never after a real verification, and never on success.

This is timing hardening, not a formal constant-time guarantee. The paths still differ in the database work they do and in ordinary scheduling and allocation noise, and the measured times are close rather than identical. Two residual differences are known:

- A malformed or unusable stored hash fails closed as `Failed` without completing full PBKDF2 work, so such an account can still answer faster than a normal wrong password. This affects only corrupted persisted data, not any credential the application writes, and is not addressed here.
- Registration already reveals whether an email is registered through `409` for a request with a valid workspace id.

Rate limiting and account lockout remain deferred (see Deferred Decisions).

## Refresh Use Case

`POST /api/auth/refresh` exchanges an active refresh token for a new access token and a new refresh token. The endpoint is `[AllowAnonymous]`: the refresh token is itself the credential, and the access token it replaces may already have expired, so requiring a Bearer token would make the endpoint useless.

Request body:

```json
{
  "refreshToken": "..."
}
```

`RefreshRequest.RefreshToken` is nullable, so a missing value is reported by Application validation (`422`) rather than model-binding (`400`). Validation checks only presence and a defensive 512-character maximum. The token is an opaque credential: it is never trimmed, never decoded, and its format is never validated, so it is matched exactly against the stored hash. The bound is not a format rule — tokens issued today are about 86 Base64Url characters from 64 random bytes, and `token_hash` is a fixed 128 characters regardless of input length — it exists only to reject absurd payloads while leaving room for a future token format.

`RefreshCommandHandler` then:

1. hashes the raw token with `ITokenProvider.HashRefreshToken`; the raw token never reaches a query, the database, or a log
2. loads the row with `IRefreshTokenRepository.GetByTokenHashAsync`
3. takes the refresh timestamp from `IDateTimeProvider.UtcNow`
4. rejects the request unless `RefreshToken.IsActive(now)` — not revoked, and `now` strictly before `ExpiresAt`, so `ExpiresAt <= now` is expired
5. loads the user with `IUserRepository.GetByIdAsync`
6. requires `UserStatus.Active`
7. creates the new access token and refresh token
8. revokes the presented token with `RefreshToken.Revoke(now)`
9. tracks a new `RefreshToken` with a new id, the same user id, the generated `TokenHash`, the refresh timestamp as `CreatedAt`, and the generated expiration
10. calls `IUnitOfWork.SaveChangesAsync` once
11. returns the tokens only after the save succeeds

Invalid-token privacy: an unknown token hash, a revoked token, an expired token, a token whose user no longer exists, a replayed token, and the loser of a concurrent rotation all return the same `401` with `authentication.invalid_refresh_token` and the message `Invalid or expired refresh token.`. The response never says which case occurred, and never exposes a token id, a hash, a timestamp, a constraint name, or any database detail. A token without its user is invalid session state, not a `404`.

Account status: only `Active` users may refresh. `Pending`, `Suspended`, and `Deactivated` all return `403` with `authentication.account_unavailable`, the same generic error Login uses, so the exact status is never disclosed. A forbidden refresh changes nothing — the presented token is not revoked, no tokens are created, and nothing is saved. Whether a status change should revoke existing sessions is a separate, deferred decision.

One-time rotation: a refresh token can rotate successfully only once. The presented token is revoked in the same save that inserts its replacement, so after a successful refresh the old raw token returns `401` and only the new one works. Only the new token's hash is persisted; the raw refresh token and the access token are returned to the client and never stored. The new access token carries the same identity-only claims described in Access Tokens: no roles, permissions, platform roles, or workspace data.

Nothing is mutated before every check has passed, so a rejected refresh performs no save at all. A failure while creating tokens happens before anything is tracked. A failed save leaves the presented token active and inserts no replacement, and propagates as an unexpected `500` with no tokens.

### Concurrent Use of the Same Refresh Token

Two requests can both load the same token with `RevokedAt` null before either saves. `RefreshToken.RevokedAt` is mapped as an EF Core concurrency token, so the generated statement is:

```sql
UPDATE identity.refresh_tokens SET revoked_at = @p0
WHERE id = @p1 AND revoked_at IS NULL;
```

The first writer matches and commits. The second carries the same `revoked_at IS NULL` predicate, affects zero rows, and EF raises `DbUpdateConcurrencyException`; its replacement insert is in the same batch and is rolled back with it. `UnitOfWork` recognizes that conflict from EF's own entry metadata — never from message text — and only when every failed entry is a `RefreshToken`; it then throws the framework-neutral `ConcurrencyConflictException(PersistenceResource.RefreshToken)`. Any other concurrency failure does not match and propagates unchanged. The handler maps that one signal to the same `401 authentication.invalid_refresh_token`, because from the client's point of view the credential is simply spent. There is no separate concurrency error code and no `409`.

This needs no schema change: marking an existing column as a concurrency token changes only the `UPDATE` predicate, not the column or the table.

Exactly one concurrent request therefore succeeds, every other receives `401`, none receives `500`, and exactly one replacement row survives.

Sessions: each successful refresh replaces one session's token. Other refresh tokens for the same user are untouched, so several concurrent sessions per user remain allowed. Refresh-token families, family-wide revocation on replay, and reuse-chain detection are not implemented: replaying an already-rotated token is rejected on its own, and the newly issued token is not revoked.

Secrets: `RefreshCommand` and `RefreshRequest` override `ToString()` to omit the raw refresh token; `RefreshResult` and `RefreshResponse` override it to omit both tokens. JSON serialization of the response is unaffected. Refresh writes no log entries of its own.

## Me Use Case

`GET /api/auth/me` returns the authenticated user's current profile. It is the first permanently protected endpoint: the action carries `[Authorize]`, not `[AllowAnonymous]`.

The identity comes only from the validated principal. `GetMeQueryHandler` reads `ICurrentUser.UserId` and accepts no user id, email, subject, or workspace id from the route, query string, headers, or a body. Application never reads the `Authorization` header, parses a JWT, or references an ASP.NET or token-library type — JWT Bearer already owns token validation, and `CurrentUser` translates the validated principal into a `Guid?` that is null rather than throwing when no usable subject is present.

The query has no input to validate, so there is no validator. It uses the existing `IQuery<TResponse>` / `IQueryHandler<TQuery, TResponse>` abstractions, which already existed unused: Me is a read, and no new CQRS framework was introduced for it.

The handler:

1. reads `ICurrentUser.UserId`; a missing or unusable subject fails closed with `401`
2. loads the user with `IUserRepository.GetByIdAsync`, propagating the cancellation token
3. returns `401` when no row matches
4. returns `403` when `User.Status` is not `Active`
5. otherwise returns the profile read from the current row

Status is re-read on every call, so an access token minted while the account was active stops working here as soon as the account is no longer active. Tokens are not revoked when this happens; automatic session revocation on a status change remains deferred.

Me is read-only: no `SaveChangesAsync`, no last-login write, no credential or refresh-token access, and no token issuance.

Success returns `200 OK`:

```json
{
  "userId": "0b4f2f4e-1f0e-4f2a-9a5e-6d4b8f1c2a30",
  "email": "user@example.com",
  "firstName": "First",
  "lastName": "Last"
}
```

The contract uses `userId`, matching the field name Register already returns, rather than a bare `id`. Status is not returned: a successful response already implies the account is active. The response carries no access token, refresh token, `jti`, expiration, password data, credential data, role, permission, platform role, membership, or workspace information, and the access token is unchanged — it stays identity-only, so profile values are read from the database rather than from claims.

Failures use the standard error contract:

| Status | Code | When |
| --- | --- | --- |
| `401` | `authentication.unauthorized` | No usable subject, or the subject names a user that no longer exists |
| `403` | `authentication.account_unavailable` | The user exists but is not `Active` |

A token can outlive the user it names. That returns the same generic `401` as an unauthenticated request — never `404` — so the response does not confirm whether an account id was ever real.

Secrets: `MeResult` and `MeResponse` override `ToString()` to print only the user id, keeping the email and name out of logs and debugger displays. JSON serialization is unaffected, so the response body still carries the full profile.

## Logout Use Case

`POST /api/auth/logout` revokes the refresh token the caller presents, and nothing else. The action is `[AllowAnonymous]`: the refresh token being destroyed is itself the credential, the caller's access token may already have expired, and an account that can no longer sign in must still be able to end a session. No Bearer token is required, and the protected-endpoint `401`/`403` behavior is unaffected.

Request body:

```json
{
  "refreshToken": "..."
}
```

`LogoutRequest.RefreshToken` is nullable, so a missing value is Application validation (`422`) rather than model binding (`400`). Validation checks only presence and the same defensive 512-character maximum Refresh applies; the token is opaque, so it is never trimmed, decoded, or format-checked.

`LogoutCommandHandler` then hashes the raw token with `ITokenProvider.HashRefreshToken`, loads the row with `IRefreshTokenRepository.GetByTokenHashAsync`, takes one timestamp from `IDateTimeProvider.UtcNow`, and — only when `RefreshToken.IsActive(now)` — calls `RefreshToken.Revoke(now)` followed by a single `IUnitOfWork.SaveChangesAsync`. The raw token never reaches a query, the database, or a log. No user is loaded and no account status is checked: that would add account-state exposure and work to an operation whose whole purpose is destroying a credential.

No replacement token is created, no access token is issued, and nothing but that one row is touched.

### Idempotence

Every structurally valid request returns `204 No Content` with an empty body:

| Case | Work |
| --- | --- |
| Active token | revoked, one `SaveChanges` |
| Unknown token | none, no save |
| Expired token | none, no save — `RevokedAt` is not set merely because a token expired |
| Already revoked token | none, no save — the original `RevokedAt` is left untouched |
| Loser of a concurrent revocation | none committed, mapped to success |

The response never says which case occurred, so it discloses nothing about whether the token exists, is expired, was already revoked, or was previously rotated. Repeated logout is therefore safe, and a client may retry freely.

This is deliberately different from Refresh. Refresh must prove a usable credential before issuing new ones, so an unusable token there is `401 authentication.invalid_refresh_token`. Logout only attempts to destroy the presented credential, and a token that cannot be used is already in the state logout wanted — so it reports success rather than `401`.

### Concurrent Logout

`RefreshToken.RevokedAt` is already an EF Core concurrency token (see Refresh), so a second writer's `UPDATE … WHERE id = @p1 AND revoked_at IS NULL` affects zero rows and EF raises a conflict, which `UnitOfWork` translates to `ConcurrencyConflictException(PersistenceResource.RefreshToken)`. The handler catches only that recognized signal, around the single save, and treats it as success: the credential is gone either way. Nothing new was added for logout — no second concurrency exception, no extra column, no lock, no serializable transaction — and the translation was not broadened.

Twenty simultaneous logouts of one token therefore all return `204`, with exactly one persisted revocation, no replacement rows, and no `409` or `500`.

### Logout versus Refresh on the same token

Both mutate the same concurrency-protected value, so only one persisted change to that token can win:

- **Logout wins:** logout `204`, refresh `401 authentication.invalid_refresh_token`, no replacement token.
- **Refresh wins:** refresh `200` with a replacement token, logout `204` through the idempotent path, the presented token revoked, exactly one replacement.

Both are correct. There is no deterministic winner, and neither produces a `500`, leaves the presented token active, or creates more than one replacement.

**Known limitation, stated plainly:** if Refresh wins that race, the replacement token it just issued stays active. Logout revokes only the credential it was given, and the model has no token-family ancestry to follow, so logout cannot guarantee that an already-concurrently-rotated chain is terminated. Family-wide revocation is deferred; it is not implemented here.

### Access tokens are unaffected

Logout does not change access-token validation. Access tokens are stateless — not persisted, not blacklisted, and not introspected — so an access token issued before logout stays cryptographically valid until it expires. `GET /api/auth/me` with that token still returns `200` for an active user after logout. This is verified behavior, not an oversight.

What logout does achieve is ending renewal: the revoked refresh token can no longer be exchanged, so the session cannot be extended past the current access token's lifetime.

| Token | After logout |
| --- | --- |
| Access token (short-lived) | remains valid until it expires |
| Presented refresh token (long-lived) | revoked immediately |

Clients must therefore discard both their access token and their refresh token locally on receiving `204`.

Sessions: logout revokes one session's token. The user's other refresh tokens stay active, so signing out of one client does not sign out the others. Logout-all-sessions and device management are deferred.

Secrets: `LogoutCommand` and `LogoutRequest` override `ToString()` to omit the raw refresh token. Logout returns no result type, so there is nothing else to keep safe, and it writes no log entries of its own.

## Protected Endpoint Responses

JWT Bearer no longer returns the framework's empty bodies. Both events write the same `ApiErrorResponse` contract as the rest of the API, through `ApiErrorResponseFactory`, so there is no second error format and no hand-built JSON.

**`401` challenge** (`JwtBearerEvents.OnChallenge`) — one response for every rejected or absent credential:

```json
{
  "code": "authentication.unauthorized",
  "message": "Authentication is required.",
  "status": 401,
  "fieldErrors": null,
  "correlationId": "..."
}
```

It is returned identically for a missing `Authorization` header, a non-Bearer or unknown scheme, an empty or malformed credential, a malformed JWT, a bad signature or wrong signing key, a wrong issuer or audience, an expired token, a disallowed algorithm, and every subject failure — missing, duplicate, non-string, non-canonical, or `Guid.Empty`. Which rule rejected the token is never disclosed, and no JWT material or exception text appears in the body.

`WWW-Authenticate: Bearer` is still sent. `OnChallenge` calls `HandleResponse()` to stop the framework writing a competing response, which also skips the framework's own header, so the bare scheme is re-added explicitly. `IncludeErrorDetails` is set to `false`, so the header carries no `error` or `error_description` naming the failure. `OnAuthenticationFailed` deliberately writes no response: a failure there flows on to the challenge, which owns the single public `401`.

**`403` authorization failure** (`JwtBearerEvents.OnForbidden`) — authentication succeeded but an authorization requirement was not met:

```json
{
  "code": "authorization.forbidden",
  "message": "You do not have permission to access this resource.",
  "status": 403,
  "fieldErrors": null,
  "correlationId": "..."
}
```

It names no policy, role, permission, or authorization handler. No `WWW-Authenticate` header is sent, because the caller is already authenticated.

Both events read the request's existing correlation id through `CorrelationIdFeature`; neither generates its own. The correlation-id middleware is registered first in the pipeline, so the id is available to both. Both responses are written with `WriteAsJsonAsync`, so the content type is JSON rather than `text/plain` or framework HTML, and both check `Response.HasStarted` before writing so a response is never written twice.

### Two different `403`s

| Code | Meaning |
| --- | --- |
| `authentication.account_unavailable` | The authenticated account itself may not be used — it is `Pending`, `Suspended`, or `Deactivated`. Raised by Login, Refresh, and Me. |
| `authorization.forbidden` | The account is fine, but the caller lacks permission for this resource. Raised by the authorization middleware. |

They share the `ApiErrorResponse` contract but keep distinct codes and messages, because the causes are different: one is about the account, the other about the resource.

## Deferred Decisions

- Password policy (length, complexity).
- Production access and refresh token lifetimes.
- Refresh-token families: family-wide revocation when a rotated token is replayed, and reuse-chain detection. Replay of a rotated token is rejected on its own, but the tokens issued after it are not revoked.
- Logout-all-sessions and device/session management. Logout revokes only the presented refresh token; a user's other sessions are unaffected.
- Access-token revocation. Access tokens stay valid until they expire, including after logout, because they are stateless. A blacklist, jti revocation table, or introspection endpoint would be needed to change that.
- Session policy (single session, device sessions, session lists, revoke-all-sessions). Each login adds a session and each refresh replaces one; neither revokes the others.
- Whether a status change to Pending, Suspended, or Deactivated should revoke that user's existing refresh tokens. A non-active user cannot refresh, but their tokens are left untouched.
- A formal constant-time login. Unknown emails and missing credentials now perform the same password verification work as a wrong password (see Login Timing), but the paths are not provably indistinguishable, and a corrupted stored hash still fails faster.
- Authorization itself: policies, roles, permissions, workspace scoping, and Platform Admin rules. The standardized `authorization.forbidden` response exists (see Protected Endpoint Responses), but no requirement yet produces it in tracked source.
- `415 Unsupported Media Type` and `405 Method Not Allowed` response bodies. Controllers currently return the framework `ProblemDetails` body for 415 and an empty body for 405, not `ApiErrorResponse`.
- Structured `fieldErrors` for Application validation failures.
- Claims and authorization strategy.
- Email verification.
- MFA.
- Password reset.
- Account lockout and failed login tracking.
- Expired or revoked refresh token cleanup.
- Migration for `identity.user_credentials` and `identity.refresh_tokens`.
- Seed strategy.
