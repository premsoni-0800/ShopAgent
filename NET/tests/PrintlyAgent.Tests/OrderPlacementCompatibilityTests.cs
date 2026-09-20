using PrintlyAgent.Core;
using Xunit;

namespace PrintlyAgent.Tests;

/// <summary>
/// Which orders the agent treats as real, across a backend it may be newer or
/// older than.
///
/// This agent is installed on a shop's counter PC and updates on its own
/// schedule, so "the server is a version behind" and "the counter is a version
/// behind" are both ordinary states that last for months. The field that says an
/// order was placed moved from <c>paidAt</c> to <c>placedAt</c> when online
/// payment was removed, and reading only one of them would empty a shop's board
/// on whichever side updated first.
///
/// Emptying the board is the worst direction for this to fail in: a shop with no
/// orders on screen has no reason to suspect a version skew, and every reason to
/// think Printly has stopped working.
/// </summary>
public class OrderPlacementCompatibilityTests
{
    private static Dictionary<string, object?> Order(object? placedAt, object? paidAt)
    {
        var order = new Dictionary<string, object?>();
        if (placedAt is not null) order["placedAt"] = placedAt;
        if (paidAt is not null) order["paidAt"] = paidAt;
        return order;
    }

    [Fact]
    public void ANewBackendSendsPlacedAtAndTheOrderCounts()
    {
        Assert.True(AgentCore.IsPlaced(Order(placedAt: "2026-09-17T10:00:00Z", paidAt: null)));
    }

    [Fact]
    public void AnOlderBackendSendsOnlyPaidAtAndTheOrderStillCounts()
    {
        // The live case the moment a counter updates ahead of the server.
        Assert.True(AgentCore.IsPlaced(Order(placedAt: null, paidAt: "2026-09-17T10:00:00Z")));
    }

    [Fact]
    public void AnOrderCarryingBothCounts()
    {
        // What a pre-existing order looks like after the migration backfills it.
        Assert.True(AgentCore.IsPlaced(Order("2026-09-17T10:00:00Z", "2026-09-17T10:00:00Z")));
    }

    [Fact]
    public void AnAbandonedUploadDoesNotCount()
    {
        // The row exists because creation has to write it before it can place
        // it. Nobody owes any work for this one.
        Assert.False(AgentCore.IsPlaced(Order(placedAt: null, paidAt: null)));
    }

    [Fact]
    public void ExplicitNullsAreNotMistakenForAValue()
    {
        // The JSON carries the keys with null values rather than omitting them,
        // which a TryGetValue-only check would read as "present".
        var order = new Dictionary<string, object?> { ["placedAt"] = null, ["paidAt"] = null };
        Assert.False(AgentCore.IsPlaced(order));
    }
}
