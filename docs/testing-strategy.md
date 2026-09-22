# Testing Strategy

This document describes the permanent automated test suite: which project owns what, what the suite
deliberately does not cover yet, and where the missing coverage is scheduled.

## Permanent test projects

Three projects, split by what they need to run rather than by how fast they are. Only the third one needs
Docker.

### `tests/SmartProperty.UnitTests`

Owns everything that can be decided without a web host.

- **Domain invariants** — `Permission`, `Role`, and `RefreshToken`: code trimming and exact-case comparison,
  platform/workspace scope pairing, and refresh-token activity and revocation rules.
- **Application authorization contracts** — `AuthorizationTarget` and `AuthorizationRequest`: which
  combinations can be constructed at all, and how a permission code is normalized.
- **Application handlers** — the real `LoginCommandHandler`, `RefreshCommandHandler`, `LogoutCommandHandler`,
  and `GetMeQueryHandler`, exercised against hand-written test doubles in `TestDoubles/`.

It references `SmartProperty.Domain`, `SmartProperty.Common`, and `SmartProperty.Application`. It has no web,
EF Core, or JWT dependency.

`SmartProperty.Application` grants it `InternalsVisibleTo`, so the tests assert against the real internal
`LoginErrors`, `RefreshErrors`, and `MeErrors` instances instead of restating their codes as string literals.

### `tests/SmartProperty.Api.IntegrationTests`

Owns everything that needs the ASP.NET Core pipeline.

- **A real in-memory host** — `WebApplicationFactory<Program>` over the production `Program`, keeping real
  routing, `CorrelationIdMiddleware`, JWT Bearer with its `OnTokenValidated`, `OnChallenge` and `OnForbidden`
  events, the authorization stack, MVC model binding, and JSON serialization.
- **Real JWT Bearer authentication** — no fake authentication scheme is installed. Access tokens are issued
  through the production `TokenProvider`, so a 401 assertion exercises the real bearer handler.
- **The authorization bridge** — `PermissionRequirement` and `PermissionAuthorizationHandler`, both over HTTP
  and directly through the real `IAuthorizationService` where resource typing, requirement combination,
  exception propagation, and request cancellation need to be observed.
- **The standardized 401 and 403 responses** — `ApiErrorResponse` code, message, status, and correlation id,
  and the rule that a denial never names the permission, handler, role, or workspace involved.
- **Correlation id** — an inbound `X-Correlation-ID` is echoed in both the response header and the body.
- **A test-only endpoint** — `TestPermissionController`, declared in the test assembly and attached to the host
  through `AddApplicationPart`. It exists only because no production route applies a `PermissionRequirement`
  yet. It is not referenced by `SmartProperty.Api` and cannot reach the published application.

`SmartProperty.Api` grants it `InternalsVisibleTo`, so the tests drive the real internal `PermissionRequirement`,
`PermissionAuthorizationHandler`, `JwtOptions`, `TokenProvider`, `ApiErrorCodes`, and `CorrelationIdFeature`
rather than re-implementing any of them. Nothing is made public for testing.

### `tests/SmartProperty.Persistence.IntegrationTests`

Owns everything that can only be decided by a real database. Added in STEP 05.7B.

- **Real PostgreSQL through the real provider** — the production `ApplicationDbContext` over Npgsql against a
  PostgreSQL server. There is no EF Core InMemory provider, no SQLite, no mocked `DbContext`, and no mocked
  `DbSet` anywhere in the project.
- **Schema creation from the current EF model** — that the model and its configurations can build the two
  schemas, eleven tables, named unique indexes, and named check constraints on PostgreSQL, and that no foreign
  key was created with a cascading delete or update.
- **Constraints, verified by the database** — duplicate user email, refresh-token hash, permission code,
  workspace membership, and user credential, each attempted from a context that did not write the original row.
  Restrictive delete behaviour is exercised on the relationships where an accidental cascade would be most
  damaging.
- **Provider exception translation as it stands today** — `ux_identity_users_email` reaching Application as
  `UniqueConstraintViolationException`, a refresh-token concurrency loss reaching it as
  `ConcurrencyConflictException`, and the other unique violations arriving untranslated as a `DbUpdateException`
  wrapping a `PostgresException`. That asymmetry is recorded, not redesigned here.
- **Real optimistic concurrency** — two independent contexts load the same refresh token and both revoke it.
  The concurrency token is `RefreshToken.RevokedAt`, mapped with `IsConcurrencyToken()`; there is no `xmin` and
  no version column. One writer wins, the stale writer fails.
- **Real races** — two requests rotating the same refresh token through the real `RefreshCommandHandler`, and a
  refresh racing a logout, each competing call in its own service scope with its own context, repositories, and
  unit of work.
- **`PermissionChecker` against persisted state** — platform and workspace resolution, workspace isolation,
  platform/workspace scope isolation, non-active user denial, exact-case permission codes, and the rule that a
  database failure propagates instead of becoming a denial.
- **Query counts** — one database command per `CheckAsync`, for both the platform and the workspace path,
  counted by a test-only `DbCommandInterceptor` reset after seeding.

It references `SmartProperty.Application`, `SmartProperty.Common`, `SmartProperty.Domain`, and
`SmartProperty.Persistence`. Nothing in `SmartProperty.Persistence` is made public or `InternalsVisibleTo` for
it: `UnitOfWork`, the repositories, and `PermissionChecker` are internal, and the tests resolve them from a
container built by the production `AddPersistence` — the same way the API reaches them. The only registration
the tests replace is the `DbContext` one, and only so their interceptors can observe it.

