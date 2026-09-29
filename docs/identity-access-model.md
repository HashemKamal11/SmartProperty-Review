# Identity Access Model

This document records the current identity and workspace-access model. Authentication and JWT are implemented (see [authentication-model.md](authentication-model.md)); platform permission enforcement, workspace access-request review, and the Platform Admin bootstrap are implemented (see [authorization-model.md](authorization-model.md)). Compliance APIs, the rest of the admin APIs, and a broader permission/role catalog remain deferred.

One rule this document states is worth repeating because the approval workflow depends on it: approving a workspace access request creates a `WorkspaceMembership` and **no** `WorkspaceMembershipRole`. Membership and role are separate grants, and nothing assigns a default workspace role.

## Core Decisions

- A user can access multiple workspaces through workspace memberships.
- A user can hold multiple roles inside the same workspace.
- Workspaces are dynamic database entities, not enum values.
- The current workspace names shown by the frontend are business data, not domain constants: Customer Workspace, Operations Workspace, Developer Workspace, Investor Workspace, Admin Workspace, and Compliance Workspace.
- Registration workspace selection creates a workspace access request only. It does not grant access.
- Listing and reviewing workspace access requests requires the persisted platform permission `workspace.access_requests.review`.
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

There are no direct User -> Permission assignments. Permission codes are database-driven machine-readable identifiers. The default-off Platform Admin bootstrap provisions only the review permission it needs; no broader permission catalog is seeded.

Roles aggregate permissions; permissions are what authorization checks. The two scopes stay separate: a workspace role never grants platform access, and a platform role is not automatically a workspace role. The naming convention for permission codes and the full enforcement design are documented separately in [authorization-model.md](authorization-model.md).

## Access Request Flow

The current registration and access workflow is:

```text
Register
    ->
Choose requested workspace
    ->
WorkspaceAccessRequest = Pending
    ->
Authorized permission holder reviews request
    ->
Approved / Rejected
```

Registration creates a `Pending` user, credential, and `Pending` access request for the selected existing Workspace in one unit of work. It grants no membership, role, permission, or token.

An authenticated caller holding `workspace.access_requests.review` can list the review queue and approve or reject a request. The reviewer id is never accepted from the route, query, headers, or body; Application reads it from `ICurrentUser`, which is populated from the validated access-token subject.

Approval records the decision, activates the applicant only when the account is `Pending`, and ensures a `WorkspaceMembership` exists for the requested Workspace. It reuses an existing membership and does **not** create a `WorkspaceMembershipRole` or assign any default role. A `Suspended` or `Deactivated` applicant is not reactivated.

Rejection records the decision and nothing else. It does not activate the user or create a membership, role, or permission grant; a rejected applicant that registered as `Pending` remains unable to sign in.

Self-approval is currently allowed when the applicant is already `Active` and independently holds the review permission. There is no reviewer-versus-applicant prohibition in the current use case. Whether that should remain allowed is a business decision; this document does not describe it as forbidden, and F-007 does not change the behavior.

## Assignment Invariants

The model uses explicit association entities:

- `UserPlatformRole` assigns a platform-scoped role to a user.
- `WorkspaceMembershipRole` assigns a workspace-scoped role to a workspace membership.
- `RolePermission` assigns permissions to roles.

Application workflows must enforce these cross-entity invariants before assignment:

- `UserPlatformRole.RoleId` must reference a role whose scope is `Platform`.
- `WorkspaceMembershipRole.RoleId` must reference a role whose scope is `Workspace`.
- A workspace role assigned to a membership must belong to the same workspace as that membership.

These invariants are enforced by the relevant Application workflows rather than database triggers or complex cross-table constraints.

## Deferred Decisions

- Permission Group semantics.
- Role and permission seed strategy.
- Additional Workspace provisioning beyond the deployment-time initial Workspace.
- Broader authorization policy and permission-catalog strategy.
- Workspace deletion lifecycle.
- Membership lifecycle or status.
- Access request re-request rules.
- Audit integration.
