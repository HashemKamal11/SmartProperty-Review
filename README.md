# SmartProperty

SmartProperty is the backend foundation for a multi-workspace property platform. It is a .NET 10 modular monolith built with ASP.NET Core, Clean Architecture, PostgreSQL, and EF Core.

The backend currently provides JWT authentication, persisted permission authorization, workspace access-request review, a configuration-gated Platform Admin bootstrap, migration-based database provisioning, configurable fail-closed CORS, health checks, and a Docker Compose deployment chain. The dedicated `SmartProperty.Migrator` applies schema changes and can provision the initial Workspace before the API starts.

## Current Status

| Area | Status | Notes |
| --- | --- | --- |
| API error contract and correlation IDs | Implemented | Standard error body and `X-Correlation-ID` header |
| Health checks | Implemented | `GET /health/live`; readiness requires PostgreSQL connectivity and no pending migrations |
| User identity (`User`, `UserCredential`) | Implemented | New users start as `Pending` |
| Workspaces and workspace access requests | Implemented | Registration creates a `Pending` request; authorized reviewers can list, approve, or reject it |
| Memberships, roles, and permissions | Implemented foundation | Approval ensures membership without assigning a workspace role; authorization resolves current persisted grants |
| Password hashing | Implemented | ASP.NET Core Identity password hasher |
| JWT access tokens | Implemented | Issued by Login and Refresh; required by `GET /api/auth/me` |
| Refresh tokens | Implemented | Issued by Login, persisted as hashes, rotated single-use by `POST /api/auth/refresh`, and revoked by `POST /api/auth/logout` |
| Current user (`ICurrentUser`) | Implemented | Reads the user id from a validated access token for Me and review decisions |
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
| ASP.NET permission authorization bridge | Implemented | `RequirePermission` protects the production workspace access-request review controller |
| Production permission enforcement | Implemented for review APIs | Platform permission `workspace.access_requests.review` protects list/approve/reject |
| Platform endpoint enforcement | Implemented for review APIs | Uses current persisted permission data; JWTs remain identity-only |
| Workspace endpoint enforcement | Not implemented | |
| Route/workspace authorization-target resolution | Not implemented | A `workspaceId` filter exists for review, but no request value is yet treated as proof of workspace authorization |
| Access request review | Implemented | Approval activates a `Pending` user and ensures membership; rejection grants nothing; neither assigns a workspace role |
| Platform Admin bootstrap | Implemented | Disabled by default; elevates one existing registered user during a deliberate startup run |
| EF Core migrations and initial Workspace provisioning | Implemented | Dedicated `SmartProperty.Migrator`; see [First Boot](docs/deployment-first-boot.md) |
| CORS | Implemented | Fail-closed base policy; exact origins supplied by development or deployment configuration |
| OpenAPI and Scalar | Development/Staging | Mapped only when the host environment is `Development` or `Staging` |
| Password policy, email verification, and rate limiting | Not implemented | |
| MFA, password reset, and account lockout | Not implemented | |
| Automated tests | Implemented | Unit, API integration, and PostgreSQL persistence/concurrency/deployment suites. See [Testing Strategy](docs/testing-strategy.md) |

## Architecture

The solution follows Clean Architecture. The inner layers do not depend on web, database, or token frameworks.

