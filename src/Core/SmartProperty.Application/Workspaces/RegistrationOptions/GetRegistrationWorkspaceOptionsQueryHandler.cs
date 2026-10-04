using SmartProperty.Application.Abstractions.Messaging;
using SmartProperty.Application.Abstractions.Persistence.Repositories;
using SmartProperty.Common.Results;

namespace SmartProperty.Application.Workspaces.RegistrationOptions;

/// <summary>
/// Returns the workspaces registration can target. Read-only: it saves nothing.
/// </summary>
/// <remarks>
/// It exists because registration requires an existing workspace id, and until this query an anonymous caller had
/// no way to discover one.
///
/// The domain has no rule that closes a workspace to registration, so every persisted workspace is an option —
/// exactly the set <c>RegisterCommandHandler</c> accepts. No such rule is invented here.
/// </remarks>
public sealed class GetRegistrationWorkspaceOptionsQueryHandler(
    IWorkspaceRepository workspaceRepository)
    : IQueryHandler<GetRegistrationWorkspaceOptionsQuery, IReadOnlyList<RegistrationWorkspaceOption>>
{
    public async Task<Result<IReadOnlyList<RegistrationWorkspaceOption>>> Handle(
        GetRegistrationWorkspaceOptionsQuery query,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);

        var options = await workspaceRepository.ListRegistrationOptionsAsync(cancellationToken);

        return Result<IReadOnlyList<RegistrationWorkspaceOption>>.Success(options);
    }
}
