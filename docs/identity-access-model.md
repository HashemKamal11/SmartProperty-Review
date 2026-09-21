# Identity Access Model

This document records the Step 05.3 identity and access model foundation. It is a data model baseline only. Authentication, JWT, authorization policy enforcement, admin APIs, compliance APIs, approval workflows, seed data, and migrations remain deferred.

## Core Decisions

- A user can access multiple workspaces through workspace memberships.
- A user can hold multiple roles inside the same workspace.
- Workspaces are dynamic database entities, not enum values.
- The current workspace names shown by the frontend are business data, not domain constants: Customer Workspace, Operations Workspace, Developer Workspace, Investor Workspace, Admin Workspace, and Compliance Workspace.
- Registration workspace selection creates a workspace access request only. It does not grant access.
- Platform Admin approval is required for future workspace access request approval.
- Approval of an access request does not automatically imply any role assignment.
- `WorkspaceAccessRequest` and `WorkspaceMembership` are separate concepts.
- `WorkspaceMembership` represents actual workspace access.

## Admin Workspace Versus Platform Admin

Admin Workspace is not the same thing as a Platform Admin role.

A user may request or become a member of a workspace named Admin Workspace. That membership grants zero global admin permissions by itself. Platform Admin access requires an explicit global platform role assignment. No implicit mapping from workspace name to authorization role is allowed.

## Roles And Permissions

Roles are data records with an explicit scope:

- `Platform` roles apply globally and must not belong to a workspace.
- `Workspace` roles belong to one workspace.

Permissions are assigned through roles:

- Platform authorization: User -> Platform Role -> Permission.
- Workspace authorization: User -> WorkspaceMembership -> Workspace Role -> Permission.

There are no direct User -> Permission assignments in this foundation. Permission codes are database-driven machine-readable identifiers, and no permission catalog is seeded in this step.

Roles aggregate permissions; permissions are what authorization checks. The two scopes stay separate: a workspace role never grants platform access, and a platform role is not automatically a workspace role. The naming convention for permission codes and the full enforcement design are documented separately in [authorization-model.md](authorization-model.md).

## Access Request Flow

The future registration and access workflow is:

```text
Register
    ->
Choose requested workspace
    ->
WorkspaceAccessRequest = Pending
    ->
Platform Admin reviews request
    ->
Approved / Rejected
```

Approving a `WorkspaceAccessRequest` only changes the request state. It does not create a `WorkspaceMembership`, assign roles, call repositories, call `DbContext`, or perform authorization. A future Application workflow will coordinate request approval, membership creation, role assignment, and the final transaction boundary.

## Assignment Invariants

The model uses explicit association entities:

- `UserPlatformRole` assigns a platform-scoped role to a user.
- `WorkspaceMembershipRole` assigns a workspace-scoped role to a workspace membership.
- `RolePermission` assigns permissions to roles.

Application workflows must enforce these cross-entity invariants before assignment:

- `UserPlatformRole.RoleId` must reference a role whose scope is `Platform`.
- `WorkspaceMembershipRole.RoleId` must reference a role whose scope is `Workspace`.
- A workspace role assigned to a membership must belong to the same workspace as that membership.

These invariants are documented here instead of implemented with database triggers or complex constraints in this foundation step.

## Deferred Decisions

- Permission Group semantics.
- Role and permission seed strategy.
- Initial workspace seed strategy.
- Authentication.
- JWT and token strategy.
- Authorization policies.
- Claims strategy.
- Workspace deletion lifecycle.
- Membership lifecycle or status.
- Access request re-request rules.
- Audit integration.
- Save and transaction boundary.
