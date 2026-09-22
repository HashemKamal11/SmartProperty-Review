# Testing Strategy

This document describes the permanent automated test suite: which project owns what, what the suite
deliberately does not cover yet, and where the missing coverage is scheduled.

## Permanent test projects

Two projects, split by what they need to run rather than by how fast they are.

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

## Scope of STEP 05.7A: no PostgreSQL, no Docker

The suite runs entirely in-process on any machine, with no external dependency.

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
behaviour, and it is not evidence that two concurrent requests presenting the same token behave correctly
against the database.

## Deferred to STEP 05.7B — Persistence & concurrency integration tests

- Real EF Core persistence against PostgreSQL.
- Database constraints, including unique-constraint conflicts.
- Real refresh-token concurrency: two requests rotating the same token.
- Refresh and logout races against the database.
- `PermissionChecker` resolution against persisted roles and permissions, including its SQL and query counts.
- Workspace isolation verified against real data.
- Migration-backed schema tests.
- Full end-to-end business flows, and a successful `/api/auth/me` that reads a real user row.

## Historical scratch harnesses

Throwaway verification scripts and ad-hoc harnesses used while earlier steps were built are not part of the
permanent suite and are not maintained. Only the two projects above are.

## Running the suite

```
dotnet build SmartProperty.sln --configuration Release
dotnet test SmartProperty.sln --configuration Release --no-build
```

Both projects must report a non-zero test count and zero failures. The suite is deterministic: repeated runs
without code changes discover the same tests and produce the same result.
