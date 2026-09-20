# SmartProperty

SmartProperty is the backend foundation for a multi-workspace property platform. It is an ASP.NET Core API on .NET 10 that follows Clean Architecture and stores data in PostgreSQL through EF Core.

The backend currently provides shared API contracts, an identity foundation, the workspace and access model, authentication infrastructure, and three business workflows: user registration, login, and refresh-token rotation. The current-user endpoint, logout, and authorization are not implemented yet.

## Current Status

| Area | Status | Notes |
| --- | --- | --- |
| API error contract and correlation IDs | Implemented | Standard error body and `X-Correlation-ID` header |
| Health checks | Implemented | `GET /health/live` and `GET /health/ready` |
| User identity (`User`, `UserCredential`) | Implemented | New users start as `Pending` |
| Workspaces and workspace access requests | Implemented | Workspaces are database records; access requests start as `Pending` |
| Memberships, roles, and permissions | Data model only | Domain entities, EF Core mappings, and repositories; no workflows use them |
| Password hashing | Implemented | ASP.NET Core Identity password hasher |
| JWT access tokens | Implemented | Issued by Login; bearer validation exists, but no endpoint requires a token yet |
| Refresh tokens | Implemented | Issued by Login, persisted as hashes, and rotated single-use by `POST /api/auth/refresh` |
| Current user (`ICurrentUser`) | Implemented | Reads the user id from a validated access token; no endpoint uses it yet |
| Commit boundary (`IUnitOfWork`) | Implemented | One save per use case |
| `POST /api/auth/register` | Implemented | Creates `Pending` users |
| `POST /api/auth/login` | Implemented | Active users only; returns access and refresh tokens |
| `POST /api/auth/refresh` | Implemented | Active users only; single-use rotation returning a new token pair |
| Me and Logout endpoints | Not implemented | |
| Authorization policies, permission enforcement, and workspace authorization | Not implemented | |
| Access request approval and role assignment workflows | Not implemented | |
| Password policy, email verification, and rate limiting | Not implemented | |
| MFA, password reset, and account lockout | Not implemented | |
| EF Core migrations and seed data | Not implemented | See [Database Schema](#3-database-schema) |
| Automated tests | Not implemented | `tests/` is a placeholder |

## Architecture

The solution follows Clean Architecture. The inner layers do not depend on web, database, or token frameworks.

| Project | Responsibility |
| --- | --- |
| `SmartProperty.Domain` | Entities and their invariants: users, credentials, refresh tokens, workspaces, access requests, memberships, roles, and permissions. No framework dependencies. |
| `SmartProperty.Common` | Shared `Result` and `Error` types and pagination primitives. No framework dependencies. |
| `SmartProperty.Application` | Abstractions (repositories, `IUnitOfWork`, `IPasswordHasher`, `ITokenProvider`, `ICurrentUser`, `IDateTimeProvider`), command and query contracts, and use cases. No EF Core, ASP.NET Core, or JWT dependencies. |
| `SmartProperty.Persistence` | EF Core with PostgreSQL (Npgsql): `ApplicationDbContext`, entity configurations, repositories, `UnitOfWork`, the database health check, and translation of recognized PostgreSQL unique-constraint violations and refresh-token concurrency conflicts into provider-neutral exceptions. |
| `SmartProperty.Api` | ASP.NET Core host: controllers and HTTP DTOs, JWT Bearer authentication, error mapping, correlation IDs, health endpoints, and the dependency injection composition root. |

Project references:

```text
Common       -> no project dependencies
Domain       -> no project dependencies
Application  -> Common, Domain
Persistence  -> Application, Domain
Api          -> Application, Persistence
```

Commands and queries use the project's own messaging interfaces (`ICommand`, `ICommandHandler`, `IQuery`, `IQueryHandler`); MediatR is not used. Registration, Login, and Refresh are the only use cases so far, there are no query handlers yet, and handlers are registered explicitly in the API.

```text
SmartProperty/
├── docs/                              Architecture and API contract decisions
├── src/
│   ├── Core/
│   │   ├── SmartProperty.Common/      Results/, Pagination/
│   │   ├── SmartProperty.Domain/      Identity/, Workspaces/
│   │   └── SmartProperty.Application/ Abstractions/, Authentication/Register/, Authentication/Login/, Authentication/Refresh/
│   ├── Infrastructure/
│   │   └── SmartProperty.Persistence/ Configurations/, Context/, Health/, Repositories/
│   └── Presentation/
│       └── SmartProperty.Api/         Contracts/, Controllers/, Infrastructure/
├── tests/                             Placeholder; no test projects yet
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
| Npgsql.EntityFrameworkCore.PostgreSQL | `10.0.3` | `Directory.Packages.props` |
| Microsoft.AspNetCore.Authentication.JwtBearer | `10.0.11` | `Directory.Packages.props` |
| Microsoft.Extensions.Diagnostics.HealthChecks | `10.0.11` | `Directory.Packages.props` |
| Microsoft.Extensions.Configuration.Abstractions | `10.0.11` | `Directory.Packages.props` |
| PostgreSQL for local development | `postgres:17` image | `docker-compose.yml` |

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

## Authentication Infrastructure

Login issues access and refresh tokens (see [Login API](#login-api)), and Refresh rotates them (see [Refresh API](#refresh-api)). No endpoint requires authentication yet.

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

Not available yet: current user (`me`), logout, access request approval, and any screen that depends on authorization. Routes such as `/api/auth/me` currently return `404`.

Integration notes:

- There is no endpoint for listing workspaces yet, so a workspace id must be taken from the database.
- No CORS policy is configured, so browsers block calls from a frontend served on a different origin.
- There is no Swagger or OpenAPI UI.
- JSON property names are camelCase, and identifiers are GUID strings.
- Registered users stay `Pending`; the API cannot activate them yet, so Login returns `403` for them. To test a successful login, set the user's `status` to `Active` in `identity.users` in a local or test database.

## Known Limitations

These are planned work items, not defects in the implemented features.

- EF Core migrations and seed data are pending, so the database must be prepared manually.
- No password strength policy has been decided; passwords are only required and limited to 128 characters.
- Email verification is pending.
- Rate limiting is pending.
- Me and Logout are pending. Refresh rotates one token at a time; it does not revoke a user's other sessions, and a replayed token does not revoke the tokens issued after it (refresh-token families are not implemented).
- Login performs one password verification on every rejected attempt, including an unknown email and a missing credential, so response time no longer reveals whether an email is registered. This is timing hardening, not a constant-time guarantee: a corrupted stored hash can still fail faster, and registration still reveals a taken email through `409`. Rate limiting and account lockout are pending.
- Authorization policies and permission enforcement are pending.
- Standard error bodies for `401` and `403` responses produced by JWT Bearer authentication are pending; no endpoint requires authentication yet. Login's own `401` and `403` responses already use the standard error body.
- `405 Method Not Allowed` responses (empty body) and `415 Unsupported Media Type` responses (framework `ProblemDetails` body) do not use the standard error contract yet.
- Validation errors do not return structured `fieldErrors` yet.

## Security Notes

- Never commit JWT signing keys or database credentials. Use User Secrets or environment variables locally, and secure configuration in deployed environments.
- Passwords are stored only as hashes, never as plaintext.
- Raw refresh tokens are never persisted; Login and Refresh return the raw token to the client and store only its hash.
- Refresh tokens are single use. A successful refresh revokes the presented token in the same save that inserts its replacement, so a replayed token returns `401`, and concurrent refreshes with one token yield exactly one winner.
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
