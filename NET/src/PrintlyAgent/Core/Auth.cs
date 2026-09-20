using System.Reflection;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using PrintlyAgent.Credentials;
using PrintlyAgent.Net;

namespace PrintlyAgent.Core;

public sealed class NoOwnedShopError : Exception
{
    public NoOwnedShopError(string message) : base(message) { }
}

public sealed class PasswordNotSetError : Exception
{
    public PasswordNotSetError(string message) : base(message) { }
}

/// <summary>
/// A different PC already holds this shop's one active agent slot - never
/// silently taken over.
/// </summary>
public sealed class AnotherMachinePairedError : Exception
{
    public AnotherMachinePairedError(string message) : base(message) { }
}

/// <summary>
/// Owner login and first-run pairing - port of core/Auth.kt.
///
/// Two ways in, both ending at the same <see cref="OwnerSession"/>: email/phone
/// + password (the normal, daily way in), or the MSG91 OTP widget (first-run
/// only, to prove phone ownership before a password can be set).
/// </summary>
public sealed class Auth
{
    /// <summary>
    /// The version this agent reports to the backend, on every heartbeat and
    /// at pairing - so it is what the shop and anyone helping them actually
    /// sees when asking "which build is that counter running?".
    ///
    /// Read from the assembly rather than written here. It was a literal, and
    /// it said 0.1.0 for a build stamped 1.0.0: a second place to remember is a
    /// place that gets forgotten, and the one number support relies on is a bad
    /// one to have wrong.
    /// </summary>
    public static readonly string AgentVersion = ResolveVersion();

    private static string ResolveVersion()
    {
        var informational = typeof(Auth).Assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;

        // InformationalVersion carries build metadata after a '+' (the source
        // revision, when the build supplies one). The backend wants a version,
        // not a commit.
        if (!string.IsNullOrWhiteSpace(informational))
        {
            var plus = informational.IndexOf('+');
            return plus < 0 ? informational : informational[..plus];
        }

        return typeof(Auth).Assembly.GetName().Version?.ToString(3) ?? "0.0.0";
    }

    private readonly ILogger _log;

    public Auth(ILogger log) => _log = log;

    private OwnerSession EstablishSession(Dictionary<string, object?> body)
    {
        // The response arrives as loosely-typed JSON on both sides. Reading it
        // defensively rather than binding to a DTO keeps the port faithful: the
        // Kotlin casts through Map<String, Any?> for the same reason, because
        // this endpoint is shared with clients that need different slices of it.
        var tokens = AsElement(body, "tokens");
        var user = AsElement(body, "user");

        var ownedShopIds = new List<string>();
        if (user.TryGetProperty("ownedShopIds", out var ids) && ids.ValueKind == JsonValueKind.Array)
        {
            foreach (var id in ids.EnumerateArray())
            {
                if (id.ValueKind == JsonValueKind.String) ownedShopIds.Add(id.GetString()!);
            }
        }

        if (ownedShopIds.Count == 0)
        {
            throw new NoOwnedShopError(
                "This account does not own a shop. Staff accounts are not yet supported here.");
        }

        var session = new OwnerSession(
            AccessToken: tokens.GetProperty("accessToken").GetString()!,
            RefreshToken: tokens.GetProperty("refreshToken").GetString()!,
            ShopId: ownedShopIds[0],
            ShopName: null);

        CredentialStore.SaveOwnerSession(session);
        return session;
    }

    private static JsonElement AsElement(Dictionary<string, object?> body, string key)
    {
        if (!body.TryGetValue(key, out var value) || value is null)
        {
            throw new InvalidOperationException($"the sign-in response had no \"{key}\"");
        }
        return value is JsonElement element
            ? element
            : JsonSerializer.SerializeToElement(value);
    }

    public async Task<OwnerSession> LoginWithPasswordAsync(
        PrintlyApiClient api, string identifier, string password, CancellationToken ct = default)
    {
        Dictionary<string, object?> body;
        try
        {
            body = await api.LoginWithPasswordAsync(identifier, password, ct).ConfigureAwait(false);
        }
        catch (ApiError exc) when (exc.Code == "PASSWORD_NOT_SET")
        {
            throw new PasswordNotSetError(
                "No password has been set for this account yet. Verify by phone, then set one.");
        }

        var session = EstablishSession(body);
        _log.LogInformation("owner_signed_in method=password");
        return session;
    }

    public async Task<OwnerSession> FinishLoginAsync(
        PrintlyApiClient api, string widgetAccessToken, CancellationToken ct = default)
    {
        var body = await api.VerifyWidgetAsync(widgetAccessToken, ct).ConfigureAwait(false);
        var session = EstablishSession(body);
        _log.LogInformation("owner_signed_in method=otp");
        return session;
    }

    public async Task SetPasswordAsync(
        PrintlyApiClient api, OwnerSession session, string password, CancellationToken ct = default)
    {
        await api.SetPasswordAsync(session, password, ct).ConfigureAwait(false);
        _log.LogInformation("owner_password_set");
    }

