using SmartProperty.Application.Abstractions.Messaging;
using SmartProperty.Common.Pagination;

namespace SmartProperty.Application.Workspaces.Administration.List;

public sealed record GetAdminWorkspacesQuery(int? Page = null, int? PageSize = null) : IQuery<PagedList<AdminWorkspaceDetails>>;