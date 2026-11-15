# Workspace administration API

Implementation baseline: `2ea085a547378245b5c37e78c9e3bcaa0ec66bf2`.

## Contracts

| Method and route | Platform permission | Success |
| --- | --- | --- |
| GET /api/admin/workspaces?page=1&pageSize=10 | workspace.admin.read | 200 paginated directory |
| GET /api/admin/workspaces/{workspaceId} | workspace.admin.read | 200 detail |
| POST /api/admin/workspaces | workspace.admin.create | 201 detail and Location pointing to administrative detail |

Read and create are independent persisted platform grants. Platform Admin is not a role-name bypass.
Inactive users, missing grants and grants held only through Workspace roles cannot authorize these operations.
The existing permission checker and action-level RequirePermissionAttribute enforce access.

Detail and list items contain exactly `id`, `name`, `createdAt`, `updatedAt`.
The list envelope contains `items`, `page`, `pageSize`, `totalCount`, `totalPages`, `hasNext`, `hasPrevious`.
All stored Workspaces are included regardless of the caller's memberships.
Ordering is database name order, then UUID ascending. Paging defaults to 1/10 and clamps to pages 1–10000,
sizes 1–100. Count and page are separate database reads and do not promise a shared concurrency snapshot.
There are no filters or client-selectable sorts.

POST accepts `{"name":"Workspace name"}` and, optionally, `"admin":{"email","password","firstName","lastName"}`. IDs and UTC timestamps are server-generated.
With `admin`, the response is `id`, `name`, `createdAt`, `updatedAt`, `adminUserId` (null without `admin`). The admin fields follow the registration rules; an existing email returns 409 `users.email_already_exists` and nothing is saved.
Names are trimmed, required, and limited to 1–200 UTF-16 code units after trimming.
Control characters are rejected in the original input, including surrounding tabs/newlines.
Unicode, casing and internal spaces are preserved. Duplicate names are allowed; IDs are authoritative.
Unknown JSON members follow the existing API binding behavior and cannot assign Domain fields or access grants.

Malformed JSON/types/paging return 400 `request.malformed`.
Invalid names return 422 `workspace_admin.validation_failed`.
Authentication/authorization use the existing 401/403 contracts.
Authorized administrative detail returns 404 `workspace_admin.not_found` for absent/empty IDs.
Non-GUID paths produce routing 404. Unexpected persistence errors propagate to the existing generic 500 handler.
Errors use the existing code/message/status/fieldErrors/correlationId envelope.

## Creation and security boundaries

Creation performs one Workspace insert through the repository and UnitOfWork, with one SaveChangesAsync.
Without `admin` it creates no membership, Workspace role, role-permission mapping, access request, user or platform assignment.
With `admin`, the same single save also creates: an Active user and credential, a Workspace-scoped "Workspace Admin" role for the new workspace holding exactly `workspace.read` and `workspace.members.create` (the permission rows are created if absent), the user's membership, and the membership-role assignment. No platform role or platform permission is granted, and no access request is created.
The creator's auth-context memberships remain unchanged. Existing member detail still requires local workspace.read.
The new Workspace immediately appears in anonymous registration options, under the existing public visibility rule.

POST is non-idempotent. No automatic retry, duplicate-name rejection, uniqueness migration, creator-audit schema,
update/delete endpoint, or permission provisioning is included. If the response is lost, do not automatically repeat POST.
Directory inspection can assist an operator but a matching name cannot establish whether a particular request committed.

P-008 approval is unchanged: only the local Member role with exactly workspace.read is granted by its approved workflow.
Marketplace is outside scope.

## Verification

Run the solution Release build and these Release test filters:
- Unit: FullyQualifiedName~Workspaces|FullyQualifiedName~WorkspaceAccessRequests
- API: FullyQualifiedName~Workspaces|FullyQualifiedName~WorkspaceAccessRequest|FullyQualifiedName~AuthContext
- Persistence: FullyQualifiedName~Workspace|FullyQualifiedName~PermissionChecker

API tests use the production permission checker against isolated PostgreSQL.
New coverage includes platform/local separation, role-name-only refusal, inactive users, independent permissions,
OpenAPI security and schemas, pagination, duplicate ordering, name validation, concurrent same-name creation,
201 Location, persistence, public registration visibility, and unchanged access-table counts.
Existing P-008 tests cover approval, Member-role guards and concurrency.

## Staging prerequisites: separate approval