    /// <summary>
    /// Idempotent: returns the existing agent credential if this PC has already
    /// been paired to this shop, otherwise pairs and exchanges silently - no
    /// code for the owner to copy between two apps, since this app is both the
    /// one minting the code and the one consuming it.
    /// </summary>
    public async Task<AgentCredential> EnsurePairedAsync(
        PrintlyApiClient api, OwnerSession session, CancellationToken ct = default)
    {
        var existing = CredentialStore.LoadAgentCredential();
        if (existing is not null
            && existing.ShopId == session.ShopId
            && await StillValidAsync(api, existing, ct).ConfigureAwait(false))
        {
            return existing;
        }

        try
        {
            return await PairAndExchangeAsync(api, session, ct).ConfigureAwait(false);
        }
        catch (ApiError exc) when (exc.Code == "PRINT_AGENT_ALREADY_ACTIVE")
        {
            await RevokeStaleRegistrationForThisMachineAsync(api, session, ct).ConfigureAwait(false);
            return await PairAndExchangeAsync(api, session, ct).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Whether a stored credential still works, asked of the server rather than
    /// assumed.
    ///
    /// Holding one is not the same as being paired. The owner can revoke this
    /// machine from the dashboard, or another PC can take the shop's
    /// registration - and the stored secret looks exactly the same afterwards.
    /// Trusting it on sight left the agent retrying a dead credential every ten
    /// seconds forever, with no way to recover from inside the app: re-pairing
    /// returned the same rejected credential it already had.
    ///
    /// A rejection is the only thing that discards it. A network failure
    /// deliberately does not: the credential is probably fine, the connection is
    /// not, and throwing away a working pairing because the wifi dropped would
    /// turn a blip into an unpairing.
    /// </summary>
    private async Task<bool> StillValidAsync(
        PrintlyApiClient api, AgentCredential credential, CancellationToken ct)
    {
        try
        {
            await api.HeartbeatAsync(credential, AgentVersion, ct).ConfigureAwait(false);
            return true;
        }
        catch (ApiError exc)
        {
            var rejected = exc.StatusCode is 401 or 403;
            if (rejected)
            {
                _log.LogInformation("stored_agent_credential_rejected code={Code} - pairing again", exc.Code);
                CredentialStore.ClearAgentCredential();
            }
            return !rejected;
        }
        catch (Exception exc)
        {
            _log.LogDebug(exc, "agent_credential_check_unreachable");
            return true;
        }
    }

    private async Task<AgentCredential> PairAndExchangeAsync(
        PrintlyApiClient api, OwnerSession session, CancellationToken ct)
    {
        var pairing = await api.PairAsync(session, ct).ConfigureAwait(false);
        var exchanged = await api.ExchangeAsync(
            pairing.Code, HostName(), MachineFingerprint(), AgentVersion, ct).ConfigureAwait(false);

        var credential = new AgentCredential(exchanged.AgentId, exchanged.ShopId, exchanged.Secret);
        CredentialStore.SaveAgentCredential(credential);
        _log.LogInformation(
            "agent_paired agentId={AgentId} shopId={ShopId}", credential.AgentId, credential.ShopId);
        return credential;
    }

    /// <summary>
    /// Clears a registration this same PC left behind - the backend allows one
    /// ACTIVE agent per shop, so a local credential that has gone missing
    /// (reinstall, cleared credential store, wiped profile) otherwise locks the
    /// owner out of ever signing in here again: pairing refuses with
    /// PRINT_AGENT_ALREADY_ACTIVE and no UI path exists to clear it.
    ///
    /// Only ever revokes a registration whose name matches this machine's own
    /// hostname. A *different* PC holding the pairing is someone else's live
    /// agent, and silently stealing it would take that shop's printing offline
    /// with no warning - the backend's own "explicit revoke-then-pair, never a
    /// silent swap" rule. That case is surfaced to the owner instead.
    /// </summary>
    private async Task RevokeStaleRegistrationForThisMachineAsync(
        PrintlyApiClient api, OwnerSession session, CancellationToken ct)
    {
        var agents = await api.ListPrintAgentsAsync(session, ct).ConfigureAwait(false);
        var active = agents.FirstOrDefault(a => a.Status == "ACTIVE");
        // Nothing active after all - a concurrent revoke won the race; retrying
        // is correct.
        if (active is null) return;

        if (!string.Equals(active.Name, HostName(), StringComparison.OrdinalIgnoreCase))
        {
            throw new AnotherMachinePairedError(
                $"\"{active.Name}\" is already this shop's print agent. Disconnect it there first, " +
                "or revoke it from the shop dashboard, then sign in again here.");
        }

        _log.LogInformation(
            "revoking_stale_self_registration agentId={AgentId} name={Name}", active.Id, active.Name);
        await api.RevokePrintAgentAsync(session, active.Id, ct).ConfigureAwait(false);
    }

    internal static string HostName()
    {
        try { return Environment.MachineName; }
        catch (InvalidOperationException) { return "Print Agent"; }
    }

    /// <summary>
    /// Not a security boundary - purely diagnostic. A fresh random id, never the
    /// real machine identity.
    /// </summary>
    private static string MachineFingerprint() => Guid.NewGuid().ToString("N");
}
