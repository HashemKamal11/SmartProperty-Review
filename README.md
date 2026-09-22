# SmartProperty

SmartProperty is the backend foundation for a multi-workspace property platform. It is an ASP.NET Core API on .NET 10 that follows Clean Architecture and stores data in PostgreSQL through EF Core.

The backend currently provides shared API contracts, an identity foundation, the workspace and access model, authentication infrastructure, and five authentication workflows: user registration, login, refresh-token rotation, the current-user endpoint, and logout. Authorization resolves permissions from current persisted data and has a reusable ASP.NET authorization bridge, but no production business endpoint is permission-protected yet.

## Current Status

| Area | Status | Notes |
| --- | --- | --- |
| API error contract and correlation IDs | Implemented | Standard error body and `X-Correlation-ID` header |
| Health checks | Implemented | `GET /health/live` and `GET /health/ready` |
| User identity (`User`, `UserCredential`) | Implemented | New users start as `Pending` |
| Workspaces and workspace access requests | Implemented | Workspaces are database records; access requests start as `Pending` |
| Memberships, roles, and permissions | Data model and resolution | Domain entities, EF Core mappings, repositories, and permission resolution; no workflow assigns them yet |
| Password hashing | Implemented | ASP.NET Core Identity password hasher |
| JWT access tokens | Implemented | Issued by Login and Refresh; required by `GET /api/auth/me` |
| Refresh tokens | Implemented | Issued by Login, persisted as hashes, rotated single-use by `POST /api/auth/refresh`, and revoked by `POST /api/auth/logout` |
| Current user (`ICurrentUser`) | Implemented | Reads the user id from a validated access token; used by `GET /api/auth/me` |
| Commit boundary (`IUnitOfWork`) | Implemented | One save per use case |
| `POST /api/auth/register` | Implemented | Creates `Pending` users |
| `POST /api/auth/login` | Implemented | Active users only; returns access and refresh tokens |
| `POST /api/auth/refresh` | Implemented | Active users only; single-use rotation returning a new token pair |
| `GET /api/auth/me` | Implemented | First protected endpoint; Active users only |
| Standardized protected `401` and `403` bodies | Implemented | JWT Bearer challenge and authorization failures use the standard error contract |
| `POST /api/auth/logout` | Implemented | Revokes the presented refresh token; idempotent `204` |
| Logout-all-sessions and device management | Not implemented | |
| Authorization model and contracts | Implemented | Scopes, permission-check contracts, and rules; see [Authorization Model](docs/authorization-model.md) |
| Permission resolution (`IPermissionChecker`) | Implemented | Resolves platform and workspace permissions from current persisted data, one query per check |
| ASP.NET permission authorization bridge | Implemented | `PermissionRequirement` and a resource-based handler over `AuthorizationTarget`; reusable, but unused by any endpoint |
| Production permission enforcement | Not implemented | No endpoint requires a permission yet |
| Platform endpoint enforcement | Not implemented | |
| Workspace endpoint enforcement | Not implemented | |
| Route/workspace target resolution | Not implemented | A workspace id is never read from a route, query, header, or body |
| Access request approval and role assignment workflows | Not implemented | |
| Password policy, email verification, and rate limiting | Not implemented | |
| MFA, password reset, and account lockout | Not implemented | |
| EF Core migrations and seed data | Not implemented | See [Database Schema](#3-database-schema) |
| Automated tests | Implemented foundation | Permanent unit, API integration, and PostgreSQL persistence/concurrency suites. See [Testing Strategy](docs/testing-strategy.md) |

## Architecture

The solution follows Clean Architecture. The inner layers do not depend on web, database, or token frameworks.

| Project | Responsibility |
| --- | --- |
| `SmartProperty.Domain` | Entities and their invariants: users, credentials, refresh tokens, workspaces, access requests, memberships, roles, and permissions. No framework dependencies. |
| `SmartProperty.Common` | Shared `Result` and `Error` types and pagination primitives. No framework dependencies. |
| `SmartProperty.Application` | Abstractions (repositories, `IUnitOfWork`, `IPasswordHasher`, `ITokenProvider`, `ICurrentUser`, `IDateTimeProvider`), command and query contracts, and use cases. No EF Core, ASP.NET Core, or JWT dependencies. |
| `SmartProperty.Persistence` | EF Core with PostgreSQL (Npgsql): `ApplicationDbContext`, entity configurations, repositories, `UnitOfWork`, permission resolution (`IPermissionChecker`), the database health check, and translation of recognized PostgreSQL unique-constraint violations and refresh-token concurrency conflicts into provider-neutral exceptions. |
| `SmartProperty.Api` | ASP.NET Core host: controllers and HTTP DTOs, JWT Bearer authentication, the permission authorization requirement and handler, error mapping, correlation IDs, health endpoints, and the dependency injection composition root. |

Project references:

```text
Common       -> no project dependencies
Domain       -> no project dependencies
Application  -> Common, Domain
Persistence  -> Application, Domain
Api          -> Application, Persistence
```

Commands and queries use the project's own messaging interfaces (`ICommand`, `ICommandHandler`, `IQuery`, `IQueryHandler`); MediatR is not used. Registration, Login, Refresh, Logout, and Me are the only use cases so far; Me is the first query handler, and handlers are registered explicitly in the API.

```text
SmartProperty/
├── docs/                              Architecture and API contract decisions
├── src/
│   ├── Core/
│   │   ├── SmartProperty.Common/      Results/, Pagination/
│   │   ├── SmartProperty.Domain/      Identity/, Workspaces/
│   │   └── SmartProperty.Application/ Abstractions/, Authentication/Register/, Authentication/Login/, Authentication/Refresh/, Authentication/Logout/, Authentication/Me/, Authorization/
│   ├── Infrastructure/
│   │   └── SmartProperty.Persistence/ Authorization/, Configurations/, Context/, Health/, Repositories/
│   └── Presentation/
│       └── SmartProperty.Api/         Contracts/, Controllers/, Infrastructure/ (Authentication/, Authorization/, Errors/, Http/, Time/)
├── tests/
│   ├── SmartProperty.UnitTests/       Domain/, Application/, TestDoubles/
│   ├── SmartProperty.Api.IntegrationTests/ Infrastructure/, Authentication/, Authorization/
│   └── SmartProperty.Persistence.IntegrationTests/ Infrastructure/, Persistence/, Concurrency/, Authorization/
├── Directory.Build.props              Shared build settings
├── Directory.Packages.props           Central package versions
├── docker-compose.yml                 Local PostgreSQL
├── global.json                        .NET SDK version
└── SmartProperty.sln
```

## Tech Stack

| Component | Version | Defined in |
| --- | --- | --- |
| .NET SDK | `10.0.100` or a later 10.0 SDK (`rollForward: latestFeature`) | `global.json` |
| Target framework | `net10.0` | `Directory.Build.props` |
| ASP.NET Core | .NET 10 shared framework | `SmartProperty.Api.csproj` |
| C# | SDK default for `net10.0` (`LangVersion` is not set) | |
| Microsoft.EntityFrameworkCore | `10.0.11` | `Directory.Packages.props` |
| Microsoft.EntityFrameworkCore.Design | `10.0.11` | `Directory.Packages.props` |
| Microsoft.EntityFrameworkCore.Relational | `10.0.11` | `Directory.Packages.props` (pinned so test projects resolve the same assembly the production projects use) |
| Npgsql.EntityFrameworkCore.PostgreSQL | `10.0.3` | `Directory.Packages.props` |
| Microsoft.AspNetCore.Authentication.JwtBearer | `10.0.11` | `Directory.Packages.props` |
| Microsoft.Extensions.Diagnostics.HealthChecks | `10.0.11` | `Directory.Packages.props` |
| Microsoft.Extensions.Configuration.Abstractions | `10.0.11` | `Directory.Packages.props` |
| xunit | `2.9.3` | `Directory.Packages.props` (test projects only) |
| xunit.runner.visualstudio | `3.1.4` | `Directory.Packages.props` (test projects only) |
| Microsoft.NET.Test.Sdk | `17.14.1` | `Directory.Packages.props` (test projects only) |
| Microsoft.AspNetCore.Mvc.Testing | `10.0.11` | `Directory.Packages.props` (test projects only) |
| Microsoft.Extensions.Configuration | `10.0.11` | `Directory.Packages.props` (test projects only) |
| Testcontainers.PostgreSql | `4.15.0` | `Directory.Packages.props` (persistence integration tests only) |
| PostgreSQL for local development | `postgres:17` image | `docker-compose.yml` |
| PostgreSQL for persistence integration tests | `postgres:17` image, started and removed by Testcontainers | `tests/SmartProperty.Persistence.IntegrationTests` |

All projects enable nullable reference types and build with warnings treated as errors.

## Getting Started

### Prerequisites

- .NET SDK 10.0 (`10.0.100` or later, see `global.json`)
- A PostgreSQL database. The repository's `docker-compose.yml` can run one locally with Docker.

### 1. Configure Settings

The API uses standard ASP.NET Core configuration. `appsettings.json` contains only non-secret defaults.

| Key | Environment variable | Default in `appsettings.json` | Notes |
| --- | --- | --- | --- |
| `ConnectionStrings:Database` | `ConnectionStrings__Database` | empty | Required. The API stops at startup when it is empty. |
| `Jwt:Issuer` | `Jwt__Issuer` | `SmartProperty` | Required |
| `Jwt:Audience` | `Jwt__Audience` | `SmartProperty.Api` | Required |
| `Jwt:SigningKey` | `Jwt__SigningKey` | empty | Required secret of at least 32 bytes |
| `Jwt:AccessTokenLifetime` | `Jwt__AccessTokenLifetime` | `00:15:00` | Positive `TimeSpan` |
| `Jwt:RefreshTokenLifetime` | `Jwt__RefreshTokenLifetime` | `7.00:00:00` | Positive `TimeSpan` |

The API validates the `Jwt` settings at startup and stops if any value is missing or invalid. The token lifetimes are technical defaults, not confirmed business policy.

Keep the signing key and database password out of source control. To generate a strong local signing key in PowerShell:

```powershell
$bytes = New-Object byte[] 64
[System.Security.Cryptography.RandomNumberGenerator]::Create().GetBytes($bytes)
[Convert]::ToBase64String($bytes)
```

Then supply your local secrets in one of two ways. Replace the placeholders in angle brackets with your own values.

**Option A: .NET User Secrets.** User Secrets are read only in the `Development` environment, which the `dotnet run` launch profile sets. The committed project file has no `UserSecretsId`, so run `init` once per clone; it adds one to your local `SmartProperty.Api.csproj`.

```powershell
dotnet user-secrets init --project src/Presentation/SmartProperty.Api/SmartProperty.Api.csproj
dotnet user-secrets set "Jwt:SigningKey" "<strong-local-development-key>" --project src/Presentation/SmartProperty.Api/SmartProperty.Api.csproj
dotnet user-secrets set "ConnectionStrings:Database" "Host=localhost;Port=5432;Database=smart_property;Username=smart_property;Password=<local-postgres-password>" --project src/Presentation/SmartProperty.Api/SmartProperty.Api.csproj
```

**Option B: environment variables** for the current PowerShell session:

```powershell
$env:Jwt__SigningKey = "<strong-local-development-key>"
$env:ConnectionStrings__Database = "Host=localhost;Port=5432;Database=smart_property;Username=smart_property;Password=<local-postgres-password>"
```

### 2. Start PostgreSQL (Optional)

`docker-compose.yml` defines a PostgreSQL 17 database named `smart_property`, owned by user `smart_property` and bound to `127.0.0.1` on port `5432` (set `POSTGRES_PORT` to change it). The compose file requires `POSTGRES_PASSWORD`:

```powershell
$env:POSTGRES_PASSWORD = "<local-postgres-password>"
docker compose up -d --wait
```

Use the same password in `ConnectionStrings:Database`.

### 3. Database Schema

The EF Core model and entity configurations exist, but **no migrations are committed and no seed data exists**. The repository does not yet provide a command that creates the schema or adds workspaces.

As a result:

- The API starts, and `/health/ready` reports `Healthy` as long as the database accepts connections. Readiness does not check tables.
- Registration needs the tables defined by the EF Core model (in the `identity` and `platform` schemas) and at least one row in `platform.workspaces`. Against an empty database, `POST /api/auth/register` returns `500`, although validation (`422`) and malformed-request (`400`) errors still work. Login needs the same tables, and `POST /api/auth/login` also returns `500` against an empty database once validation passes.
- Until migrations and seed data are added, the schema and workspace data must be prepared manually.

### 4. Build and Run

```powershell
dotnet restore SmartProperty.sln
dotnet build SmartProperty.sln --configuration Release
dotnet run --project src/Presentation/SmartProperty.Api/SmartProperty.Api.csproj
```

`dotnet run` uses the `SmartProperty.Api` launch profile: the `Development` environment, listening on `https://localhost:49683` and `http://localhost:49685`. HTTPS uses the ASP.NET Core development certificate; if it is missing, run `dotnet dev-certs https --trust`.

## Health Endpoints

| Endpoint | What it checks | Response |
| --- | --- | --- |
| `GET /health/live` | Only that the API process responds | `200` `Healthy` |
| `GET /health/ready` | That the database accepts a connection (5-second timeout) | `200` `Healthy`, or `503` `Unhealthy` when the database cannot be reached |

Both endpoints return plain text and require no authentication. Readiness does not verify the schema.

## API Error Contract

Errors produced by the API's error handling use one JSON shape:

```json
{
  "code": "validation.failed",
  "message": "Email is required.",
  "status": 422,
  "fieldErrors": null,
  "correlationId": "..."
}
```

- `code` is a stable, machine-readable identifier, and `message` is safe to show to users.
- `fieldErrors` is currently always `null`. A validation failure describes the first failing rule in `message`.
- Unexpected failures return `500` with code `server.unexpected_error` and no internal details.

Responses include an `X-Correlation-ID` header. Send your own `X-Correlation-ID` value (at most 128 characters, no control characters) to trace a request; otherwise the API uses the request's trace identifier. The same value appears in `correlationId` and is added to the server logging scope.

See [docs/api-contract-standard.md](docs/api-contract-standard.md) for the full contract.

## Registration API

`POST /api/auth/register` registers a new user. It is anonymous and is currently the only business endpoint.

Request, sent with `Content-Type: application/json`:

```json
{
  "email": "user@example.com",
  "password": "example-password",
  "firstName": "First",
  "lastName": "Last",
  "workspaceId": "3fa85f64-5717-4562-b3fc-2c963f66afa6"
}
```

`example-password` is a placeholder for this document, not a recommended password. `workspaceId` must be the id of an existing workspace.

Successful response, `201 Created` without a `Location` header:

```json
{
  "userId": "...",
  "status": "Pending",
  "workspaceId": "...",
  "workspaceAccessRequestId": "...",
  "workspaceAccessStatus": "Pending"
}
```

Registration:

- creates a `User` with status `Pending`
- creates the user's `UserCredential`, which stores only the password hash
- creates one `WorkspaceAccessRequest` with status `Pending` for the selected workspace
- saves all three together, or none of them

Registration does **not** create a `WorkspaceMembership`, assign roles or permissions, activate the user, or issue an access token or refresh token.

### Validation

Only the first failing rule is reported.

| Field | Rules |
| --- | --- |
| `email` | Required; at most 320 characters after trimming; exactly one `@` with text on both sides; no whitespace or control characters. Stored trimmed and lower-cased. |
| `password` | Required and not only whitespace; at most 128 characters. Not trimmed. No strength rules yet. |
| `firstName`, `lastName` | Required; at most 100 characters after trimming. |
| `workspaceId` | Required; must not be the empty GUID. |

### Status Codes

| Status | Code | When |
| --- | --- | --- |
| `201` | | The user was registered. |
| `400` | `request.malformed` | The body is empty, is not valid JSON, is not a JSON object, or has a value of the wrong type. |
| `422` | `validation.failed` | A validation rule failed, including a missing field. |
| `404` | `workspaces.not_found` | No workspace exists with the given `workspaceId`. |
| `409` | `users.email_already_exists` | The email is already registered. |
| `500` | `server.unexpected_error` | An unexpected server failure occurred. |

Checks run in this order: request format (`400`), validation (`422`), workspace (`404`), then email (`409`).

Duplicate detection ignores letter case and surrounding whitespace, so `User@Example.com` conflicts with `user@example.com`. If two registrations for the same new email arrive at the same time, the database unique index rejects the later one, and it returns the same `409` as a sequential duplicate.

## Login API

`POST /api/auth/login` signs in an `Active` user. It is anonymous.

Request, sent with `Content-Type: application/json`:

```json
{
  "email": "user@example.com",
  "password": "example-password"
}
```

Successful response, `200 OK`:

```json
{
  "accessToken": "eyJhbGciOiJIUzI1NiIs...",
  "accessTokenExpiresAt": "2026-09-17T12:15:00+00:00",
  "refreshToken": "q3Jx...",
  "refreshTokenExpiresAt": "2026-09-24T12:00:00+00:00"
}
```

- `accessToken` is a JWT to send as `Authorization: Bearer <token>`. It identifies the user only and carries no role, permission, or workspace claims.
- `refreshToken` is an opaque random value. The raw value is returned only in this response; the database stores only its hash.
- Expirations use the default lifetimes (15 minutes and 7 days), which are technical defaults.
- Only `Active` users receive tokens. Each login creates a new refresh token and leaves earlier ones valid.
- If the stored password hash uses outdated parameters, it is upgraded during a successful login and saved together with the new refresh token.

Validation uses the registration rules for `email` and `password`: both are required, `email` is at most 320 characters with exactly one `@`, and `password` is at most 128 characters.

### Status Codes

| Status | Code | When |
| --- | --- | --- |
| `200` | | Login succeeded. |
| `400` | `request.malformed` | The body is empty, is not valid JSON, is not a JSON object, or has a value of the wrong type. |
| `422` | `validation.failed` | A validation rule failed, including a missing field. |
| `401` | `authentication.invalid_credentials` | The email or password is wrong. |
| `403` | `authentication.account_unavailable` | The password is correct, but the account is not `Active`. |
| `500` | `server.unexpected_error` | An unexpected server failure occurred. |

The `401` response is identical whether the email is unknown, the password is wrong, or the stored credential is missing or unusable: `Invalid email or password.`. The account status is checked only after the password is verified, so a wrong password always returns `401`. The `403` message, `This account is not currently allowed to sign in.`, is the same for `Pending`, `Suspended`, and `Deactivated` accounts.

## Refresh API

`POST /api/auth/refresh` exchanges a refresh token for a new token pair. It requires no `Authorization` header: the refresh token is the credential, and the access token it replaces may already have expired.

```json
{
  "refreshToken": "q3Jx..."
}
```

Successful response, `200 OK`:

```json
{
  "accessToken": "eyJhbGciOiJIUzI1NiIs...",
  "accessTokenExpiresAt": "2026-09-17T12:30:00+00:00",
  "refreshToken": "8ZpK...",
  "refreshTokenExpiresAt": "2026-09-24T12:15:00+00:00"
}
```

- **Rotation is single use.** A successful refresh revokes the token you sent and returns a new one. Replay the old token and you get `401` — store the new `refreshToken` from every response and discard the previous one.
- The new raw refresh token is returned only in this response; the database stores only its SHA-256 hash, never the raw value.
- The new access token carries the same identity-only claims as a login token: no role, permission, or workspace claims.
- Only `Active` users can refresh. Other refresh tokens belonging to the same user are untouched, so multiple sessions stay valid.
- **Concurrent refreshes with the same token are safe.** If several requests send the same token at once, exactly one receives `200` and the rest receive `401`; only one replacement token is ever created.
- `refreshToken` is required and capped at 512 characters. It is matched exactly — never trimmed — so send it back byte for byte.

### Status Codes

| Status | Code | When |
| --- | --- | --- |
| `200` | | The token was rotated. |
| `400` | `request.malformed` | The body is empty, is not valid JSON, is not a JSON object, or has a value of the wrong type. |
| `422` | `validation.failed` | A validation rule failed, including a missing field. |
| `401` | `authentication.invalid_refresh_token` | The token is unknown, expired, revoked, already rotated, or lost a concurrent refresh. |
| `403` | `authentication.account_unavailable` | The token is valid, but the account is not `Active`. |
| `500` | `server.unexpected_error` | An unexpected server failure occurred. |

The `401` body is identical in every case: `Invalid or expired refresh token.`. It never says which case occurred. A `401` here means the client must sign in again.

## Logout API

`POST /api/auth/logout` revokes the refresh token you send it. No `Authorization` header is required: the refresh token is the credential being destroyed, and your access token may already have expired.

```json
{
  "refreshToken": "<refresh-token>"
}
```

Success: **`204 No Content`**, with no response body.

- **The endpoint is idempotent.** Logging out twice, sending an unknown token, or sending an expired or already-revoked token all return `204` as well. The response never says which case occurred, so retrying is always safe.
- **Only the token you send is revoked.** Other sessions for the same user stay signed in. There is no logout-all.
- The raw token is never persisted — it is hashed to find the stored row, and only the hash is ever stored.
- **Your access token keeps working until it expires.** Access tokens are stateless and are not revoked, so `GET /api/auth/me` can still succeed with the old access token for the rest of its 15-minute lifetime. What logout stops is renewal: the refresh token can no longer be exchanged.
- **The frontend must clear both tokens locally** after a `204`. Server-side revocation alone does not make the access token unusable.

### Status Codes

| Status | Code | When |
| --- | --- | --- |
| `204` | | The token was revoked, or there was nothing to revoke. |
| `400` | `request.malformed` | The body is empty, is not valid JSON, is not a JSON object, or has a value of the wrong type. |
| `422` | `validation.failed` | `refreshToken` is missing, blank, or longer than 512 characters. |
| `500` | `server.unexpected_error` | An unexpected server failure occurred. A retry is safe. |

Logout never returns `401`, `403`, `404`, or `409`. Unlike Refresh — which must prove a usable credential before issuing new ones, and answers `401` when it cannot — logout only destroys the credential it was given, so a token it cannot use is already in the state logout wanted.

## Me API

`GET /api/auth/me` returns the signed-in user's current profile. It is the first protected endpoint: a valid access token is required.

```text
GET /api/auth/me
Authorization: Bearer <access-token>
```

Successful response, `200 OK`:

```json
{
  "userId": "0b4f2f4e-1f0e-4f2a-9a5e-6d4b8f1c2a30",
  "email": "user@example.com",
  "firstName": "First",
  "lastName": "Last"
}
```

- The identity comes from the access token alone. Supplying a `userId` or `email` in the query string, headers, or a body changes nothing.
- The profile is read from the database on every call, so a name changed elsewhere shows up immediately. The access token itself carries no profile data.
- Only `Active` users succeed. If the account became `Pending`, `Suspended`, or `Deactivated` after the token was issued, the call returns `403` even though the token is still valid.
- Nothing is written: no last-login timestamp, no session or token changes.
- No token, password, credential, role, permission, or workspace information is returned.

### Status Codes

| Status | Code | When |
| --- | --- | --- |
| `200` | | The profile was returned. |
| `401` | `authentication.unauthorized` | No token, or a token that failed validation; also a valid token whose user no longer exists. |
| `403` | `authentication.account_unavailable` | The token is valid, but the account is not `Active`. |
| `500` | `server.unexpected_error` | An unexpected server failure occurred. |

## Protected Endpoint Errors

Every protected endpoint uses the standard error body for authentication and authorization failures — never an empty response.

**`401 authentication.unauthorized`** — one identical response for a missing `Authorization` header, a non-Bearer scheme, a malformed or expired token, a bad signature, a wrong issuer or audience, a disallowed algorithm, and any invalid subject. The body never says which check failed:

```json
{
  "code": "authentication.unauthorized",
  "message": "Authentication is required.",
  "status": 401,
  "fieldErrors": null,
  "correlationId": "..."
}
```

Challenge responses carry `WWW-Authenticate: Bearer`, with no `error_description` or other detail about the failure.

**`403 authorization.forbidden`** — the caller is authenticated but lacks permission for the resource. It names no policy, role, or permission. The requirement and handler that produce it exist (see [Authorization Model](docs/authorization-model.md)), but no production endpoint applies them yet, so nothing currently returns it.

Note that `403 authentication.account_unavailable` is a different thing: it means the account itself may not be used, not that it lacks a permission.

## Authentication Infrastructure

Login issues access and refresh tokens (see [Login API](#login-api)), Refresh rotates them (see [Refresh API](#refresh-api)), and `GET /api/auth/me` (see [Me API](#me-api)) is the first endpoint that requires one. Role/permission modeling, persisted permission resolution, and the reusable ASP.NET authorization bridge are implemented; production endpoint permission enforcement remains pending.

- **Password hashing:** ASP.NET Core Identity's `PasswordHasher<TUser>` (PBKDF2 with a per-password salt). Only the hasher is used, not Identity's stores, managers, or tables.
- **JWT Bearer validation:** tokens must be signed with HS256 using `Jwt:SigningKey` and pass signature, issuer, audience, and lifetime validation, with 30 seconds of allowed clock skew.
- **Subject:** a token authenticates only if it has exactly one `sub` claim that holds a non-empty GUID in the 36-character hyphenated format.
- **Access tokens** identify the user only: `sub`, `jti`, and the standard issuer, audience, and time claims. They carry no role, permission, or workspace claims.
- **Refresh tokens** are opaque random values (64 bytes, Base64Url encoded). The persistence model stores only their SHA-256 hash, never the raw token.
- **Current user:** `ICurrentUser` exposes the user id from a validated access token.

See [docs/authentication-model.md](docs/authentication-model.md) for details.

## Workspace and Access Model

- Workspaces are data in `platform.workspaces`, not enum values.
- A user can be a member of several workspaces and can hold several roles within a workspace.
- Selecting a workspace during registration only creates a `Pending` access request; it grants no access.
- Approving requests, creating memberships, and assigning roles are future workflows. The domain model can approve or reject a request, but no use case or endpoint does so yet.
- Membership in a workspace named Admin Workspace grants no platform-wide admin rights. Platform Admin access will require an explicit platform role.

See [docs/identity-access-model.md](docs/identity-access-model.md) for details.

## Notes for Frontend and QA

Available now:

| Endpoint | Notes |
| --- | --- |
| `GET /health/live` | Responds whenever the API is running. |
| `GET /health/ready` | Returns `200` only when the database is reachable. |
| `POST /api/auth/register` | Needs the database schema and an existing workspace id. |
| `POST /api/auth/login` | Needs the database schema and an `Active` user. Frontend and QA can test sign-in with it. |
| `POST /api/auth/refresh` | Needs a refresh token from a login or an earlier refresh. Frontend and QA can test session renewal with it. |
| `GET /api/auth/me` | Needs an access token from a login or refresh. Frontend and QA can test the protected-endpoint flow with it. |
| `POST /api/auth/logout` | Needs a refresh token. Frontend and QA can test sign-out with it; remember that the access token stays valid until it expires. |

Not available yet: access request approval and any screen that depends on authorization. Routes for those features do not exist yet.

Integration notes:

- There is no endpoint for listing workspaces yet, so a workspace id must be taken from the database.
- No CORS policy is configured, so browsers block calls from a frontend served on a different origin.
- There is no Swagger or OpenAPI UI.
- JSON property names are camelCase, and identifiers are GUID strings.
- Registered users stay `Pending`; the API cannot activate them yet, so Login returns `403` for them. To test a successful login, set the user's `status` to `Active` in `identity.users` in a local or test database.

## Testing

Permanent automated tests are implemented as a foundation. Run them with:

```bash
dotnet build SmartProperty.sln --configuration Release
dotnet test SmartProperty.sln --configuration Release --no-build
```

A working Docker daemon is required, and only by `tests/SmartProperty.Persistence.IntegrationTests`. The other
two projects run entirely in process on a clean checkout, with no database, User Secret, or environment
variable.

| Project | Owns |
| --- | --- |
| `tests/SmartProperty.UnitTests` | Domain invariants, the Application authorization contracts, and the Login, Refresh, Logout, and Me handlers against hand-written test doubles. |
| `tests/SmartProperty.Api.IntegrationTests` | The real ASP.NET Core host over `WebApplicationFactory<Program>`: JWT Bearer authentication, the permission authorization bridge, the standardized `401` and `403` bodies, and correlation IDs. |
| `tests/SmartProperty.Persistence.IntegrationTests` | The real `ApplicationDbContext` over the real Npgsql provider against real PostgreSQL: schema creation, constraints, repository round trips, the unit of work, refresh-token optimistic concurrency, same-token refresh and refresh/logout races, and `PermissionChecker` resolution against persisted access state. |

### The persistence suite and Docker

`tests/SmartProperty.Persistence.IntegrationTests` runs against an **ephemeral PostgreSQL 17 container that
Testcontainers starts and removes for the run** — a random host port, no named volume, no bind mount, no fixed
`5432` mapping, and credentials generated on the spot.

It does **not** use the development database. It never reads a connection string from configuration, User
Secrets, an environment variable, or `localhost`; it never reuses, reconfigures, or removes any container,
volume, or network that already exists; and it issues no `docker` command of its own. Running it leaves the
`docker-compose.yml` PostgreSQL untouched.

Each test gets its own database on that one container, copied from a template built once with
`Database.EnsureCreated()` against the current EF model. That verifies the model works on PostgreSQL. It
verifies nothing about production migrations, upgrade paths, rollback, migration ordering, or the operational
safety of a schema change — the repository has no migrations, and this suite adds none.

If Docker is unavailable the persistence project fails. It never falls back to EF Core InMemory, to SQLite, or
to a local database.

Current permanent coverage:

- Domain and Application unit tests.
- Authentication use-case regression: the login dummy-verification timing hardening, non-active account
  statuses, password re-hash on login, refresh rotation and its invalid paths, and logout idempotency.
- Authorization bridge regression: `PermissionRequirement`, `PermissionAuthorizationHandler`, resource typing,
  AND semantics across multiple requirements, cancellation, and infrastructure-failure propagation.
- API `401` and `403` integration against the real pipeline, including that a denial never names the
  permission involved.
- Correlation-ID integration on both `401` and `403`.
- Real PostgreSQL schema creation from the current EF model, and the unique indexes, check constraints, and
  restrictive foreign keys the configurations declare.
- Real unique-constraint and foreign-key rejections, and how each one reaches the Application layer today.
- Real refresh-token optimistic concurrency across two independent contexts, and its translation into
  `ConcurrencyConflictException` at the persistence boundary.
- A real same-token refresh race and a real refresh/logout race, each competing call in its own scope, with
  exactly one winner and at most one replacement token.
- `PermissionChecker` against persisted access state: platform and workspace grants, workspace isolation,
  platform/workspace scope isolation, non-active users, exact-case permission codes, one database command per
  check, and an infrastructure failure propagating instead of becoming a denial.

Still deferred:

- Migration-backed schema tests and the migration lifecycle. No migrations exist yet.
- Full end-to-end business flows over HTTP against a real database.

This is a foundation, not full coverage, and not a security-coverage or production-readiness claim. See
[Testing Strategy](docs/testing-strategy.md).

## Known Limitations

These are planned work items, not defects in the implemented features.

- EF Core migrations and seed data are pending, so the database must be prepared manually.
- No password strength policy has been decided; passwords are only required and limited to 128 characters.
- Email verification is pending.
- Rate limiting is pending.
- Logout revokes only the refresh token presented to it. A user's other sessions stay active, there is no logout-all or device management, and an access token issued before logout keeps working until it expires. Refresh likewise rotates one token at a time, and a replayed token does not revoke the tokens issued after it (refresh-token families are not implemented).
- The standardized `403 authorization.forbidden` response exists and the permission bridge produces it correctly, but no production endpoint applies a `PermissionRequirement`, so nothing returns it in practice. A reusable bridge is not a protected API.
- Login performs one password verification on every rejected attempt, including an unknown email and a missing credential, so response time no longer reveals whether an email is registered. This is timing hardening, not a constant-time guarantee: a corrupted stored hash can still fail faster, and registration still reveals a taken email through `409`. Rate limiting and account lockout are pending.
- Authorization has a documented model, framework-neutral contracts, a persistence resolver that answers permission questions from current data — including the active-user rule and platform/workspace isolation — and a reusable ASP.NET requirement and handler that call it. What is missing is the last step: no production endpoint declares a permission, and no workspace id is extracted from a route. See [Authorization Model](docs/authorization-model.md).
- Authentication is enforced on `GET /api/auth/me` only. Its JWT Bearer `401` challenge and `403` authorization responses already use the standard error body, and presented-token logout is implemented at `POST /api/auth/logout`; logout-all, device and session management, and the broader role, permission, and workspace authorization remain pending.
- `405 Method Not Allowed` responses (empty body) and `415 Unsupported Media Type` responses (framework `ProblemDetails` body) do not use the standard error contract yet.
- Validation errors do not return structured `fieldErrors` yet.

## Security Notes

- Never commit JWT signing keys or database credentials. Use User Secrets or environment variables locally, and secure configuration in deployed environments.
- Passwords are stored only as hashes, never as plaintext.
- Raw refresh tokens are never persisted; Login and Refresh return the raw token to the client and store only its hash.
- Refresh tokens are single use. A successful refresh revokes the presented token in the same save that inserts its replacement, so a replayed token returns `401`, and concurrent refreshes with one token yield exactly one winner.
- Logout revokes the presented refresh token only. It is idempotent, so an unknown, expired, or already revoked token still returns `204` and the response never reveals the token's state. Access tokens are stateless and are not revoked: one issued before logout stays valid until it expires, so clients must discard both tokens locally.
- Refresh returns the same `401` response whether the token is unknown, expired, revoked, replayed, or lost a concurrent rotation, and never reveals the account status behind its `403`.
- Login returns the same `401` response for an unknown email, a wrong password, or an unusable stored credential, and checks the account status only after the password is verified.
- API error responses do not include stack traces, SQL, or database constraint names. Those details go only to server logs.
- PostgreSQL-specific details, such as error codes and constraint names, stay inside `SmartProperty.Persistence`.
- The backend is not yet production-ready from a security standpoint: authorization, rate limiting, email verification, and a password policy are still pending.

## Documentation

| Document | Description |
| --- | --- |
| [API Contract Standard](docs/api-contract-standard.md) | Shared HTTP conventions: identifiers, dates, pagination, errors, status codes, and correlation IDs. |
| [Identity Access Model](docs/identity-access-model.md) | Workspaces, memberships, roles, permissions, and the access request flow. |
| [Authentication Model](docs/authentication-model.md) | Credentials, password hashing, tokens, configuration, the commit boundary, registration, and login. |
| [Authorization Model](docs/authorization-model.md) | Authorization scopes, permission contracts, role-to-permission paths, fail-closed rules, how permission resolution queries them, and the ASP.NET authorization bridge. No endpoint enforcement yet. |
| [Testing Strategy](docs/testing-strategy.md) | What each test project owns, which one needs Docker and what it is not allowed to touch, what `EnsureCreated` does and does not prove, and which coverage is still deferred. |
