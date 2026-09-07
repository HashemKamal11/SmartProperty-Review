# SmartProperty

Backend for the Smart Property platform, built with .NET 10 using Clean Architecture.

## Solution Structure

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
├── docker-compose.yml
├── global.json
└── SmartProperty.sln
```

## Dependency Rule

```text
Common       -> no project dependencies
Domain       -> no project dependencies
Application  -> Domain + Common
Persistence  -> Application + Domain
Api          -> Application + Persistence
```

The Domain and Application layers remain independent from database and HTTP implementation details.

## Architecture Approach

The application follows CQRS with separate read and write responsibilities.

```text
Write Side
API -> Command -> Command Handler -> Domain -> EF Core -> PostgreSQL

Read Side
API -> Query -> Query Handler -> Dapper -> PostgreSQL -> Read Model
```

EF Core is used for write-side persistence and aggregate state changes.

Dapper will be introduced with the first real read-side query. It is intentionally not installed yet because there is no business query implementation at this stage.

CQRS abstractions are framework-independent and do not depend on MediatR.

## Current Stack

- .NET 10
- ASP.NET Core
- Clean Architecture
- CQRS
- Entity Framework Core
- PostgreSQL
- Npgsql
- Docker
- Dapper planned for read-side queries

## Common Foundation

`SmartProperty.Common` currently contains shared infrastructure-independent primitives:

```text
Results/
├── Error.cs
├── ErrorType.cs
├── Result.cs
└── ResultOfT.cs

Pagination/
├── PageParameters.cs
└── PagedList.cs
```

The result model provides a consistent way to represent successful operations and expected business failures without coupling the core layers to HTTP status codes.

Pagination is database-agnostic. Database queries are responsible for returning already-paged items and a total count.

## Application Foundation

`SmartProperty.Application` contains framework-independent abstractions used by future application features:

```text
Abstractions/
├── Identity/
│   └── ICurrentUser.cs
├── Time/
│   └── IDateTimeProvider.cs
└── Messaging/
    ├── ICommand.cs
    ├── ICommandOfT.cs
    ├── ICommandHandler.cs
    ├── ICommandHandlerOfT.cs
    ├── IQueryOfT.cs
    └── IQueryHandler.cs
```

These abstractions keep future use cases independent from ASP.NET Core, JWT, EF Core, Dapper, and other infrastructure details.

## Local Setup

### 1. Configure PostgreSQL

From PowerShell:

```powershell
$env:POSTGRES_PASSWORD = 'local-development-only'
$env:POSTGRES_PORT = '5432'

$env:ConnectionStrings__Database = "Host=localhost;Port=$env:POSTGRES_PORT;Database=smart_property;Username=smart_property;Password=$env:POSTGRES_PASSWORD"
```

### 2. Start PostgreSQL

```powershell
docker compose up -d --wait
docker compose ps
```

### 3. Build the Solution

```powershell
dotnet restore
dotnet build SmartProperty.sln --configuration Release
```

### 4. Run the API

```powershell
dotnet run --project src/Presentation/SmartProperty.Api --configuration Release
```

## Health Checks

```text
GET /health/live
GET /health/ready
```

`/health/live` checks that the API process is running.

`/health/ready` also verifies PostgreSQL connectivity.

## Database

The local environment uses PostgreSQL 17 with:

```text
Database: smart_property
User:     smart_property
Port:     5432
```

Database credentials are provided through environment variables and are not stored in `appsettings.json`.

EF Core configuration and PostgreSQL-specific implementation are contained in `SmartProperty.Persistence`.

## EF Core

The current DbContext is:

```text
SmartProperty.Persistence.Context.ApplicationDbContext
```

Entity mappings will be added through `IEntityTypeConfiguration<T>` implementations inside the Persistence project.

No business entities or migrations have been added yet.

The first migration will be created when the first real domain model is implemented.

To verify EF Core design-time configuration:

```powershell
dotnet ef dbcontext info `
  --project src/Infrastructure/SmartProperty.Persistence `
  --startup-project src/Presentation/SmartProperty.Api `
  --configuration Release
```

## Current Status

Completed:

- Solution setup
- Clean Architecture projects
- PostgreSQL local environment
- EF Core integration
- Database dependency injection
- Database readiness health check
- Common Result and Error foundation
- Shared pagination foundation
- Current user and date/time abstractions
- CQRS command/query abstractions

## Next

- Identity & Access
