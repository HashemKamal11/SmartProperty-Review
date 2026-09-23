# Staging/production image for SmartProperty.Api.
# Build stage restores and publishes; the runtime stage carries published output only.

FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /source

# Repository-wide build settings first: the SDK pin, the shared MSBuild properties,
# and central package management all take part in restore.
COPY global.json Directory.Build.props Directory.Packages.props ./

# Project files before the source tree, so the restore layer is reused whenever
# only source files change.
COPY src/Core/SmartProperty.Common/SmartProperty.Common.csproj src/Core/SmartProperty.Common/
COPY src/Core/SmartProperty.Domain/SmartProperty.Domain.csproj src/Core/SmartProperty.Domain/
COPY src/Core/SmartProperty.Application/SmartProperty.Application.csproj src/Core/SmartProperty.Application/
COPY src/Infrastructure/SmartProperty.Persistence/SmartProperty.Persistence.csproj src/Infrastructure/SmartProperty.Persistence/
COPY src/Presentation/SmartProperty.Api/SmartProperty.Api.csproj src/Presentation/SmartProperty.Api/

RUN dotnet restore src/Presentation/SmartProperty.Api/SmartProperty.Api.csproj

# Only src/ is copied; tests/ and docs/ never enter the build context of the image.
COPY src/ src/

RUN dotnet publish src/Presentation/SmartProperty.Api/SmartProperty.Api.csproj \
    --configuration Release \
    --no-restore \
    --output /publish

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS final
WORKDIR /app

COPY --from=build /publish ./

# APP_UID is defined by the aspnet base image; running as that non-root user is the
# documented default for .NET containers and works on the unprivileged port 8080.
USER $APP_UID

EXPOSE 8080

ENTRYPOINT ["dotnet", "SmartProperty.Api.dll"]
