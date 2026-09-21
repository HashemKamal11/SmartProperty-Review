# Authorization Model

This document records the Step 05.6A authorization foundation: the scope semantics, contracts, and rules that later steps must implement. It is a design and contract baseline only. Permission resolution against the database, ASP.NET Core policy integration, endpoint enforcement, the permission catalog, seeds, and caching are all deferred.

Nothing in this step enforces a permission on any endpoint.

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

**No implementation exists yet.** Supplying one is Step 05.6B.

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

Rules a resolver must honour:

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

Rules a resolver must honour:

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

Authorization denies unless it can positively establish access. A resolver must return `Denied` for every one of:

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

## Future Query Shape

Permission resolution should be an existence check, not an object-graph load. The intended shape is a single `AnyAsync` per question — compiling to `SELECT EXISTS (...)` — joining the path above and filtering on user id, permission code, role scope, and (for workspace targets) workspace id.

What to avoid: loading the user, then their roles, then those roles' permissions, and searching in memory. That is several round trips and an N+1 waiting to happen.

Current repository contracts (`IUserRepository`, `IRoleRepository`, `IPermissionRepository`, `IWorkspaceMembershipRepository`) return single entities and are **not** sufficient for this; there are no repositories for `UserPlatformRole`, `WorkspaceMembershipRole`, or `RolePermission`. Step 05.6B should add the smallest purpose-built authorization read contract — two existence queries, one per scope — rather than generic collection getters. No `IRepository<T>`, `GetAll()`, or `FindAll()` is to be introduced for authorization.

No repository method was added in this step, because nothing here resolves a permission yet.

## Deferred

- **Permission resolution** — implementing `IPermissionChecker` against the database (Step 05.6B).
- **Platform enforcement** and **workspace enforcement** on real endpoints.
- **ASP.NET Core integration** — `AuthorizationHandler`, `IAuthorizationRequirement`, a dynamic policy provider, a `RequirePermission` attribute, endpoint filters, resource-based authorization.
- **Permission catalog** and role/permission **seed data**; no migrations.
- **Caching** of authorization results, in memory or Redis.
- **Platform Admin semantics** — whether it implies all permissions, and whether it reaches into workspaces.
- **Workspace route resolution** — how a workspace id is taken from a request and turned into a target.
- **Claims strategy** — if permissions are ever put in a token, the staleness trade-off above must be addressed explicitly.
