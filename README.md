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

The Domain and Application layers are kept independent from database and HTTP implementation details.

## Current Stack

- .NET 10
- ASP.NET Core
- Entity Framework Core
- PostgreSQL
- Npgsql
- Docker

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

The first migration will be created when the Property Core model is implemented.

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

## Next

- Property Core
