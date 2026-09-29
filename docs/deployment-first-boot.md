# Fresh Environment Deployment and First Boot

This is the supported first-boot and upgrade procedure for the Compose deployment. Schema changes are owned by
the short-lived `SmartProperty.Migrator` process; normal API processes never apply migrations. Workspace creation
also goes through that process and the existing `Workspace` domain model. No operator `INSERT`, `UPDATE`, or
schema SQL is part of this procedure.

## 1. Configure deployment values

Copy `.env.example` to the ignored `.env` file and replace every `CHANGE_ME` value. At minimum, set a strong
PostgreSQL password and JWT signing key. Do not commit `.env`.

The example `JWT_SIGNING_KEY=CHANGE_ME` value is intentionally shorter than the production minimum and the API
will reject it during startup validation. Compose verifies presence only; replace it with a high-entropy secret
of at least 32 bytes before starting the deployment.

For the first deployment, generate and retain one non-empty GUID, then configure:

```dotenv
INITIAL_WORKSPACE_ENABLED=true
INITIAL_WORKSPACE_ID=<generated-non-empty-guid>
INITIAL_WORKSPACE_NAME=<operator-chosen-workspace-name>

BOOTSTRAP_ENABLED=false
# BOOTSTRAP_PLATFORM_ADMIN_EMAIL is intentionally unset
```

The workspace ID is the stable provisioning identity and is also the ID supplied during first-user registration.
Names are not globally unique. A later migrator run with the same ID and the same trimmed name is a no-op; the
same ID with a different name fails without renaming or creating another workspace.

## 2. Upgrade precheck

Before upgrading a database that might predate migration
`20260927143034_EnforceUniquePlatformRoleNames`, run this read-only query with an appropriately restricted
database client:

```sql
SELECT name, count(*) AS duplicates
FROM identity.roles
WHERE scope = 'Platform'
GROUP BY name
HAVING count(*) > 1;
```

The expected result is zero rows. If rows are returned, stop: operator review and explicit data correction are
required before migration. The application and migrator deliberately do not delete, merge, or rename roles.
For a completely blank database the `identity.roles` table does not exist yet, so this upgrade-only query is not
applicable.

## 3. First start: migrate and create the Workspace

Start the stack:

```sh
docker compose up --build -d
```

Compose enforces this order:

1. PostgreSQL must become healthy.
2. The one-shot migrator applies all pending EF Core migrations, verifies none remain, and ensures the configured
   Workspace.
3. The API starts only after the migrator exits successfully.

Run exactly one `SmartProperty.Migrator` instance per database at a time. Compose already guarantees one
migrator in this normal deployment path; operators must not manually start an additional instance concurrently.
EF/PostgreSQL migration locking and the Workspace primary key reduce data risk, but a second provisioning process
is unnecessary and may fail while competing for the same configured Workspace.

If migration, configuration validation, or workspace provisioning fails, the migrator exits non-zero and Compose
does not start the API. Inspect the migrator logs, correct the cause, and run the deployment again. Do not work
around a failure with manual data writes.

Verify readiness after the API starts:

```sh
curl --fail http://localhost:${API_PORT:-5000}/health/ready
```

Readiness is healthy only when PostgreSQL is reachable and EF reports no pending migrations. `/health/live`
remains independent of the database and migration state.

## 4. Register the intended administrator

With `BOOTSTRAP_ENABLED=false`, register the intended administrator through the normal registration endpoint,
using `INITIAL_WORKSPACE_ID` as `workspaceId`. Registration still creates a `Pending` user, credentials through
the normal password-hashing path, and a pending Workspace access request. It does not grant administration or
activate the user.

No Workspace creation endpoint exists, and bootstrap does not create a user, credentials, or password.

## 5. Second start: elevate that existing user

After registration succeeds, update the deployment configuration:

```dotenv
INITIAL_WORKSPACE_ENABLED=true
INITIAL_WORKSPACE_ID=<the-same-guid-used-on-first-start>
INITIAL_WORKSPACE_NAME=<the-same-name-used-on-first-start>

BOOTSTRAP_ENABLED=true
BOOTSTRAP_PLATFORM_ADMIN_EMAIL=<the-registered-user-email>
```

Restart/redeploy the API through Compose:

```sh
docker compose up -d
```

The migrator is safe to run again and leaves the matching Workspace unchanged. During API startup, the existing
configuration-gated Platform Admin bootstrap finds the registered user, activates the `Pending` account, ensures
the Platform Admin role and its explicit permission, and assigns that role. It refuses unknown, suspended, or
deactivated accounts and never creates credentials.

Verify that the user can log in and reach the intended administration operation.

## 6. Disable bootstrap after success

Immediately return to:

```dotenv
BOOTSTRAP_ENABLED=false
# BOOTSTRAP_PLATFORM_ADMIN_EMAIL may be removed
```

Restart/redeploy normally:

```sh
docker compose up -d
```

Leaving initial Workspace provisioning enabled with the same ID/name is safe and provides a deployment-time
consistency check; disabling it is also safe and causes the migrator to perform migrations only. Platform Admin
bootstrap is separate and must remain disabled after its one-time use.
