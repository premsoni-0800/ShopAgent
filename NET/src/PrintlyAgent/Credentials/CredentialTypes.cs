using System.Text.Json.Serialization;

namespace PrintlyAgent.Credentials;

/// <summary>
/// The shop owner's own session, as signed in on this machine.
///
/// Port of the OwnerSession half of credentials/CredentialStore.kt. Only ever
/// used against owner-facing routes - the backend has two disjoint
/// authentication filters and presenting this at a device route is a 401.
/// </summary>
public sealed record OwnerSession(
    string AccessToken,
    string RefreshToken,
    string ShopId,
    string? ShopName);

/// <summary>
/// This machine's registration as the shop's print agent.
///
/// Port of the AgentCredential half of credentials/CredentialStore.kt.
/// </summary>
public sealed record AgentCredential(string AgentId, string ShopId, string Secret)
{
    /// <summary>
    /// Derived, never persisted.
    ///
    /// [JsonIgnore] carries over the Kotlin @get:JsonIgnore for the same reason
    /// it was needed there: written out, it would come back as an unrecognised
    /// property on the next read and the stored credential would fail to
    /// deserialize - which reads to the shop as "this PC is no longer paired".
    /// </summary>
    [JsonIgnore]
    public string BearerToken => $"{AgentId}.{Secret}";
}