1. Review this isolated diff, build/tests and integration conflicts in shared permission constants/service registration.
2. Confirm the target Staging database and exact intended platform role, its current assignee counts and statuses.
3. Audit both new permission codes, descriptions and existing mappings. Stop on conflicting definitions.
4. Separately authorize a narrow idempotent transaction creating missing definitions and linking only the approved
   permissions to the verified platform role. Descriptions:
   - workspace.admin.read: Read the administrative workspace directory and details.
   - workspace.admin.create: Create workspaces.
5. Preserve pre/post evidence and inserted IDs; verify no unrelated grants, memberships or assignments change.
   No Bootstrap or general permission repair is part of this plan.
6. Authorize deployment separately. No schema migration is required.
7. Verify read/create grant independence using approved accounts. POST smoke tests require separately approved test data.

Declaring constants does not provision permission rows. Deploying without grants fails closed.

## Rollback

With separately authorized deployment rollback, restore the previous API image using the normal release procedure.
No schema rollback is needed. Workspaces created through this API remain valid data and continue to appear in
registration options; code rollback does not undo creation or remove data.
Any rollback of newly provisioned grants requires separate approval and exact inserted-ID evidence.
Do not delete Workspaces or undo memberships/access requests as part of code rollback.
## Validation result for this implementation (2026-11-02)

- Full solution Release build: passed, zero warnings and errors.
- Complete MigrationTests suite: 5 passed, 0 failed, 0 skipped.
- Workspace/P-008 unit filter: 129 passed, 0 failed, 0 skipped.
- Workspace/access-request/AuthContext API filter: 83 passed, 0 failed, 0 skipped.
- Workspace/PermissionChecker persistence filter: 100 passed, 0 failed, 0 skipped.
- The prior final-migration assertion failure was independently reproduced on a pristine export of the approved
  baseline (4 migration tests passed, 1 failed). With separate approval, MigrationTests.cs was added as the
  sole additional allowlisted file. Its final-migration assertion now expects
  20261028072837_AddListingDraftDiscardAudit through a new ListingDraftDiscardAuditMigration constant.
  The original WorkspaceRoleNameMigration constant and all migration/rollback assertions are preserved.
- The corrected test is named
  BlankDatabase_AppliesEveryMigrationIncludingWorkspaceRoleNameInvariantEndingWithListingDraftDiscardAudit.
  It passed in both the complete migration suite and the approved Workspace/PermissionChecker filter.
- All validation commands exited 0. Integration tests used disposable Testcontainers PostgreSQL (postgres:17).
  No Staging connection, existing-database mutation, bootstrap, permission provisioning, commit, push, merge,
  or deployment was performed. Migration files, EF snapshots, schemas, and existing runtime code were not edited.
- Final allowlist audit: exactly 26 files (6 modified, 20 untracked additions), with no outside paths.
- Git diff whitespace check passed.

Validation used `dotnet build SmartProperty.sln --configuration Release`, followed by Release `dotnet test
--no-build` runs. The complete migration-suite filter was `FullyQualifiedName~MigrationTests`; the other
three filters are recorded in Verification above. Build output, command script, test logs, and TRX files
are retained outside the worktree in
`C:\Users\PC\AppData\Local\Temp\sp-workspace-final-validation-20261102`.
The TRX files are `results/migrations.trx`, `results/persistence.trx`, `results/unit.trx`, and `results/api.trx`.

### Worktree isolation audit exception — resolved.

Before/after snapshots covered all 42 registered worktrees, comparing HEAD, Git status, tracked/staged
diffs, and tracked/untracked file hashes. The P-008 release candidate remains clean at
`2ea085a547378245b5c37e78c9e3bcaa0ec66bf2`. Of the 41 other worktrees, 40 were unchanged.
`sp-marketplace-review-read-policy-20261102-01` changed during the validation interval: HEAD remained
at the approved baseline and its staged diff remained unchanged, but these four files changed:

- src/Presentation/SmartProperty.Api/Contracts/Marketplace/Admin/AdminReviewMapper.cs
- tests/SmartProperty.Api.IntegrationTests/Marketplace/Admin/AdminReviewContractPrivacyTests.cs
- tests/SmartProperty.Api.IntegrationTests/Marketplace/Admin/AdminReviewMapperTests.cs
- tests/SmartProperty.UnitTests/Application/Marketplace/ListingSubmissions/Review/GetReviewQueueQueryHandlerTests.cs

