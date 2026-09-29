# Authorization Model

This document records the authorization scope semantics, contracts, and rules, how permission resolution implements them, and how ASP.NET Core authorization reaches them. Step 05.6A established the model and contracts; Step 05.6B supplied the resolver that answers a permission question from the database; Step 05.6C added the reusable ASP.NET bridge; Step 05.7 put the first permission on real endpoints, added the code that names it, and added the bootstrap that provisions the first account able to hold it.

**Platform enforcement is now live.** One permission code exists, `workspace.access_requests.review`, declared once in `PermissionCodes`; `RequirePermissionAttribute` attaches it to the workspace access request review endpoints; and `PlatformAdminBootstrapService` is the configuration-gated, default-off way the first administrator comes to hold it.

Route workspace-id extraction, workspace enforcement on an endpoint, a named-policy catalog, and caching remain deferred.

## Authentication Versus Authorization

The two are separate concerns and are deliberately kept apart.

| | Question | Where it lives |
| --- | --- | --- |
| Authentication | Who is the caller? | JWT Bearer validation, `ICurrentUser` (see authentication-model.md) |
| Authorization | May this caller do this? | This document |

Authentication already guarantees that an authenticated request carries exactly one valid user id. Authorization starts from that id and asks a separate question against current persisted data.

## Access Tokens Stay Identity-Only

Access tokens carry `sub`, `jti`, and the standard issuer, audience, and time claims. They carry **no** role, permission, workspace, membership, or platform-admin claim, and this step does not change that.

The reason is correctness over convenience. If permissions travelled in the token, then revoking a role, removing a membership, changing a role's permissions, or suspending an account would not take effect until the token expired. Reading current persisted state instead means an access change applies to the next request.

The cost is a database read per authorization check. Caching is a later optimization, to be considered only once correctness is established, and it must not silently reintroduce the staleness this decision avoids.

## Scopes

Authorization has exactly two scopes, and they never mix.

- **Platform** — global. Concerns the platform itself, not any single workspace.
- **Workspace** — one specific workspace, identified by id.

The Domain already models this as `RoleScope { Platform = 1, Workspace = 2 }`, and `Role` already enforces that a platform role has no workspace id while a workspace role has a non-empty one. The authorization foundation reuses that enum rather than defining a parallel one: the role scope that can satisfy a target is exactly the target's own scope, so a second enum could only disagree with the first.

## Authorization Target

`AuthorizationTarget` (Application/Authorization) names what is being accessed:

```csharp
AuthorizationTarget.Platform                 // Scope = Platform, WorkspaceId = null
AuthorizationTarget.Workspace(workspaceId)   // Scope = Workspace, WorkspaceId = that id
```

The constructor is private and the properties are get-only, so the meaningless pairings cannot be constructed at all:

| Invalid state | Why it cannot occur |
| --- | --- |
| Platform + workspace id | `Platform` is a single instance built with a null id; nothing else yields a platform target |
| Workspace + null id | `Workspace(Guid)` requires an id |
| Workspace + `Guid.Empty` | `Workspace(Guid)` rejects it with `ArgumentException` |

Validity is therefore established once, at construction, instead of being re-checked at every call site.

## Authorization Request

`AuthorizationRequest.For(userId, permissionCode, target)` is one question: *may this user exercise this permission against this target?*

It validates on construction — non-empty user id, non-blank permission code, non-null target — and trims the permission code to match how `Permission` stores it. The user is identified by `Guid`, never by a `ClaimsPrincipal` or an HTTP context, which keeps the contract framework-neutral and unit-testable. The API builds the request from `ICurrentUser.UserId`; `ICurrentUser` remains the single boundary between the authenticated principal and Application.

## Authorization Decision

`AuthorizationDecision` is `Allowed` or `Denied` and exposes nothing but `IsAllowed`.

