# SmartProperty Backend

Clean Architecture foundation through **Step 03: PostgreSQL and EF Core**.

## Current solution structure

```text
SmartProperty/
├── src/
│   ├── Core/
│   │   ├── SmartProperty.Common/
│   │   ├── SmartProperty.Domain/
│   │   └── SmartProperty.Application/
│   ├── Infrastructure/
│   │   └── SmartProperty.Persistence/
│   └── Presentation/
│       └── SmartProperty.Api/
├── tests/
├── Directory.Build.props
├── Directory.Packages.props
├── global.json
└── SmartProperty.sln
```

## Dependency rule

```text
Common        -> no project dependency
Domain        -> no project dependency
Application   -> Domain + Common
Persistence  -> Application + Domain
Api           -> Application + Persistence
```

The inner layers never depend on outer layers. In particular, `Domain` has no EF Core, PostgreSQL, ASP.NET Core, JWT, OpenAPI, or persistence package dependency.

## Intentionally not implemented yet

The following belong to later steps and are intentionally absent here:

- Migrations
- Migrator / Seeder projects
- Authentication / JWT / ASP.NET Identity
- Swagger / OpenAPI setup
- Redis / storage / external integrations
- Business entities and use cases
- Aspire AppHost / ServiceDefaults
- Test projects

## Local verification

Requires .NET 10 SDK.

```bash
dotnet restore
dotnet build
```

## Local PostgreSQL and API

Requires Docker Desktop with Linux containers and .NET 10 SDK. Run these PowerShell
commands from the solution root, in the same terminal:

```powershell
$env:POSTGRES_PASSWORD = 'local-development-only'
$env:POSTGRES_PORT = '5432'
$env:ConnectionStrings__Database = "Host=localhost;Port=$env:POSTGRES_PORT;Database=smart_property;Username=smart_property;Password=$env:POSTGRES_PASSWORD"
docker compose config --quiet
docker compose up -d --wait
docker compose ps
dotnet build SmartProperty.sln --configuration Release
dotnet run --project src/Presentation/SmartProperty.Api --configuration Release --no-build --no-launch-profile --urls http://localhost:5080
```

The example password is disposable local development data, never a production
credential. Compose requires `POSTGRES_PASSWORD`; the API requires
`ConnectionStrings__Database` (configuration key `ConnectionStrings:Database`).
No password is stored in appsettings. Missing or blank connection strings cause a
clear startup failure, including when running EF tooling. Production supplies
the connection string through the deployment's secret configuration.

From another terminal:

```powershell
Invoke-WebRequest http://localhost:5080/health/live
Invoke-WebRequest http://localhost:5080/health/ready
```

Liveness checks only the application process. Readiness resolves the scoped
`ApplicationDbContext` and checks database connectivity, with a five-second health
check timeout. It returns HTTP 200 when reachable and HTTP 503 otherwise, without
exposing connection details in the response. Readiness does not verify future
schema/migration currency. No PostgreSQL-specific health-check package is needed;
the check uses EF Core and Microsoft's general health-check infrastructure.

Compose runs PostgreSQL 17, database/user `smart_property`, a persistent
`postgres_data` named volume, a `pg_isready` health check, and `unless-stopped`
restart policy. The port binds only to `127.0.0.1` and defaults to 5432; change
`POSTGRES_PORT` before running the commands if that port is occupied.
`docker compose stop` stops the database while retaining its data. Initialization
credentials apply only to a new volume; changing the environment variable does
not change an existing database password. The Compose database user is for local
development; production should use separately provisioned least-privilege roles.

## Persistence and migrations

`ApplicationDbContext` is sealed, has no business DbSets, and discovers mappings
from the Persistence assembly. EF Core 10.0.11 and Npgsql EF provider 10.0.3 use
central package versions. Persistence owns database implementation and generic
configuration/health-check dependencies. Api retains only the private EF Design
tooling reference required when it is the startup project; Common, Domain, and
Application have no database packages.

To inspect the context without creating a migration, with the connection-string
environment variable above still set:

```powershell
dotnet tool install --global dotnet-ef --version 10.0.11
dotnet ef dbcontext info --project src/Infrastructure/SmartProperty.Persistence --startup-project src/Presentation/SmartProperty.Api --configuration Release
```

If the tool is already installed, use `dotnet tool update --global dotnet-ef
--version 10.0.11` instead. After Property Core supplies the first real model, the
first meaningful migration can be created using:

```powershell
dotnet ef migrations add InitialPropertyCore --project src/Infrastructure/SmartProperty.Persistence --startup-project src/Presentation/SmartProperty.Api --output-dir Migrations
```

Do not run the migration command for the current empty model. No empty migration,
`EnsureCreated`, or startup `Migrate` is included. Production migrations will run
through a future dedicated SmartProperty.Migrator deployment step. Neither that
project nor a Seeder is created in Step 03.

## Future mapping conventions and data guardrails

- Database: `smart_property`. Future domain schemas: `identity`, `property`,
  `document`, `sales`, `rental`, `development`, `investment`, `finance`, `workflow`,
  and `governance`. No empty schemas or business tables are created now.
- Use explicit Fluent API mappings in Persistence for snake_case database
  identifiers, including tables, columns, keys, indexes, and constraints. Examples:
  `properties`, `property_ownerships`, `verification_status`, `created_at`.
  C# stays PascalCase. Automatic snake_case conversion is not installed; future
  mappings must name identifiers explicitly and review generated migrations.
- Internal IDs use Guid/UUID; business references may be separate readable values.
- Authoritative money uses decimal/numeric with explicit precision and scale.
- Store timestamps unambiguously, primarily UTC (`timestamp with time zone` for
  instants, supplied as UTC values).
- Preserve historical/legal/financial records; do not rely on destructive deletes
  or a generic `IsDeleted` flag for business state.
- Configure sensitive relationships deliberately; do not blindly accept cascade
  delete defaults. Database constraints form the final integrity boundary.
- Add concurrency protection for approvals, offers, payments, and state transitions
  when those models exist. JSONB must not replace a relational domain model.
