using SmartProperty.Application.Abstractions.Authorization;

namespace SmartProperty.UnitTests.TestDoubles;

internal sealed class FakeAccessContextReader : IAccessContextReader
{
    public AccessContext Context { get; set; } = new([], [], []);

    public int ReadCallCount { get; private set; }

    public Guid? LastUserId { get; private set; }

    public CancellationToken LastCancellationToken { get; private set; }

    public Task<AccessContext> ReadAsync(Guid userId, CancellationToken cancellationToken = default)
    {
        ReadCallCount++;
        LastUserId = userId;
        LastCancellationToken = cancellationToken;
        return Task.FromResult(Context);
    }
}