It deliberately carries no reason, missing-permission code, role name, membership state, or policy name. A caller able to read *why* access was denied could map out the access model one request at a time, and the public response has to be uniform anyway. HTTP status codes, error bodies, and action results have no place in it — that mapping belongs to the API.

## Permission Checker Contract

One abstraction, `IPermissionChecker` (Application/Abstractions/Authorization):

```csharp
Task<AuthorizationDecision> CheckAsync(
    AuthorizationRequest request,
    CancellationToken cancellationToken = default);
```

It is named to avoid collision with ASP.NET Core's `IAuthorizationService`. There is deliberately one authorization boundary rather than a family of overlapping `IPermissionService` / `IRoleChecker` / `IPolicyService` interfaces.

### Implementation

`PermissionChecker` (Persistence/Authorization) implements it against EF Core and PostgreSQL, registered `Scoped` in `AddPersistence` alongside the repositories. The implementation is `internal` to the Persistence assembly: callers depend on the Application-layer interface, and no EF Core type appears anywhere in the contract.

Persistence is the right layer for it because resolving a permission is a database question. The Application layer defines what is being asked; it does not know how the answer is found. The API knows neither.

It contributed no repository: the two questions are existence checks rather than entity lookups, so they are expressed directly against `ApplicationDbContext` instead of behind `IUserPlatformRoleRepository` / `IWorkspaceMembershipRoleRepository` / `IRolePermissionRepository`, which would have cost three abstractions and several round trips to answer one question.

## ASP.NET Core Integration

Two types in the API (`Infrastructure/Authorization`) connect ASP.NET Core authorization to the checker. Both are `internal`, like the rest of the API's infrastructure, and neither has an equivalent in Application or Domain — `IAuthorizationRequirement`, `AuthorizationHandler`, `ClaimsPrincipal`, and `HttpContext` stay in the API.

**`PermissionRequirement`** names one permission code and nothing else:

```csharp
new PermissionRequirement("property.read")
```

It trims the code and preserves case, matching `Permission.Code` semantics exactly. A blank code throws `ArgumentException` — a requirement is server configuration, not user input, so a bad one is a startup-time programming error rather than an HTTP response.

It deliberately does **not** carry a workspace id or a scope flag. What the permission is exercised against is the authorization *resource*:

**`PermissionAuthorizationHandler`** is `AuthorizationHandler<PermissionRequirement, AuthorizationTarget>` — the Application-layer `AuthorizationTarget` is the ASP.NET resource. Typing it that way means a second Platform/Workspace model never gets invented in the web layer, and that the framework simply never invokes the handler for any other resource, including a null one. An unexpected resource therefore leaves the requirement unsatisfied; there is no loose `object` switch that could fall through to success.

The flow is a translation and nothing more:

```text
HttpContext (RequestAborted)
ICurrentUser.UserId          ─┐
requirement.PermissionCode   ─┼─> AuthorizationRequest.For(...)
AuthorizationTarget resource ─┘         ↓
                                 IPermissionChecker.CheckAsync
                                        ↓
                          Allowed -> context.Succeed(requirement)
                          Denied  -> requirement left unsatisfied
```

Details worth stating, because each is a rule the handler has to honour rather than an implementation accident:

- **Identity comes only from the authenticated principal**, through `ICurrentUser`. The handler never parses the `Authorization` header, re-validates a token, or accepts a user id from a route, query, or body. If no usable id is present the requirement stays unsatisfied and the checker is not called — `Guid.Empty` is never passed down.
- **Only `Allowed` succeeds.** No role-name shortcut, no Platform Admin bypass, no claim-based bypass, and "authenticated" alone is never enough.
- **A denial is a silent non-success**, not `context.Fail()`. Failing outright would block any other handler that might legitimately satisfy the same requirement later, and no public failure reason is wanted in any case.
- **`HttpContext.RequestAborted` is passed to the checker**, not `CancellationToken.None`, so an abandoned request stops querying.
- **Nothing is caught.** A checker exception — a database outage — propagates to the normal exception pipeline and surfaces as `500`. Turning it into a denial would disguise an outage as a permissions problem. Cancellation propagates for the same reason.
- **The handler writes no response.** It sets no status code, adds no header, and calls neither `ForbidAsync` nor `ChallengeAsync`; the existing JwtBearer `OnChallenge` and `OnForbidden` events own the standardized bodies.

