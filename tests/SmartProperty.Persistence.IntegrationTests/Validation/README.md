# Disposable PostgreSQL Marketplace validation

This Windows PowerShell runner reproduces the two separately approved PostgreSQL
validation batches. It is test infrastructure, not a migration deployment tool.
Running without `-Execute` performs only 55 synthetic safety checks: no Docker,
Git, .NET, database connections or external processes are invoked.

## Prerequisites and approval

- Obtain explicit approval for each runtime invocation. `-Execute` is an execution
  gate, not evidence of approval. Never retry a failed batch automatically.
- Use an isolated checkout at an explicitly reviewed full Git commit, with an
  empty index and no tracked changes. Git must be available on PATH.
- Windows PowerShell 5.1 or compatible PowerShell; Docker Desktop's
  `desktop-linux` context must resolve to
  `npipe:////./pipe/dockerDesktopLinuxEngine`.
- The exact locally cached Linux PostgreSQL 17 image must be
  `sha256:67f41722b7a8cbdb868a44a4995c846eddfdc2973bccb291ce937dce88ad5675`.
  Missing images fail; the runner does not pull or build images.
- Supply absolute local paths to the installed .NET executable and existing NuGet
  package cache. No packages are copied, installed or restored by this runner.
- The integration-test project must already have valid restored assets using that
  cache and Release/net10.0 outputs built from this exact source state. Rebuild
  offline after source changes; an existing DLL alone does not prove freshness.
- No container carrying `smartproperty.validation.run` may remain from an earlier
  run. Recovery requires its own approval.

Managed Testcontainers behavior is unchanged when `SP_MARKETPLACE_PG_MODE` is not
set. The commands below explicitly use external mode and never construct
Testcontainers/Ryuk. Do not substitute a direct full-suite `dotnet test` command
for this no-pull workflow.

## Offline preparation

From the repository root, the synthetic checks require no parameters:

```powershell
& '.\tests\SmartProperty.Persistence.IntegrationTests\Validation\Invoke-DisposablePostgreSql.ps1'
```

Where a new build is separately authorized, use the installed SDK, existing
assets and an ignored local output area. Example (set `$dotnet` and `$packages`
to verified absolute local paths first):

```powershell
$env:DOTNET_CLI_HOME = Join-Path $PWD 'artifacts/postgresql-validation/offline/cli'
$env:TEMP = Join-Path $PWD 'artifacts/postgresql-validation/offline/temp'
$env:TMP = $env:TEMP
$env:NUGET_PACKAGES = $packages
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
$env:DOTNET_SKIP_FIRST_TIME_EXPERIENCE = '1'
$env:DOTNET_GENERATE_ASPNET_CERTIFICATE = 'false'
$env:DOTNET_CLI_WORKLOAD_UPDATE_NOTIFY_DISABLE = 'true'
New-Item -ItemType Directory -Force -Path $env:DOTNET_CLI_HOME,$env:TEMP | Out-Null
& $dotnet build SmartProperty.sln --configuration Release --no-restore -p:NuGetAudit=false
if ($LASTEXITCODE -ne 0) { throw 'Build failed; stop without fixes or retries.' }
```

Missing restore assets require separate local-only preparation approval. Do not
remove `--no-restore`, add network feeds or use `--ignore-failed-sources`.

## Runtime commands — separate approval required

Set these variables explicitly; do not infer the approved commit from whatever
HEAD happens to be present:

```powershell
$approvedCommit = '<reviewed-full-40-character-commit>'
$dotnet = '<absolute-local-path-to-dotnet.exe>'
$packages = '<absolute-local-path-to-installed-NuGet-cache>'
$runner = '.\tests\SmartProperty.Persistence.IntegrationTests\Validation\Invoke-DisposablePostgreSql.ps1'
```

For one approved migration batch:

```powershell
& $runner -Execute -Batch Migrations -ExpectedCommit $approvedCommit -DotnetPath $dotnet -PackagesPath $packages
```

For one independently approved Marketplace batch:

```powershell
& $runner -Execute -Batch Marketplace -ExpectedCommit $approvedCommit -DotnetPath $dotnet -PackagesPath $packages
```