The subsequent read-only reconciliation confirmed that the four changed paths matched the authorized Marketplace Review Read Policy work independently reported by Claude. The Marketplace worktree matched the recorded after-snapshot, the Workspace Administration candidate retained its validated source and test hashes and exact 26-file inventory, and P-008 remained clean. The concurrent changes were legitimate independent work, not a Workspace Administration isolation violation. The audit exception is resolved, and Workspace Administration Phase 1 is ready for the separately authorized commit and GitHub branch publication.
Evidence: `before-valid.json`, `after.json`, and `worktree-comparison.txt` in the validation evidence directory.
Only MigrationTests.cs and this handoff document were edited in the Workspace Administration candidate
during this correction session; the approved inventory remains exactly 26 files.

## Exact changed-file inventory

- `docs/workspace-administration-api.md`
- `src/Core/SmartProperty.Application/Abstractions/Persistence/Repositories/IWorkspaceRepository.cs`
- `src/Core/SmartProperty.Application/Authorization/PermissionCodes.cs`
- `src/Core/SmartProperty.Application/Workspaces/Administration/AdminWorkspaceDetails.cs`
- `src/Core/SmartProperty.Application/Workspaces/Administration/AdminWorkspaceErrors.cs`
- `src/Core/SmartProperty.Application/Workspaces/Administration/AdminWorkspacePage.cs`
- `src/Core/SmartProperty.Application/Workspaces/Administration/Create/CreateWorkspaceCommand.cs`
- `src/Core/SmartProperty.Application/Workspaces/Administration/Create/CreateWorkspaceCommandHandler.cs`
- `src/Core/SmartProperty.Application/Workspaces/Administration/Create/CreateWorkspaceCommandValidator.cs`
- `src/Core/SmartProperty.Application/Workspaces/Administration/Get/GetAdminWorkspaceQuery.cs`
- `src/Core/SmartProperty.Application/Workspaces/Administration/Get/GetAdminWorkspaceQueryHandler.cs`
- `src/Core/SmartProperty.Application/Workspaces/Administration/List/GetAdminWorkspacesQuery.cs`
- `src/Core/SmartProperty.Application/Workspaces/Administration/List/GetAdminWorkspacesQueryHandler.cs`
- `src/Infrastructure/SmartProperty.Persistence/Repositories/Platform/WorkspaceRepository.cs`
- `src/Presentation/SmartProperty.Api/Contracts/Workspaces/AdminWorkspaceResponse.cs`
- `src/Presentation/SmartProperty.Api/Contracts/Workspaces/CreateWorkspaceRequest.cs`
- `src/Presentation/SmartProperty.Api/Controllers/AdminWorkspacesController.cs`
- `src/Presentation/SmartProperty.Api/Infrastructure/ServiceCollectionExtensions.cs`
- `tests/SmartProperty.Api.IntegrationTests/Workspaces/AdminWorkspaceCreateEndpointTests.cs`
- `tests/SmartProperty.Api.IntegrationTests/Workspaces/AdminWorkspaceReadEndpointTests.cs`
- `tests/SmartProperty.Api.IntegrationTests/Workspaces/AdminWorkspacesOpenApiTests.cs`
- `tests/SmartProperty.Persistence.IntegrationTests/Persistence/AdminWorkspaceRepositoryTests.cs`
- `tests/SmartProperty.Persistence.IntegrationTests/Persistence/MigrationTests.cs`
- `tests/SmartProperty.UnitTests/Application/Workspaces/AdminWorkspaceQueryHandlerTests.cs`
- `tests/SmartProperty.UnitTests/Application/Workspaces/CreateWorkspaceCommandHandlerTests.cs`
- `tests/SmartProperty.UnitTests/TestDoubles/FakeWorkspaceRepository.cs`

## Workspace members created by the workspace admin

`POST /api/workspaces/members` creates a member account in the caller's own workspace. The request names no
workspace: it is the one in which the signed-in caller holds the workspace permission `workspace.members.create`, so an
administrator can only add members to their own workspace and a `workspaceId` in the body is ignored. The "Workspace Admin" role created together with a
workspace (see above) holds it; the default "Member" role does not, and a platform role never satisfies it.
A caller who holds the permission in no workspace gets 403. A caller who holds it in more than one workspace gets 409
`workspace_members.ambiguous_workspace`, because the request cannot say which.

Body: `{"email","password","firstName","lastName"}`, validated by the registration rules (no phone number).
The administrator vouches for the account, so it is created `Active` and can sign in at once, with no verification
or approval. One save creates the user, its credential, the membership, and the assignment of the workspace's
"Member" role, which carries exactly `workspace.read`. The Member role is created with the workspace's first member.
An existing Member role is reused only if it carries exactly `workspace.read`; otherwise the request fails closed
with 409 `workspace_members.member_role_misconfigured` and nothing is saved.