#### Docker, and what it is not allowed to touch

This project is the only one that needs Docker, and it must never be pointed at anything that already exists.

- The server is an **ephemeral Testcontainers container** on image `postgres:17`, matching the major version
  `docker-compose.yml` runs locally. It gets a random host port, no named volume, no bind mount, no fixed
  `5432` mapping, and credentials generated for the run.
- **The development database is not used.** No test reads a connection string from configuration, User Secrets,
  an environment variable, or `localhost`. No existing container, volume, or network is reused, reconfigured, or
  removed, and the suite issues no `docker` command at all — Testcontainers owns the lifecycle and its resource
  reaper owns the cleanup.
- **Isolation is per test.** The schema is built once on a template database with `EnsureCreated`; every test
  then gets its own database copied from that template, so no test can observe another's rows and test order
  cannot matter. Test parallelism is disabled for this assembly only, because all of those databases are created
  through one administrative connection on one server; `SmartProperty.UnitTests` and
  `SmartProperty.Api.IntegrationTests` keep xUnit's default parallelism.
- **No secrets are persisted.** Refresh-token rows carry hashes only. No raw refresh token, no access token, and
  no real password or production secret is written to any test database.
- **No sleeps and no stopwatches.** The races are made deterministic by a test-only EF `SaveChangesInterceptor`
  that holds every competing writer at the moment it is about to save until all of them have arrived. Because a
  handler reads before it writes, arriving there proves the row was already loaded; the barrier then releases
  both into real, competing saves, and PostgreSQL decides the winner. The barrier delays the call, it never
  replaces it.

#### What EnsureCreated does not test

The schema these tests run against comes from the current EF model, not from migrations — the repository has
none, and STEP 05.7B adds none. So this project verifies that the model is expressible on PostgreSQL and behaves
correctly there. It verifies **nothing** about production migrations, upgrade paths, rollback, migration
deployment ordering, or the operational safety of a schema change. That remains future database-lifecycle work.

## Scope of STEP 05.7A: no PostgreSQL, no Docker

The two STEP 05.7A projects run entirely in-process on any machine, with no external dependency. That still
holds; the statements below are about those two projects, not about
`tests/SmartProperty.Persistence.IntegrationTests`.

- **No PostgreSQL.** No test connects to a database, creates a schema, or runs a migration. The test host points
  EF Core at a syntactically valid but deliberately unreachable connection string, because `AddPersistence`
  refuses to start without one; Npgsql connects lazily, so nothing is ever dialled. `IPermissionChecker` — the
  one seam that would otherwise query — is replaced by a fake. No route under test runs a query, and
  `/health/ready` is not exercised.
- **No Docker and no Testcontainers.**
- **No developer machine state.** No User Secrets, no `appsettings.Local`, no environment variable. The JWT
  issuer, audience, signing key, and lifetimes are supplied in-process by `TestJwt`. That signing key is a
  committed, non-secret, test-only value: it signs tokens this host validates in the same process and is never
  deployed.
- **No wall-clock assertions.** Login's timing hardening is asserted as control flow — which verification ran
  and how many times — never with a stopwatch, `Thread.Sleep`, or `Task.Delay`. Domain tests supply every time
  value explicitly.

### Concurrency mapping is application-level only

`RefreshCommandHandlerTests` and `LogoutCommandHandlerTests` drive a `ConcurrencyConflictException` from a fake
unit of work and assert how each handler maps it — `authentication.invalid_refresh_token` for refresh, success
for logout. That proves the handler's catch and its public error. It does **not** prove real PostgreSQL race
behaviour on its own; `tests/SmartProperty.Persistence.IntegrationTests` is what proves that two concurrent
requests presenting the same token behave correctly against the database.

## Delivered by STEP 05.7B

- Real EF Core persistence against PostgreSQL, through the real Npgsql provider.
- Schema creation from the current EF model.
- Database constraints, including unique-constraint conflicts and restrictive delete behaviour.
- Real refresh-token optimistic concurrency: two contexts rotating the same token.
- Refresh and logout races against the database.
- `PermissionChecker` resolution against persisted roles and permissions, including its query counts.
- Workspace and scope isolation verified against real data.

## Still deferred

- Migration-backed schema tests, and everything about the migration lifecycle (see *What EnsureCreated does not
  test* above). The repository has no migrations yet.
- Full end-to-end business flows, and a successful `/api/auth/me` that reads a real user row over HTTP.
- Production permission enforcement on an endpoint, which does not exist yet to be tested.

## Historical scratch harnesses

Throwaway verification scripts and ad-hoc harnesses used while earlier steps were built are not part of the
permanent suite and are not maintained. Only the three projects above are.

## Running the suite

```
dotnet build SmartProperty.sln --configuration Release
dotnet test SmartProperty.sln --configuration Release --no-build
```

Every project must report a non-zero test count and zero failures. The suite is deterministic: repeated runs
without code changes discover the same tests and produce the same result.

`tests/SmartProperty.UnitTests` and `tests/SmartProperty.Api.IntegrationTests` need nothing but the SDK.
`tests/SmartProperty.Persistence.IntegrationTests` needs a working Docker daemon, and nothing else — no local
PostgreSQL, no connection string, no User Secret, no environment variable. Without Docker it fails; it never
silently degrades to an in-memory provider or to a developer database.
