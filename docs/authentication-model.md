# Authentication Model

This document records the Step 05.4 authentication infrastructure foundation. It establishes credential and token infrastructure only. Login, Registration, Refresh, and Logout use cases, authentication endpoints, and authorization remain deferred.

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

`IPasswordHasher` exposes `Hash(password)` and `Verify(password, passwordHash)`.

The implementation delegates to the framework `PasswordHasher<TUser>` from `Microsoft.Extensions.Identity.Core`, which is part of the ASP.NET Core shared framework. It uses PBKDF2 with a per-password random salt, a versioned self-describing hash format, and fixed-time comparison. Only this hashing component is used; no Identity stores, managers, or tables are introduced.

`Verify` returns the framework-neutral Application enum `PasswordVerificationStatus` (`Failed`, `Success`, `SuccessRehashNeeded`), mapped one-to-one from the framework `PasswordVerificationResult`. The framework type does not leave the API layer. `SuccessRehashNeeded` means the password matched but the stored hash should be replaced; re-hashing on login belongs to the Login use case.

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

Only `Active` users may receive normal authenticated access. Future Login and Refresh workflows must not issue access tokens to `Pending`, `Suspended`, or `Deactivated` users.

The rule belongs to the future Application authentication use cases, which will load the user and check `User.Status` before calling `ITokenProvider`. The token provider does not enforce it.

The client-facing error behavior for non-active users is defined together with the Login use case.

## Save And Transaction Boundary

Repositories query and track entities but never call `SaveChangesAsync`. The Application abstraction `IUnitOfWork` exposes only `SaveChangesAsync` and is the single commit boundary. Its Persistence implementation delegates to `ApplicationDbContext.SaveChangesAsync`.

`IUnitOfWork` and all repositories are scoped and receive the same scoped `ApplicationDbContext`, so changes tracked through any repository within a request are committed together by one `SaveChangesAsync` call. Use cases such as Registration (`User`, `UserCredential`, `WorkspaceAccessRequest`) call it once after all changes are tracked. No explicit transaction API is exposed.

## Deferred Decisions

- Password policy (length, complexity).
- Production access and refresh token lifetimes.
- Refresh token rotation and reuse detection.
- Login use case, including status enforcement, rehash-on-login, and non-active user responses.
- Registration use case.
- Refresh use case.
- Logout and revocation workflow.
- Authentication endpoints.
- 401 challenge and 403 forbidden response bodies. JWT Bearer currently returns the default empty ASP.NET Core responses, not `ApiErrorResponse`. This must be integrated before the first protected endpoint is introduced.
- Claims and authorization strategy.
- MFA.
- Password reset.
- Account lockout and failed login tracking.
- Expired or revoked refresh token cleanup.
- Migration for `identity.user_credentials` and `identity.refresh_tokens`.
- Seed strategy.