Responses: 201 `{userId, membershipId, workspaceId, email, firstName, lastName, role, createdAt}` (no `Location`:
no endpoint reads a single member yet); 401; 403; 409 `users.email_already_exists`; 409
`workspace_members.concurrent_provisioning` (retry); 422 `workspace_members.validation_failed`.

Workspace Admin roles created before `workspace.members.create` existed do not hold it. Granting it to them is a
separate, explicitly approved data change; nothing provisions it automatically.

## Company workspace registration (2026-11-15)

There are two ways to get a workspace:

1. **The platform administrator creates it** with `POST /api/admin/workspaces` and an `admin` block (above).
2. **A company registers and a platform administrator approves.** This section.

Platform Admin now holds `workspace.admin.read`, `workspace.admin.create` and `workspace.registrations.review`
through the staff role catalogue, which runs at every start, so a freshly bootstrapped administrator can use both
paths with no hand-written grant.

### Applicant endpoints

| Method and route | Auth | Success |
| --- | --- | --- |
| POST /api/auth/register-workspace | anonymous (auth rate limit) | 201 |
| POST /api/workspace-registrations | signed in, Active account | 201 request |
| GET /api/workspace-registrations/mine | signed in | 200 array, newest first |

`POST /api/auth/register-workspace` (company sign-up screen):

```json
{
  "email": "owner@acme.com",
  "password": "********",
  "firstName": "Sara",
  "lastName": "Owner",
  "phoneNumber": "+201012345678",
  "companyName": "Acme Realty",
  "commercialRegistrationNumber": "CR-1010"
}
```

The account fields follow `POST /api/auth/register` exactly: the account is created Pending and the phone and email
codes are sent. Verify them with the usual `verify-phone`/`verify-email` endpoints; the account then becomes Active
and can sign in as an ordinary user. The response adds the request:

```json
{
  "userId": "...", "status": "Pending",
  "phoneVerificationExpiresAt": "...", "emailVerificationExpiresAt": "...",
  "registrationRequestId": "...", "registrationStatus": "Pending"
}
```

`POST /api/workspace-registrations` takes `{"companyName", "commercialRegistrationNumber"}` for an account that
already exists (for example, an individual user who later registers a company).

Rules: `companyName` is required, 1–200 characters after trimming, with no control characters.
`commercialRegistrationNumber` is optional, at most 64 characters. An account may have only one Pending request
(409 `workspace_registrations.pending_request_exists`); after a rejection it may apply again.

### Platform administrator endpoints (`workspace.registrations.review`)

| Method and route | Success |
| --- | --- |
| GET /api/admin/workspace-registrations?status=Pending&page=1&pageSize=10 | 200 paginated, oldest first; status defaults to Pending |
| GET /api/admin/workspace-registrations/{id} | 200 request |
| POST /api/admin/workspace-registrations/{id}/approve | 200 `{requestId, status, workspaceId, workspaceName, applicantUserId, membershipId}` |
| POST /api/admin/workspace-registrations/{id}/reject with `{"reason": "..."}` | 200 request |

A request item:

```json
{
  "id": "...", "companyName": "Acme Realty", "commercialRegistrationNumber": "CR-1010",
  "status": "Pending", "requestedAt": "...", "reviewedAt": null, "reviewedByUserId": null,
  "rejectionReason": null, "workspaceId": null,
  "applicant": { "userId": "...", "email": "...", "firstName": "...", "lastName": "...",
                 "phoneNumber": "...", "status": "Active", "contactVerified": true }
}
```

**Approve** creates a workspace named after the company and makes the applicant its admin, with the same
`Workspace Admin` role (`workspace.read`, `workspace.members.create`) as the administrator-created path. Everything
is saved together. It fails with 409:

- `workspace_registrations.contact_not_verified`: the applicant has not verified the phone and email yet (show
  `applicant.contactVerified` in the queue);
- `workspace_registrations.applicant_unavailable`: the account is Suspended or Deactivated (never revived);
- `workspace_registrations.already_reviewed`: already decided, including when two reviewers act at once;
- `workspace_registrations.concurrent_change`: a rare race on permission rows; retry.

**Reject** needs a reason of 1–1000 characters (422 `validation.failed` otherwise), records it for the applicant,
and creates nothing. Decisions are final.

Storage: `identity.workspace_registration_requests` (migration `WorkspaceRegistrationRequests`), with a partial unique
index allowing one Pending request per applicant, and the status column as a concurrency token.