| Project | Responsibility |
| --- | --- |
| `SmartProperty.Domain` | Entities and their invariants: users, credentials, refresh tokens, workspaces, access requests, memberships, roles, and permissions. No framework dependencies. |
| `SmartProperty.Common` | Shared `Result` and `Error` types and pagination primitives. No framework dependencies. |
| `SmartProperty.Application` | Abstractions (repositories, `IUnitOfWork`, `IPasswordHasher`, `ITokenProvider`, `ICurrentUser`, `IDateTimeProvider`), command and query contracts, and use cases. No EF Core, ASP.NET Core, or JWT dependencies. |
| `SmartProperty.Persistence` | EF Core with PostgreSQL (Npgsql): `ApplicationDbContext`, entity configurations, repositories, `UnitOfWork`, permission resolution (`IPermissionChecker`), the database health check, and translation of recognized PostgreSQL unique-constraint violations and refresh-token concurrency conflicts into provider-neutral exceptions. |
| `SmartProperty.Migrator` | Short-lived deployment process that applies pending migrations, verifies the database is current, and optionally provisions the initial Workspace. |
| `SmartProperty.Api` | ASP.NET Core host: controllers and HTTP DTOs, JWT Bearer authentication, the permission authorization requirement and handler, error mapping, correlation IDs, health endpoints, and the dependency injection composition root. |

Project references:

```text
Common       -> no project dependencies
Domain       -> no project dependencies
Application  -> Common, Domain
Persistence  -> Application, Domain
Migrator     -> Application, Domain, Persistence
Api          -> Application, Persistence
```

Commands and queries use the project's own messaging interfaces (`ICommand`, `ICommandHandler`, `IQuery`, `IQueryHandler`); MediatR is not used. Authentication, workspace access-request review, and Platform Admin bootstrap handlers are registered explicitly at the composition root.

