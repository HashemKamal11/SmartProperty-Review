# Testing Strategy

SmartProperty has three permanent test projects. Ownership follows the boundary under test: framework-free
logic, the ASP.NET Core application, or the EF Core/PostgreSQL persistence layer.

## Test projects

### `tests/SmartProperty.UnitTests`

Runs entirely in process and needs neither Docker nor PostgreSQL.

It covers:

- Domain invariants for identity, refresh tokens, roles, and permissions.
- Authentication handlers for Login, Refresh, Logout, and Me.
- Authorization request and target contracts.
- Workspace access-request list, approve, and reject handlers.
- Platform Admin bootstrap behavior through framework-neutral test doubles.
- Pagination normalization, maximum page/offset bounds, and reachable-page metadata.

The project references Domain, Common, and Application. It does not use ASP.NET Core, EF Core, a real token
handler, or a database.

### `tests/SmartProperty.Api.IntegrationTests`

Uses `WebApplicationFactory<Program>` over the production host and, for database-backed scenarios, an isolated
PostgreSQL 17 Testcontainers instance. It keeps the real routing, middleware, MVC binding, JWT Bearer handler,
authorization stack, dependency composition, EF migrations, and JSON contracts.

It covers:

- **Authentication** — Register, Login, Refresh, Logout, Me, JWT validation, and standardized protected `401`
  and `403` responses.
- **Authorization** — requirements, the authorization handler, `RequirePermission`, platform-scope enforcement,
  generated OpenAPI security metadata, and failure/cancellation behavior.
- **Workspace access requests** — the production list, approve, and reject routes against real PostgreSQL,
  including activation, membership creation/reuse, absence of automatic role assignment, and reviewer identity.
- **Platform Admin bootstrap** — disabled, invalid, successful, idempotent, and end-to-end permission behavior.
- **Configuration and CORS** — fail-closed defaults, configured origins, invalid origins, and exact example-JWT
  startup validation.
- **Migration and deployment** — migrator composition, initial Workspace provisioning, readiness with current or
  pending migrations, and a fresh-environment smoke path.

`TestPermissionController` remains a test-only route for focused authorization-bridge scenarios. It is not the
only permission coverage: the production workspace access-request controller also declares and exercises
`RequirePermission`.

### `tests/SmartProperty.Persistence.IntegrationTests`

Runs the production `ApplicationDbContext`, Npgsql provider, repositories, unit of work, and permission checker
against an isolated PostgreSQL 17 Testcontainers instance. It never substitutes EF Core InMemory, SQLite, a
mocked `DbContext`, or mocked `DbSet` objects.

It covers:

- Current-model schema, named indexes and constraints, restrictive delete behavior, and repository round trips.
- The committed EF migration path and model/migration consistency.
- Provider-neutral translation of recognized uniqueness and concurrency failures.
- Persisted platform/workspace permission resolution and query counts.
- Refresh-token concurrent rotation and refresh/logout races.
- Workspace access-request concurrent review, including the bounded membership-collision recovery path.
- Database-wide Platform Admin bootstrap serialization and lock release after failure.

## Docker and isolation

Both integration-test projects require a working Docker daemon. Each project creates and owns an ephemeral
PostgreSQL container with a random host port, generated test credentials, no bind mount, and no named volume.
Testcontainers and its resource reaper own cleanup.

The tests do not read a developer connection string, User Secrets, `.env`, or the Compose database. They do not
reuse, reconfigure, or remove existing containers, networks, volumes, or databases. If Docker is unavailable,
the affected suite fails rather than silently changing providers or targets.

Database isolation is per test or scenario. Concurrency tests use explicit barriers/interceptors rather than
sleep-based timing assumptions, so PostgreSQL chooses the winning write after all competitors have reached the
intended boundary.

## Running the suite

```text
dotnet restore SmartProperty.sln
dotnet build SmartProperty.sln --configuration Release --no-incremental
dotnet test SmartProperty.sln --configuration Release --no-build
```

Every project must discover a non-zero test count and report zero failures. Changes to migrations, persistence,
startup configuration, deployment composition, authentication, or authorization should retain coverage at the
lowest useful layer and add an integration assertion where framework or provider behavior matters.
