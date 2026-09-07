using System.Threading;
using System.Threading.Tasks;
using SmartProperty.Common.Results;

namespace SmartProperty.Application.Abstractions.Messaging;

public interface IQueryHandler<in TQuery, TResponse>
    where TQuery : IQuery<TResponse>
{
    Task<Result<TResponse>> Handle(TQuery query, CancellationToken cancellationToken = default);
}