Registration is `AddApiAuthorization()` in `Program`, adding `IAuthorizationHandler → PermissionAuthorizationHandler` as `Scoped` because it reaches the scoped checker. Authorization services themselves were already registered by `AddControllers`, which is why `[Authorize]` on `GET /api/auth/me` has always worked; nothing about that configuration changed.

### Responses

| Caller | Outcome |
| --- | --- |
| Unauthenticated | existing `401 authentication.unauthorized`, `WWW-Authenticate: Bearer` |
| Authenticated, decision `Denied` | existing `403 authorization.forbidden` |

Both are the responses that already existed; this step added no error code and changed no contract. The `403` still names no permission code, role, workspace, policy, or handler.

## Permissions Are the Authorization Contract

Roles aggregate permissions. Permissions are what code checks.

Business endpoints must eventually ask *"does this user have `property.create` here?"* — never *"is this user in the Manager role?"*. Role names are display data and administrators can rename or restructure them; a permission code is a stable contract.

Accordingly, authorization must never branch on:

- a role display name such as `"Admin"`, `"Manager"`, or `"Platform Admin"`
- a hard-coded role id
- any enum ordinal standing in for a role

### Platform Admin

A Platform Administrator role may exist as a platform-scoped role, but it gets no special treatment in code. There is no `if (role.Name == "Platform Admin") allow everything` shortcut and no magic id.

If Platform Admin is meant to hold every permission, that must be expressed as explicit permission assignments, or as an explicitly designed and documented policy — never as an accidental consequence of a role name. The identity access model already states the related rule: membership of a workspace named "Admin Workspace" grants no global permission.

Whether Platform Admin implies workspace-level access is **an open decision**, deferred. Until it is decided, no implicit platform-to-workspace bypass exists.

A `"Platform Admin"` role does now exist, created by the bootstrap described below, and it holds exactly one permission. The name is a provisioning key — the string the bootstrap looks a role up by so that a second run finds the same row — and nothing else. No code branches on it, and holding it grants nothing beyond the permissions explicitly attached to it.

## Permission Catalog

`PermissionCodes` (Application/Authorization) is the one place a permission code is spelled:

```csharp
public const string WorkspaceAccessRequestsReview = "workspace.access_requests.review";
```

Endpoints, the bootstrap, and the tests all reference the constant, so the string that is *granted* and the string that is *demanded* cannot drift apart. It is not a speculative catalog: a code is added when an endpoint actually requires one, and there is exactly one so far.

A companion `…Description` constant supplies `Permission.Description` when the row is provisioned. It is human-readable text; nothing authorizes on it.

## Declaring a Permission on an Endpoint

`RequirePermissionAttribute` (Api/Infrastructure/Authorization) is how an endpoint states its requirement:

```csharp
[ApiController]
[Route("api/admin/workspace-access-requests")]
[RequirePermission(PermissionCodes.WorkspaceAccessRequestsReview)]
public sealed class AdminWorkspaceAccessRequestsController : ControllerBase
```

One attribute covers both answers, because it is two things at once:

| It is | Which gives |
| --- | --- |
| an `AuthorizeAttribute` | the authorization middleware applies the default policy, so an unauthenticated caller gets the existing `401` before anything else runs |
| an `IAsyncAuthorizationFilter` | the permission check runs for authenticated callers and produces the existing `403` |

Being an `AuthorizeAttribute` also places `IAuthorizeData` in the endpoint metadata, which is exactly what the OpenAPI operation transformer already reads to emit the Bearer security requirement. The transformer needed no change, and an integration test asserts the generated document rather than assuming it.

