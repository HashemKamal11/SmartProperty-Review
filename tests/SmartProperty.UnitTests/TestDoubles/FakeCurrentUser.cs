using SmartProperty.Application.Abstractions.Identity;

namespace SmartProperty.UnitTests.TestDoubles;

internal sealed class FakeCurrentUser(Guid? userId) : ICurrentUser
{
    public Guid? UserId { get; } = userId;

    public bool IsAuthenticated => UserId is not null;
}
