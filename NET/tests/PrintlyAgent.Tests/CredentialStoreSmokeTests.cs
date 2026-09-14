using System.Text.Json;
using PrintlyAgent.Credentials;
using Xunit;

namespace PrintlyAgent.Tests;

/// <summary>
/// Real Windows Credential Manager round-trip - not a mock. Port of
/// CredentialStoreSmokeTest.kt.
///
/// Confirms the P/Invoke bindings in CredentialStore actually work against the
/// live OS API on this machine, not just that they compile. That distinction is
/// the entire point of the file: a wrong struct layout or a wrong blob encoding
/// compiles perfectly and fails only at runtime, on a shop counter.
///
/// Deliberately uses its own target name rather than SaveAgentCredential /
/// SaveOwnerSession: those write to the targets the running agent reads, and
/// clearing them in teardown really does sign the shop owner out and unpair the
/// PC - which then dead-ends the next sign-in on PRINT_AGENT_ALREADY_ACTIVE,
/// since the backend still holds the old pairing. A test must not have side
/// effects on a live install.
/// </summary>
public class CredentialStoreSmokeTests
{
    private const string TestTarget = "PrintlyAgentNet-test/smoke";

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    [Fact(DisplayName = "a credential round-trips through the real Windows Credential Manager")]
    public void ACredentialRoundTripsThroughTheRealWindowsCredentialManager()
    {
        var original = new AgentCredential("smoke-test-agent", "smoke-test-shop", "smoke-test-secret");
        try
        {
            CredentialStore.Write(TestTarget, JsonSerializer.Serialize(original, Json));
            var raw = CredentialStore.Read(TestTarget);
            var loaded = raw is null ? null : JsonSerializer.Deserialize<AgentCredential>(raw, Json);
            Assert.Equal(original, loaded);
        }
        finally
        {
            CredentialStore.Delete(TestTarget);
        }
        Assert.Null(CredentialStore.Read(TestTarget));
    }

    [Fact(DisplayName = "unicode and punctuation survive the UTF-16LE blob round-trip")]
    public void UnicodeAndPunctuationSurviveTheBlobRoundTrip()
    {
        // Tokens are base64url and JSON-escaped, but the blob encoding is the one
        // place a subtle corruption would go unnoticed until sign-in broke.
        const string payload = """{"token":"a/b+c=ü€","note":"line1\nline2"}""";
        try
        {
            CredentialStore.Write(TestTarget, payload);
            Assert.Equal(payload, CredentialStore.Read(TestTarget));
        }
        finally
        {
            CredentialStore.Delete(TestTarget);
        }
    }

    /// <summary>
    /// Reading something never written is the ordinary first-run answer, not a
    /// failure - the agent asks this on every start to decide whether it is
    /// already paired.
    /// </summary>
    [Fact(DisplayName = "a target that was never written reads as nothing rather than failing")]
    public void ATargetNeverWrittenReadsAsNothing()
    {
        Assert.Null(CredentialStore.Read("PrintlyAgentNet-test/never-written"));
    }

    /// <summary>Deleting twice is a no-op, same as signing out twice.</summary>
    [Fact(DisplayName = "clearing a credential that was never set is harmless")]
    public void ClearingACredentialThatWasNeverSetIsHarmless()
    {
        CredentialStore.Delete("PrintlyAgentNet-test/never-written");
        CredentialStore.Delete("PrintlyAgentNet-test/never-written");
    }
}
