using System.Threading;
using System.Threading.Tasks;
using SmartProperty.Common.Results;

namespace SmartProperty.Application.Abstractions.Messaging;

public interface ICommandHandler<in TCommand, TResponse>
    where TCommand : ICommand<TResponse>
{
    Task<Result<TResponse>> Handle(TCommand command, CancellationToken cancellationToken = default);
}
