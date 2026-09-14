using PrintlyAgent.Core;
using Xunit;

namespace PrintlyAgent.Tests;

/// <summary>
/// The version the agent tells the backend about itself.
///
/// It used to be a literal in Auth, and it drifted: the assembly was stamped
/// 1.0.0 for release while every heartbeat kept reporting 0.1.0. Nothing broke,
/// which is the problem - it would simply have misled the first person trying to
/// work out which build a shop counter was running.
/// </summary>
public class VersionTests
{
    [Fact(DisplayName = "the version reported to the backend is the version that was built")]
    public void TheReportedVersionMatchesTheAssembly()
    {
        var assembly = typeof(Auth).Assembly.GetName().Version;
        Assert.NotNull(assembly);

        // Compared on major.minor.patch: the assembly version carries a fourth
        // component that a semantic version does not.
        Assert.Equal(assembly!.ToString(3), Auth.AgentVersion);
    }

    [Fact(DisplayName = "the version carries no build metadata the backend would have to parse")]
    public void TheReportedVersionIsPlain()
    {
        Assert.DoesNotContain("+", Auth.AgentVersion);
        Assert.NotEqual("0.0.0", Auth.AgentVersion);
    }
}
