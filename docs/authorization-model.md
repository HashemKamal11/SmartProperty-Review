# Authorization Model

This document records the authorization scope semantics, contracts, and rules, and how permission resolution implements them. Step 05.6A established the model and contracts; Step 05.6B supplied the resolver that answers a permission question from the database.

ASP.NET Core policy integration, endpoint enforcement, the permission catalog, seeds, and caching remain deferred. **Nothing enforces a permission on any endpoint yet**: a resolver exists, but no production code calls it.

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

It validates on construction — non-empty user id, non-blank permission code, non-null target — and trims the permission code to match how `Permission` stores it. The user is identified by `Guid`, never by a `ClaimsPrincipal` or an HTTP context, which keeps the contract framework-neutral and unit-testable. Building a request from the current request's user is the API's job, using `ICurrentUser.UserId`; that integration is not part of this step, and `ICurrentUser` stays where it is.

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

Illustrative only — these are examples of the shape, not a catalog to implement:

```text
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

No permission catalog is defined or seeded in this step.

> Earlier examples in identity-access-model.md used a PascalCase form such as `Admin.Users.View`. No permissions are seeded yet, so nothing persisted depends on that spelling; the lowercase convention above is the one to follow.

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

Repositories were deliberately not extended for this. `IUserRepository`, `IRoleRepository`, `IPermissionRepository`, and `IWorkspaceMembershipRepository` return single entities and could only have answered these questions across several round trips, and no repository exists — or was added — for `UserPlatformRole`, `WorkspaceMembershipRole`, or `RolePermission`. No `IRepository<T>`, `GetAll()`, or `FindAll()` exists for authorization.

## Deferred

- **Platform enforcement** and **workspace enforcement** on real endpoints. Resolution works, but no endpoint asks it anything.
- **ASP.NET Core integration** — `AuthorizationHandler`, `IAuthorizationRequirement`, a dynamic policy provider, a `RequirePermission` attribute, endpoint filters, resource-based authorization.
- **Permission catalog** and role/permission **seed data**; no migrations.
- **Caching** of authorization results, in memory or Redis.
- **Platform Admin semantics** — whether it implies all permissions, and whether it reaches into workspaces.
- **Workspace route resolution** — how a workspace id is taken from a request and turned into a target.
- **Claims strategy** — if permissions are ever put in a token, the staleness trade-off above must be addressed explicitly.
