using TarkovCompanion.Application.Services.Execution;

namespace TarkovCompanion.UnitTests.Runtime;

public sealed class LatestOperationGateTests
{
    [Fact]
    public void BeginReturnsALeaseWhoseTokenIsCancelledByTheNextBeginForTheSameScope()
    {
        using var gate = new LatestOperationGate();
        var scope = new OperationScopeId("test-scope");

        var first = gate.Begin(scope);
        Assert.True(gate.IsCurrent(first));
        Assert.False(first.CancellationToken.IsCancellationRequested);

        var second = gate.Begin(scope);
        Assert.False(gate.IsCurrent(first));
        Assert.True(gate.IsCurrent(second));
        Assert.True(first.CancellationToken.IsCancellationRequested);
    }

    [Fact]
    public void TryCommitPublishesOnlyForTheCurrentLease()
    {
        using var gate = new LatestOperationGate();
        var scope = new OperationScopeId("test-scope");

        var first = gate.Begin(scope);
        var _ = gate.Begin(scope);

        var published = false;
        Assert.False(gate.TryCommit(first, () => published = true));
        Assert.False(published);
    }

    [Fact]
    public void InvalidatesCancelThePreviousLease()
    {
        using var gate = new LatestOperationGate();
        var scope = new OperationScopeId("test-scope");

        var lease = gate.Begin(scope);
        Assert.False(lease.CancellationToken.IsCancellationRequested);

        gate.Invalidate(scope);
        Assert.True(lease.CancellationToken.IsCancellationRequested);
        Assert.False(gate.IsCurrent(lease));
    }

    [Fact]
    public void DisposesCancelAllActiveLeasesAndPreventsNewBegins()
    {
        var gate = new LatestOperationGate();
        var scope1 = new OperationScopeId("scope-1");
        var scope2 = new OperationScopeId("scope-2");

        var lease1 = gate.Begin(scope1);
        var lease2 = gate.Begin(scope2);
        Assert.False(lease1.CancellationToken.IsCancellationRequested);
        Assert.False(lease2.CancellationToken.IsCancellationRequested);

        gate.Dispose();
        Assert.True(lease1.CancellationToken.IsCancellationRequested);
        Assert.True(lease2.CancellationToken.IsCancellationRequested);
        Assert.Throws<ObjectDisposedException>(() => gate.Begin(scope1));
    }

    /// <summary>
    /// A cancellation callback on a lease must not deadlock when it calls back into the gate
    /// (IsCurrent or TryCommit). Without the fix, Cancel ran inside lock(_gate), so the
    /// callback would block on acquiring the same lock.
    /// </summary>
    [Fact]
    public void CancellationCallbackCanCallIsCurrentWithoutDeadlocking()
    {
        using var gate = new LatestOperationGate();
        var scope = new OperationScopeId("reentrant-scope");

        var first = gate.Begin(scope);
        var callbackRan = false;

        // Register a callback that calls back into the gate while the previous lease is cancelled.
        first.CancellationToken.Register(() =>
        {
            // This would deadlock if Cancel() ran under _gate.
            callbackRan = gate.IsCurrent(first) == false;
        });

        // Replacing the entry cancels the first lease and fires its callback.
        gate.Begin(scope);

        Assert.True(callbackRan);
    }

    /// <summary>
    /// A throwing cancellation callback must not leave the new lease absent from the gate or
    /// prevent later operations from working. With the cancel outside the lock, the new entry
    /// was already added before the throw, so it survives.
    /// </summary>
    [Fact]
    public void ThrowingCancellationCallbackDoesNotCorruptGateState()
    {
        using var gate = new LatestOperationGate();
        var scope = new OperationScopeId("throwing-scope");

        var first = gate.Begin(scope);
        first.CancellationToken.Register(() => throw new InvalidOperationException("callback threw"));

        // Begin with a throwing callback must not propagate the throw.
        var second = gate.Begin(scope);
        Assert.True(gate.IsCurrent(second));

        var published = false;
        Assert.True(gate.TryCommit(second, () => published = true));
        Assert.True(published);
    }

    /// <summary>
    /// Dispose with a throwing cancellation callback must cancel all remaining entries and not
    /// leave any leaked or uncancelled sources.
    /// </summary>
    [Fact]
    public void DisposeWithThrowingCallbackStillCancelsRemainingEntries()
    {
        var gate = new LatestOperationGate();
        var scope1 = new OperationScopeId("scope-a");
        var scope2 = new OperationScopeId("scope-b");

        var lease1 = gate.Begin(scope1);
        var lease2 = gate.Begin(scope2);

        lease1.CancellationToken.Register(() => throw new InvalidOperationException("scope-a threw"));

        // Dispose must not propagate the throw; both leases should still be cancelled.
        gate.Dispose();
        Assert.True(lease1.CancellationToken.IsCancellationRequested);
        Assert.True(lease2.CancellationToken.IsCancellationRequested);
    }

    [Fact]
    public void DifferentScopesAreIndependent()
    {
        using var gate = new LatestOperationGate();
        var scope1 = new OperationScopeId("scope-x");
        var scope2 = new OperationScopeId("scope-y");

        var lease1 = gate.Begin(scope1);
        var lease2 = gate.Begin(scope2);

        gate.Begin(scope1);

        Assert.True(lease1.CancellationToken.IsCancellationRequested);
        Assert.False(lease2.CancellationToken.IsCancellationRequested);
        Assert.True(gate.IsCurrent(lease2));
    }
}