The filter is a translation and nothing more. It hands the real `IAuthorizationService` a `PermissionRequirement` together with an `AuthorizationTarget` resource — the pairing `PermissionAuthorizationHandler` is typed for — and only that handler reaches `IPermissionChecker`. No controller queries a permission, and the attribute touches no database, role, claim, or token.

It fails closed: only `Succeeded` lets the action run, and a denial, an unusable identity, and an unrecognized resource all end as `ForbidResult`. A checker exception is deliberately not caught, so a database outage still surfaces as `500` rather than as a permission denial.

**The attribute is platform-scoped, fixed at construction.** Workspace targets need a workspace id taken from the request, which is still deferred, so a workspace-scoped permission must not be declared with this attribute until that exists.

Why an MVC authorization filter rather than a dynamic policy provider: the existing handler is typed `AuthorizationHandler<PermissionRequirement, AuthorizationTarget>`, and the authorization middleware passes the `Endpoint` as the resource, so a policy evaluated there would never reach that handler. A policy provider would therefore have required a second handler duplicating the first. The filter supplies the real resource instead, and the one handler stays the only path to the checker.

## Platform Administrator Bootstrap

Platform authorization has a cold-start problem: only a holder of `workspace.access_requests.review` may review a request, and no endpoint can grant that permission to the first administrator. The bootstrap is the deliberate, narrow answer.

Configuration, disabled by default in code *and* in `appsettings.json`:

```text
Bootstrap:Enabled              / Bootstrap__Enabled             (false)
Bootstrap:PlatformAdminEmail   / Bootstrap__PlatformAdminEmail  ("")
```

With `Enabled` false — including a deployment that omits the section entirely — nothing runs and startup is unchanged.

With `Enabled` true, `PlatformAdminBootstrapService` (an `IHostedService`) runs `BootstrapPlatformAdminCommandHandler` once, which:

1. normalizes the configured email the same way `User` normalizes a stored one, and finds the **existing** account
2. activates it only if it is `Pending`; leaves an `Active` one untouched, including its `UpdatedAt`
3. ensures the permission row, the platform-scoped `"Platform Admin"` role, the role/permission row, and the user/role row exist
4. commits all of it through one `IUnitOfWork.SaveChangesAsync`

What it cannot do is the point:

- it never creates a `User`, a `UserCredential`, or a password — it elevates an account that registered normally
- no account is named in code; the email is deployment configuration
- it refuses a `Suspended` or `Deactivated` account rather than reviving one, because reviving a disabled account and handing it platform authority is exactly the escalation a bootstrap must not offer
- it grants one permission, not every permission

Every write is guarded by a lookup, so a second run creates no duplicate permission, role, role-permission, or platform-role row and reports `MadeNoChange`.

An enabled run that fails logs the actionable reason at `Critical` and then **aborts startup**, matching how this application already treats broken startup configuration (a missing `ConnectionStrings:Database` throws from `AddPersistence`; invalid `Jwt` options fail `ValidateOnStart`). An operator who deliberately enabled bootstrap must not be left with an un-administrable platform and no signal. An enabled configuration with no email fails `ValidateOnStart` before the host serves a request.

Nothing sensitive is logged: no password, signing key, database credential, or token — the use case has none to give. The configured email is logged because it is the one fact that makes a failure actionable, and it is operator-supplied configuration rather than a secret.

The application never rewrites its own configuration. Operations disables `Bootstrap__Enabled` externally once provisioning has succeeded, and the success log says so.

## Permission Code

`Permission.Code` is the canonical identifier. It is:

- `string`, required, trimmed on construction, maximum 200 characters
- unique in the database (`ux_identity_permissions_code`)
- **compared exactly, including case** — `PermissionRepository.GetByCodeAsync` trims the input but does not change case, and the unique index is case-sensitive
- separate from `Permission.Description`, which is optional human-readable text

Authorization operates on this code. It must never key off the permission's database `Guid` (an internal surrogate that should not reach an API contract), a role name, a display label, or an enum ordinal.

