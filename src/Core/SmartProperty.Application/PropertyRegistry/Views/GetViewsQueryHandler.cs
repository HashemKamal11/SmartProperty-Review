using SmartProperty.Application.Abstractions.Messaging;
using SmartProperty.Application.Abstractions.Persistence.Repositories;
using SmartProperty.Common.Results;

namespace SmartProperty.Application.PropertyRegistry.Views;

public sealed class GetViewsQueryHandler(IViewRepository viewRepository)
    : IQueryHandler<GetViewsQuery, IReadOnlyList<ViewDetails>>
{
    public async Task<Result<IReadOnlyList<ViewDetails>>> Handle(
        GetViewsQuery query,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);

        return Result<IReadOnlyList<ViewDetails>>.Success(await viewRepository.ListAsync(cancellationToken));
    }
}