`Migrations` selects only `MigrationTests`. `Marketplace` selects only
`MarketplaceConstraintTests`, `ListingSubmissionPersistenceTests` and
`PublicListingReadRepositoryTests`. Each invocation creates a fresh container.
Tests use `--no-build --no-restore`; the full integration suite is never selected.

### Narrow pre-commit exception

Only while validating the approved checkpoint at base
`575c120c9c3ea9619ef552f735ab94b238a9b7c0`, an explicitly approved invocation may
add `-AllowApprovedPreCommitChanges`. This accepts exactly three tracked changes,
each pinned to its approved byte-level SHA-256:

| Path relative to `tests/SmartProperty.Persistence.IntegrationTests/` | SHA-256 |
| --- | --- |
| `Infrastructure/PostgreSqlFixture.cs` | `CE972C774CCA70DCC6CE6DA4C799AF5827A97789FD4EFEC3C50B35AC21C9F408` |
| `Marketplace/MarketplaceConstraintTests.cs` | `1BB0A449F3EA56E194A9ED8CFDE9E1E4C08EC1825B92AE61DD67902C4393C674` |
| `Persistence/MigrationTests.cs` | `EB5EEBB4FBBF08D247FDB85B6ABC213672D04C590C081FAC5DBC99AAC67FD4CC` |

Only this runner and README may additionally be untracked source files in that
mode. Earlier `.postgres-validation/` evidence may remain but is not read or
needed by this package. After committing, omit the exception and supply the new
approved commit. Do not alter hashes to accommodate unreviewed changes.

## Isolation, evidence and cleanup

The runner preserves the pinned image and `--pull=never`, unique name/run label,
full returned ID, independent cryptographic credentials and loopback-only dynamic
port. It captures creation identity and a CreateNew timestamp before full
storage/endpoint checks. Effective PGDATA must be exactly
`/var/lib/postgresql/data`, backed by independently verified writable tmpfs using
`readlink`, both mountinfo views and `stat`. Bind/volume mounts, nested mounts,
contradictory evidence and process changes fail closed before database access.

Evidence is written under the already ignored
`artifacts/postgresql-validation/runtime-<run-id>/`. The script verifies the
ignore rule before runtime. It captures the expected commit, source SHA-256
inventory, secret-free creation evidence, sanitized test streams and native exit
codes. It rechecks source/Git integrity after execution. No old baseline file is
required. Do not publish raw Docker inspection, process environments or secrets.
Raw TRX is intentionally not produced by the runtime runner because exception
details could contain credentials. The existing sanitized stdout/stderr policy
is preserved.

Cleanup revalidates the exact saved ID, timestamp, name, image, unique owner label
and independent credentials. It does not require successful storage probes or a
running container. Only the exact returned ID can be removed; no `-v`, prune,
name-based deletion or broad cleanup is permitted. Both exact-ID and label
absence are checked afterward. Test, cleanup or integrity failure prevents a
success result. The runner restores its process environment afterward.

If cleanup fails, preserve all evidence and stop. Request a separately approved
read-only exact-ID review against the saved creation command, full ID, timestamp,
image, name and unique label. Do not reconstruct lost independent secrets from
Docker inspection and call that independent verification. If ownership can be
established, obtain explicit exact-ID cleanup authorization; otherwise leave the
resource untouched. Never run the test batch again merely to clean it up.

## Validation evidence and limits

The predecessor local runner recorded two successful isolated PostgreSQL 17 runs:
MigrationTests 5/5 and Marketplace tests 12/12, both with verified exact-ID and
run-label absence after cleanup. The underlying fixture passed 44 synthetic
safety cases; its combined database-independent suite passed 90 cases and the
UnitTests suite passed 718. These results do not substitute for separately
approved runtime validation of this packaged runner.

Remaining gaps include the complete public visibility/reservation matrix,
runtime privacy assertions, multi-row price ordering/pagination, count/page
consistency, data-preserving upgrade scenarios and the future P2B property-lock
coordinator. Do not claim atomic approval/reservation concurrency from these
tests. Docker daemon/image trust and filesystem-check timing limitations remain.