### Naming Convention

Permission codes follow lowercase dot-separated segments:

```text
<resource>.<action>
<area>.<resource>.<action>
```

The one code that exists, plus further examples of the shape:

```text
workspace.access_requests.review   <- implemented, see Permission Catalog
property.read
property.create
workspace.members.read
workspace.members.manage
```

Rules:

- lowercase, with `.` as the only separator
- stable once introduced; rename means a new code plus a migration of assignments, because the old string is a contract
- machine-readable and independent of any UI label, which belongs in `Description`
- compared exactly, so codes must be stored in the canonical lowercase form

> Earlier examples in identity-access-model.md used a PascalCase form such as `Admin.Users.View`. Nothing persisted uses that spelling; the lowercase convention above is the one to follow, and `workspace.access_requests.review` follows it.

There is still no migration that seeds permissions. The one permission row is created by the bootstrap described above, on demand and idempotently, rather than by migration data — so a fresh database contains no authorization rows at all until an operator provisions them.

## Platform Authorization Path

```text
User
 └─> UserPlatformRole
      └─> Role (Scope = Platform)
           └─> RolePermission
                └─> Permission (Code)
```

Rules the resolver enforces, restated as predicates in the query itself:

- the role reached must have `Scope = Platform`
- the permission must be reachable through a platform role assigned to this user
- workspace memberships grant **no** platform permission
- workspace role assignments never count toward a platform target

## Workspace Authorization Path

```text
User
 └─> WorkspaceMembership (UserId, WorkspaceId)
      └─> WorkspaceMembershipRole
           └─> Role (Scope = Workspace, WorkspaceId = target)
                └─> RolePermission
                     └─> Permission (Code)
```

Rules the resolver enforces, restated as predicates in the query itself:

- the membership must belong to this user **and** to the target workspace
- the role reached must have `Scope = Workspace`
- the role must belong to the same workspace as the membership
- membership of workspace A must never authorize anything in workspace B
- platform roles do not automatically become workspace roles

## Active-User Invariant

Only a currently persisted `UserStatus.Active` user may be allowed. This is checked against the database, not inferred from the token: a token issued while the account was active must stop authorizing once it is not.

| User state | Result |
| --- | --- |
| Missing | Deny |
| Pending | Deny |
| Suspended | Deny |
| Deactivated | Deny |
| Active | continue evaluating the permission |

## Fail-Closed Rules

Authorization denies unless it can positively establish access. The resolver returns `Denied` for every one of:

- the user row is missing
- the user is not `Active`
- the permission code is unknown
- the role is missing
- the role scope does not match the target
- the workspace is missing when one is required
- the workspace id is empty
- no membership exists for this user in the target workspace
- the membership belongs to a different workspace
- no matching permission assignment exists
- the access graph is inconsistent in any other way

**A failure is not a denial.** An unexpected persistence failure — a database outage, a connection error — must propagate as an exception and surface as a normal `500`. Swallowing infrastructure failures into a clean `Denied` would hide outages and make an unavailable database look like a permissions problem.

## Public Response Boundary

A denial maps to the already-standardized response (see authentication-model.md):

```text
403 authorization.forbidden
"You do not have permission to access this resource."
```

That response must never disclose the missing permission code, the missing role, workspace membership status, an internal policy name, a role id, or a permission id. This step does not change the existing `401`/`403` behavior.

Note the existing distinction: `403 authentication.account_unavailable` means the account itself may not be used, while `403 authorization.forbidden` means the account lacks permission for this resource.

## Query Shape

Permission resolution is an existence check, not an object-graph load. Each question is **one** database command: a single `AnyAsync` over the joined path, which PostgreSQL answers as `SELECT EXISTS (...)`. Nothing is materialized — no `Include`, no `ToList`, no entity instance, and therefore no tracking concern and no N+1.

Platform:

