# Authentication Model

This document records the Step 05.4 authentication infrastructure foundation, the Step 05.5B registration use case, and the Step 05.5D login use case. Refresh, Me, and Logout use cases, their endpoints, and authorization remain deferred.

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

Only `Active` users may receive normal authenticated access. Login and the future Refresh workflow must not issue tokens to `Pending`, `Suspended`, or `Deactivated` users.

The rule belongs to the Application authentication use cases, which load the user and check `User.Status` before calling `ITokenProvider`. The token provider does not enforce it. Login enforces it as described in Login.

## Save And Transaction Boundary

Repositories query and track entities but never call `SaveChangesAsync`. The Application abstraction `IUnitOfWork` exposes only `SaveChangesAsync` and is the single commit boundary. Its Persistence implementation delegates to `ApplicationDbContext.SaveChangesAsync` and translates a violation of a recognized unique constraint into the provider-neutral `UniqueConstraintViolationException` (see Registration). Every other failure propagates unchanged.

`IUnitOfWork` and all repositories are scoped and receive the same scoped `ApplicationDbContext`, so changes tracked through any repository within a request are committed together by one `SaveChangesAsync` call. Use cases such as Registration (`User`, `UserCredential`, `WorkspaceAccessRequest`) and Login (`RefreshToken`, plus a re-hashed `UserCredential` when needed) call it once after all changes are tracked. No explicit transaction API is exposed.

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

The Refresh endpoint is not implemented yet, so a refresh token cannot be exchanged for a new access token.

## Deferred Decisions

- Password policy (length, complexity).
- Production access and refresh token lifetimes.
- Refresh token rotation and reuse detection.
- Refresh use case.
- Logout and revocation workflow.
- Refresh, Me, and Logout endpoints.
- Login session policy (single session, device sessions, token families). Each login currently adds a session.
- A formal constant-time login. Unknown emails and missing credentials now perform the same password verification work as a wrong password (see Login Timing), but the paths are not provably indistinguishable, and a corrupted stored hash still fails faster.
- 401 challenge and 403 forbidden response bodies. JWT Bearer currently returns the default empty ASP.NET Core responses, not `ApiErrorResponse`. This must be integrated before the first protected endpoint is introduced.
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
