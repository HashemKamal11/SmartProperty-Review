using System.Reflection;
using System.Runtime.ExceptionServices;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Xunit;

namespace SmartProperty.Persistence.IntegrationTests.Concurrency;

public class P008RepositoryReadBarrier<TRepository, TValue> : DispatchProxy where TRepository : class
{
    private TRepository target = null!;
    private string methodName = string.Empty;
    private P008Checkpoint checkpoint = null!;
    private int armed = 1;

    public static TRepository Wrap(TRepository target, string methodName, P008Checkpoint checkpoint)
    {
        var proxy = Create<TRepository, P008RepositoryReadBarrier<TRepository, TValue>>();
        var state = (P008RepositoryReadBarrier<TRepository, TValue>)(object)proxy;
        state.target = target;
        state.methodName = methodName;
        state.checkpoint = checkpoint;
        return proxy;
    }

    protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
    {
        ArgumentNullException.ThrowIfNull(targetMethod);
        if (targetMethod.Name == methodName && Interlocked.Exchange(ref armed, 0) == 1)
        {
            if (targetMethod.ReturnType != typeof(Task<TValue>))
                throw new InvalidOperationException("P008 repository barrier requires a matching Task<T> signature.");
            return InvokePausedAsync(targetMethod, args);
        }
        return Forward(targetMethod, args);
    }

    private async Task<TValue> InvokePausedAsync(MethodInfo method, object?[]? args)
    {
        await checkpoint.PauseAsync();
        return await (Task<TValue>)Forward(method, args)!;
    }

    private object? Forward(MethodInfo method, object?[]? args)
    {
        try { return method.Invoke(target, args); }
        catch (TargetInvocationException exception) when (exception.InnerException is not null)
        {
            ExceptionDispatchInfo.Capture(exception.InnerException).Throw();
            throw;
        }
    }
}

public sealed class P008Checkpoint
{
    private readonly TaskCompletionSource reached = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource released = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public Task Reached => reached.Task;
    public void Release() => released.TrySetResult();

    public async Task PauseAsync(CancellationToken cancellationToken = default)
    {
        reached.TrySetResult();
        await released.Task.WaitAsync(TimeSpan.FromSeconds(20), cancellationToken);
    }
}

internal sealed class P008BeforeSaveBarrier(P008Checkpoint checkpoint) : SaveChangesInterceptor
{
    private int armed = 1;

    public override async ValueTask<InterceptionResult<int>> SavingChangesAsync(
        DbContextEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
    {
        if (Interlocked.Exchange(ref armed, 0) == 1)
            await checkpoint.PauseAsync(cancellationToken);
        return result;
    }
}

public sealed class P008RepositoryReadBarrierTests
{
    [Fact]
    public async Task SelectedReadWaitsBeforeForwardingAndOtherMethodsRemainUnchanged()
    {
        var target = new Probe();
        var checkpoint = new P008Checkpoint();
        var proxy = P008RepositoryReadBarrier<IProbe, string>.Wrap(target, nameof(IProbe.ReadAsync), checkpoint);
        Assert.Equal(7, await proxy.OtherAsync());
        var reading = proxy.ReadAsync();
        try
        {
            await checkpoint.Reached.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(0, target.Calls);
            Assert.False(reading.IsCompleted);
        }
        finally { checkpoint.Release(); }
        Assert.Equal("persisted", await reading.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.Equal("persisted", await proxy.ReadAsync());
        Assert.Equal(2, target.Calls);
    }

    [Fact]
    public async Task RepositoryExceptionIsPreservedWithoutReflectionWrapping()
    {
        var expected = new InvalidOperationException("Repository failure");
        var checkpoint = new P008Checkpoint();
        var proxy = P008RepositoryReadBarrier<IProbe, string>.Wrap(new Probe(expected), nameof(IProbe.ReadAsync), checkpoint);
        checkpoint.Release();
        var actual = await Assert.ThrowsAsync<InvalidOperationException>(() => proxy.ReadAsync());
        Assert.Same(expected, actual);
    }

    public interface IProbe
    {
        Task<string> ReadAsync();
        Task<int> OtherAsync();
    }

    private sealed class Probe(Exception? failure = null) : IProbe
    {
        public int Calls { get; private set; }
        public Task<int> OtherAsync() => Task.FromResult(7);
        public Task<string> ReadAsync()
        {
            Calls++;
            if (failure is not null) throw failure;
            return Task.FromResult("persisted");
        }
    }
}