```sql
SELECT EXISTS (
    SELECT 1
    FROM identity.users AS u
    INNER JOIN identity.user_platform_roles AS u0 ON u.id = u0.user_id
    INNER JOIN identity.roles AS r ON u0.role_id = r.id
    INNER JOIN identity.role_permissions AS r0 ON r.id = r0.role_id
    INNER JOIN identity.permissions AS p ON r0.permission_id = p.id
    WHERE u.id = @userId AND u.status = 'Active'
      AND r.scope = 'Platform' AND r.workspace_id IS NULL
      AND p.code = @permissionCode)
```

Workspace:

```sql
SELECT EXISTS (
    SELECT 1
    FROM identity.users AS u
    INNER JOIN identity.workspace_memberships AS w ON u.id = w.user_id
    INNER JOIN identity.workspace_membership_roles AS w0 ON w.id = w0.workspace_membership_id
    INNER JOIN identity.roles AS r ON w0.role_id = r.id
    INNER JOIN identity.role_permissions AS r0 ON r.id = r0.role_id
    INNER JOIN identity.permissions AS p ON r0.permission_id = p.id
    WHERE u.id = @userId AND u.status = 'Active'
      AND w.workspace_id = @workspaceId
      AND r.scope = 'Workspace' AND r.workspace_id = @workspaceId2
      AND p.code = @permissionCode)
```

The active-user rule is a predicate in the same command rather than a separate lookup, so a check never costs two round trips. The permission code is compared as `p.code = @permissionCode` — exact and case-sensitive, matching how `Permission` stores it; nothing lowercases either side.

Note that the workspace query constrains the target workspace **twice**, on the membership and on the role. Either constraint alone would be enough against data the domain constructors built, but authorization has to fail closed against rows that arrived by import or by hand: a membership in workspace A carrying a role owned by workspace B is denied rather than allowed.

Cancellation flows into `AnyAsync`, so a cancelled request surfaces as `OperationCanceledException` and never as a decision.

Repositories were deliberately not extended for *resolution*. `IUserRepository`, `IRoleRepository`, `IPermissionRepository`, and `IWorkspaceMembershipRepository` return single entities and could only have answered these questions across several round trips. No `IRepository<T>`, `GetAll()`, or `FindAll()` exists for authorization.

`IRoleRepository` did gain the assignment members the bootstrap needs — `GetPlatformRoleByNameAsync`, `HasPermissionAsync`, `AddPermissionAsync`, `IsAssignedToUserAsync`, `AddUserAssignmentAsync` — and they are for **provisioning**, not for deciding. Nothing authorizes through them; the checker's single query remains the only resolution path, and no repository was introduced for `UserPlatformRole`, `RolePermission`, or `WorkspaceMembershipRole` as a type of its own.

## Deferred

- **Workspace enforcement** on a real endpoint, and **workspace route resolution** — how a workspace id is taken from a request and turned into a target. Platform enforcement is live; `RequirePermissionAttribute` is platform-only until this exists.
- **A named-policy catalog** and endpoint filters for minimal APIs. The attribute covers MVC controllers, which is all the API has.
- **Role and permission seed data in a migration.** The single permission row is provisioned by the bootstrap instead.
- **Caching** of authorization results, in memory or Redis.
- **Platform Admin semantics** — whether it should imply all permissions, and whether it reaches into workspaces. It currently implies neither: it holds one explicitly attached permission.
- **Administration of permissions after bootstrap** — no endpoint creates a role, attaches a permission, or assigns a platform role. Bootstrap is the only way any of that happens, and it provisions exactly one fixed grant.
- **Claims strategy** — if permissions are ever put in a token, the staleness trade-off above must be addressed explicitly.

## The Protected Routes

The first permission-protected production endpoints, all three requiring `workspace.access_requests.review` against the platform target:

