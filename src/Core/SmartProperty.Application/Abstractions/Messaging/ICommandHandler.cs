using System.Threading;
using System.Threading.Tasks;
using SmartProperty.Common.Results;

namespace SmartProperty.Application.Abstractions.Messaging;

public interface ICommandHandler<in TCommand>
    where TCommand : ICommand
{
    Task<Result> Handle(TCommand command, CancellationToken cancellationToken = default);
}