```text
SmartProperty/
├── docs/                              Architecture and API contract decisions
├── src/
│   ├── Core/
│   │   ├── SmartProperty.Common/      Results/, Pagination/
│   │   ├── SmartProperty.Domain/      Identity/, Workspaces/
│   │   └── SmartProperty.Application/ Abstractions/, Authentication/Register/, Authentication/Login/, Authentication/Refresh/, Authentication/Logout/, Authentication/Me/, Authorization/
│   ├── Infrastructure/
│   │   ├── SmartProperty.Persistence/ Authorization/, Configurations/, Context/, Health/, Migrations/, Repositories/
│   │   └── SmartProperty.Migrator/    Migration and initial-Workspace deployment process
│   └── Presentation/
│       └── SmartProperty.Api/         Contracts/, Controllers/, Infrastructure/ (Authentication/, Authorization/, Errors/, Http/, Time/)
├── tests/
│   ├── SmartProperty.UnitTests/       Domain/, Application/, TestDoubles/
│   ├── SmartProperty.Api.IntegrationTests/ Infrastructure/, Authentication/, Authorization/
│   └── SmartProperty.Persistence.IntegrationTests/ Infrastructure/, Persistence/, Concurrency/, Authorization/
├── Directory.Build.props              Shared build settings
├── Directory.Packages.props           Central package versions
├── docker-compose.yml                 PostgreSQL -> Migrator -> API deployment chain
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
| Testcontainers.PostgreSql | `4.15.0` | `Directory.Packages.props` (API and persistence integration tests) |
| PostgreSQL for local development | `postgres:17` image | `docker-compose.yml` |
| PostgreSQL for integration tests | `postgres:17` image, started and removed by Testcontainers | API and persistence integration-test projects |

All projects enable nullable reference types and build with warnings treated as errors.

## Getting Started

### Prerequisites

- .NET SDK 10.0 (`10.0.100` or later, see `global.json`)
- Docker for the Compose deployment and PostgreSQL-backed integration suites, or another PostgreSQL instance for direct API development.

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

### 2. Start PostgreSQL for IDE Development (Optional)

The base `docker-compose.yml` keeps PostgreSQL private to the Compose network. The local-only override publishes it on `127.0.0.1` (set `POSTGRES_PORT` to change the default `5432`). The compose file requires `POSTGRES_PASSWORD`:

```powershell
$env:POSTGRES_PASSWORD = "<local-postgres-password>"
docker compose -f docker-compose.yml -f docker-compose.local.yml up -d --wait postgres
```

Use the same password in `ConnectionStrings:Database`.

### 3. Database Schema and First Boot

EF Core migrations are committed and owned by the dedicated `SmartProperty.Migrator`; normal API processes never apply them. In the Compose deployment, PostgreSQL becomes healthy first, the one-shot migrator applies and verifies migrations and optionally provisions the initial Workspace, and the API starts only after the migrator succeeds.

Use one migrator per database and do not prepare the schema or bootstrap access with manual write SQL. The supported environment setup, initial Workspace provisioning, administrator registration, one-time Platform Admin bootstrap, and shutdown of bootstrap are documented in [Fresh Environment Deployment and First Boot](docs/deployment-first-boot.md).

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
| `GET /health/ready` | Database connectivity and absence of pending EF migrations | `200` `Healthy`, or `503` `Unhealthy` when either check fails |

Both endpoints return plain text and require no authentication. Readiness fails closed when the database cannot be checked or its migration state is not current.

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

`POST /api/auth/register` registers a new user. It is anonymous.

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

Login issues access and refresh tokens (see [Login API](#login-api)), Refresh rotates them (see [Refresh API](#refresh-api)), and `GET /api/auth/me` (see [Me API](#me-api)) requires an access token. Persisted permission resolution and the ASP.NET authorization bridge protect the workspace access-request review endpoints with `workspace.access_requests.review`.

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
- An authorized reviewer can list pending requests and approve or reject them. Approval activates a `Pending` user and ensures a `WorkspaceMembership`; rejection grants nothing.
- Approval does not assign a workspace role. Membership and role assignment remain separate grants.
- Membership in a workspace named Admin Workspace grants no platform-wide admin rights. Platform Admin access requires an explicit platform role and persisted permission assignment.

See [docs/identity-access-model.md](docs/identity-access-model.md) for details.

## Notes for Frontend and QA

Available now:

| Endpoint | Notes |
| --- | --- |
| `GET /health/live` | Responds whenever the API is running. |
| `GET /health/ready` | Returns `200` only when the database is reachable and has no pending migrations. |
| `POST /api/auth/register` | Needs the database schema and an existing workspace id. |
| `POST /api/auth/login` | Needs the database schema and an `Active` user. Frontend and QA can test sign-in with it. |
| `POST /api/auth/refresh` | Needs a refresh token from a login or an earlier refresh. Frontend and QA can test session renewal with it. |
| `GET /api/auth/me` | Needs an access token from a login or refresh. Frontend and QA can test the protected-endpoint flow with it. |
| `POST /api/auth/logout` | Needs a refresh token. Frontend and QA can test sign-out with it; remember that the access token stays valid until it expires. |
| `GET /api/admin/workspace-access-requests` | Permission-protected, paginated review queue; defaults to `Pending`. |
| `POST /api/admin/workspace-access-requests/{id}/approve` | Activates a `Pending` applicant and ensures membership; assigns no workspace role. |
| `POST /api/admin/workspace-access-requests/{id}/reject` | Records rejection without activation, membership, or role assignment. |

Workspace-scoped endpoint authorization remains deferred; the implemented review APIs use a platform-scoped permission.

Integration notes:

- There is no general endpoint for listing workspaces yet. First-boot deployments use the configured stable initial Workspace id.
- CORS is fail-closed when no origins are configured. Development supplies local frontend origins; deployments supply exact origins through configuration. The policy uses neither `AllowAnyOrigin` nor credentials, and empty origins do not affect server-to-server requests.
- OpenAPI and Scalar are mapped in `Development` and `Staging`, and are absent in other environments under the current `Program.cs` check. This is an exposure fact, not a production-safety claim.
- JSON property names are camelCase, and identifiers are GUID strings.
- Registered users stay `Pending` until an authorized reviewer approves their access request or the deliberate one-time Platform Admin bootstrap activates the intended existing account. Do not activate users with manual write SQL.

## Testing

Run the permanent suite with:

```bash
dotnet build SmartProperty.sln --configuration Release
dotnet test SmartProperty.sln --configuration Release --no-build
```

A working Docker daemon is required by both integration-test projects. Each owns an isolated ephemeral
PostgreSQL 17 container through Testcontainers; neither reads developer connection strings, User Secrets, or a
local database. Unit tests remain entirely in process.

| Project | Owns |
| --- | --- |
| `tests/SmartProperty.UnitTests` | Domain invariants and Application handlers for authentication, authorization contracts, pagination, workspace review, and Platform Admin bootstrap. |
| `tests/SmartProperty.Api.IntegrationTests` | The production ASP.NET Core pipeline plus real PostgreSQL for authentication, authorization, review endpoints, CORS/configuration, readiness, migrations, fresh-environment deployment, and migrator composition. |
| `tests/SmartProperty.Persistence.IntegrationTests` | The real EF/Npgsql persistence boundary: schema and migration checks, constraints, repositories, permission resolution, and refresh/review/bootstrap concurrency. |

### Integration suites and Docker

Both integration projects run **ephemeral PostgreSQL 17 containers that Testcontainers starts and removes**.
They use random host ports, no named volumes or bind mounts, and generated test credentials. They do not reuse
the Compose database or any developer database. If Docker is unavailable, the integration suites fail rather
than falling back to EF Core InMemory, SQLite, or local state.

The persistence suite covers both current-model behavior and the committed migration path. The API suite uses
real migrations for endpoint, readiness, migrator, and fresh-environment deployment scenarios.

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
- Workspace access-request listing and review, including concurrent reviewers and membership uniqueness
  recovery.
- Configuration and deployment coverage for CORS, JWT example validation, migrations, readiness, initial
  Workspace provisioning, fresh environments, and migrator composition.
- Platform Admin bootstrap behavior and database-wide concurrency serialization.

This is not a claim of exhaustive business or security coverage. See [Testing Strategy](docs/testing-strategy.md).

## Known Limitations

These are planned work items, not defects in the implemented features.

- No password strength policy has been decided; passwords are only required and limited to 128 characters.
- Email verification is pending.
- Rate limiting is pending.
- Logout revokes only the refresh token presented to it. A user's other sessions stay active, there is no logout-all or device management, and an access token issued before logout keeps working until it expires. Refresh likewise rotates one token at a time, and a replayed token does not revoke the tokens issued after it (refresh-token families are not implemented).
- Login performs one password verification on every rejected attempt, including an unknown email and a missing credential, so response time no longer reveals whether an email is registered. This is timing hardening, not a constant-time guarantee: a corrupted stored hash can still fail faster, and registration still reveals a taken email through `409`. Rate limiting and account lockout are pending.
- Platform permission enforcement is live on workspace access-request review. Workspace-scoped route-to-target authorization is still deferred. See [Authorization Model](docs/authorization-model.md).
- Authentication protects `GET /api/auth/me` and all workspace access-request review routes. Presented-token logout is implemented; logout-all, device, and session management remain pending.
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
- Platform permission authorization is implemented for workspace access-request review. Broader workspace authorization, rate limiting, email verification, and a password policy remain pending.

## Documentation

| Document | Description |
| --- | --- |
| [API Contract Standard](docs/api-contract-standard.md) | Shared HTTP conventions: identifiers, dates, pagination, errors, status codes, and correlation IDs. |
| [Identity Access Model](docs/identity-access-model.md) | Workspaces, memberships, roles, permissions, and the access request flow. |
| [Authentication Model](docs/authentication-model.md) | Credentials, password hashing, tokens, configuration, the commit boundary, registration, and login. |
| [Authorization Model](docs/authorization-model.md) | Authorization scopes, persisted permission resolution, protected review routes, Platform Admin bootstrap, and concurrent-review behavior. |
| [Testing Strategy](docs/testing-strategy.md) | What each test project owns, Docker/Testcontainers isolation, and current coverage. |
| [Fresh Environment Deployment and First Boot](docs/deployment-first-boot.md) | Supported migrations, initial Workspace provisioning, one-time Platform Admin bootstrap, and post-bootstrap shutdown procedure. |