| Route | Does |
| --- | --- |
| `GET /api/admin/workspace-access-requests` | The review queue. `workspaceId` and `status` are optional; `status` defaults to `Pending`. Standard `page`/`pageSize` paging, standard `PagedResponse` shape. |
| `POST /api/admin/workspace-access-requests/{id}/approve` | Approves a pending request. |
| `POST /api/admin/workspace-access-requests/{id}/reject` | Rejects a pending request. |

The queue exists because the other two need a request id and nothing else revealed one.

**The reviewer is never an input.** Neither route accepts a reviewed-by user id in a route, query, header, or body; the id is read from the authenticated principal through `ICurrentUser` inside the use case, and `WorkspaceAccessRequest.ReviewedByUserId` is asserted to equal the token's subject in an integration test.

Approval, in one unit of work:

- approves the request
- activates the account **only** if it is `Pending`; an `Active` one is left untouched
- creates a `WorkspaceMembership` for the target workspace if none exists, and reuses one that does
- **assigns no role.** Membership answers whether an account may be in a workspace; what it may do there is a separate grant, and approval does not smuggle one in. No `WorkspaceMembershipRole` row is written, which an integration test asserts against the table.
- refuses a `Suspended` or `Deactivated` applicant with `409 workspace_access_requests.target_account_unavailable` rather than reactivating one

Rejection records the decision and nothing else: no activation, suspension, deactivation, membership, or role. A rejected applicant that registered as `Pending` stays `Pending` and still cannot sign in — an integration test proves it by calling the real login endpoint and asserting `403 authentication.account_unavailable`.

Both return `200` with the resulting state rather than `204`: the membership id and the two resulting statuses are the useful outcome, and a caller should not have to go looking for them.

Errors follow the existing `Result`/`Error` mapping. A domain invalid-state failure is never surfaced as an exception — the non-`Pending` case is translated to a stable business error before the domain method is called:

| Situation | Response |
| --- | --- |
| unknown request id | `404 workspace_access_requests.not_found` |
| request already reviewed | `409 workspace_access_requests.already_reviewed` |
| applicant suspended, deactivated, or missing | `409 workspace_access_requests.target_account_unavailable` |
| unparseable `status` or `workspaceId` | existing `400 request.malformed` |

## Simultaneous Review

`WorkspaceAccessRequest.Status` is an EF concurrency token. A terminal update therefore carries the status the reviewer originally loaded in its predicate; both reviewers may read `Pending`, but after one commits, the other's update with original status `Pending` affects zero rows. EF reports that as `DbUpdateConcurrencyException`, `UnitOfWork` translates it to `ConcurrencyConflictException(PersistenceResource.WorkspaceAccessRequest)`, and both review handlers return the existing `409 workspace_access_requests.already_reviewed` response.

Approval's request decision, user activation, and membership insertion remain one `SaveChangesAsync` transaction. A stale approval therefore commits none of those writes.

A collision on the named unique `(user_id, workspace_id)` membership constraint is not automatically treated as a review conflict: an unrelated concurrent writer may have created the membership while the request itself is still `Pending`. The approval handler performs one bounded recovery attempt:

1. Discard the failed tracked graph so the rolled-back approval, activation, and `Added` membership cannot leak into the retry.
2. Reload the request, applicant, and membership from authoritative persisted state.
3. Revalidate the request and account. A terminal request becomes `workspace_access_requests.already_reviewed` (`409`); a missing request or unusable account keeps its existing provider-neutral business result.
4. Reuse the now-existing membership, reapply approval and any required `Pending`-to-`Active` transition, and save once more.

If the bounded retry loses the request concurrency race, it also becomes `already_reviewed`. If the reported membership collision is not confirmed by a reloaded membership, the original provider-neutral persistence failure is rethrown rather than invented into a business result. Other unique violations remain unexpected.

No migration is required: `status` already exists with the same PostgreSQL type and constraints. `IsConcurrencyToken()` changes EF's update predicate and affected-row validation, not the physical schema. Real PostgreSQL tests load `Pending` through two independent contexts before either saves and cover approve/reject in both winner orders, approve/approve, and reject/reject.
