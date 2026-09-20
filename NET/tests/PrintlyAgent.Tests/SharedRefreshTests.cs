using PrintlyAgent.Core;
using Xunit;

namespace PrintlyAgent.Tests;

/// <summary>
/// An expired owner token does not expire for one caller and not the others.
///
/// Port of core/SharedRefreshTest.kt.
///
/// Intake is looking several orders up at once, the shop's screen calls in on
/// its own thread, and the order-events stream is rejected mid-flight - so they
/// all discover it in the same instant and all used to refresh.
///
/// A backend that rotates refresh tokens honours the first of those and rejects
/// the rest, which can take the session down with it. The agent then goes on
/// heartbeating on its own separate credential - reporting itself connected -
/// while every order lookup fails, every failed lookup is read as "ask again
/// later", and nothing is ever picked up.
/// </summary>
public class SharedRefreshTests
{
    [Fact(DisplayName = "callers that hit the same expired token together refresh once")]
    public async Task CallersThatHitTheSameExpiredTokenTogetherRefreshOnce()
    {
        var token = "old";
        var refreshes = 0;
        var shared = new SharedRefresh(() => Volatile.Read(ref token!), async () =>
        {
            Interlocked.Increment(ref refreshes);
            // A refresh is a network call; the others pile up behind it.
            await Task.Delay(20);
            Volatile.Write(ref token!, "new");
        });

        const int callers = 8;
        using var together = new Barrier(callers);
        var racing = Enumerable.Range(0, callers).Select(_ => Task.Run(async () =>
        {
            together.SignalAndWait();
            await shared.PastAsync("old");
        }));

        await Task.WhenAll(racing);

        Assert.Equal(1, refreshes);
        Assert.Equal("new", token);
    }

    /// <summary>A caller arriving later finds the work done and spends nothing.</summary>
    [Fact(DisplayName = "a token already refreshed past is not refreshed again")]
    public async Task ATokenAlreadyRefreshedPastIsNotRefreshedAgain()
    {
        var refreshes = 0;
        var shared = new SharedRefresh(() => "new", () =>
        {
            refreshes++;
            return Task.CompletedTask;
        });

        await shared.PastAsync("old");

        Assert.Equal(0, refreshes);
    }

    /// <summary>But a genuinely new expiry still refreshes - this must not latch.</summary>
    [Fact(DisplayName = "a later expiry is refreshed on its own account")]
    public async Task ALaterExpiryIsRefreshedOnItsOwnAccount()
    {
        var token = "first";
        var refreshes = 0;
        var shared = new SharedRefresh(() => token, () =>
        {
            token = $"token-{++refreshes}";
            return Task.CompletedTask;
        });

        await shared.PastAsync("first");
        Assert.Equal(1, refreshes);

        await shared.PastAsync(token);
        Assert.Equal(2, refreshes);
    }
}
