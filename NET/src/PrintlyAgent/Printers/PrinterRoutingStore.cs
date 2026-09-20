using System.Text.Json;
using PrintlyAgent.Db;

namespace PrintlyAgent.Printers;

/// <summary>
/// Where the shop's choice of "colour goes to that one, black and white to this
/// one" is kept.
///
/// <para>
/// Machine-local, in the <c>agent_state</c> table, and deliberately not a shop
/// setting on the backend. It names Windows printers by the names they have on
/// <em>this</em> PC, which mean nothing anywhere else - a second machine at the
/// same shop has its own printers and needs its own answer. It is also the kind
/// of setting somebody changes by walking over and unplugging something, so it
/// has to keep working with the internet down.
/// </para>
///
/// <para>
/// Read on every selection rather than cached. It is one indexed row from a
/// local SQLite file, next to a print job that is about to take seconds, and a
/// cache here would mean the owner changing the setting and the next order still
/// going to the old printer with nothing to explain why.
/// </para>
/// </summary>
public static class PrinterRoutingStore
{
    /// <summary>The <c>agent_state</c> key. Never reused for anything else.</summary>
    internal const string Key = "printer_routing";

    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    /// <summary>
    /// What the shop chose, or <see cref="PrinterRouting.None"/> if it has not
    /// chosen or the stored value can no longer be read.
    ///
    /// Never throws. A corrupt row is not a reason to stop printing: falling
    /// back to capability-based selection prints the order on the best machine
    /// available, which is what happened before anyone set this at all.
    /// </summary>
    public static PrinterRouting Read(Database db)
    {
        try
        {
            var raw = db.GetState(Key);
            if (string.IsNullOrWhiteSpace(raw)) return PrinterRouting.None;
            return JsonSerializer.Deserialize<PrinterRouting>(raw, Json) ?? PrinterRouting.None;
        }
        catch (JsonException)
        {
            return PrinterRouting.None;
        }
    }

    /// <summary>
    /// Records the shop's choice.
    ///
    /// Blank means "decide it from the capabilities", so it is stored as null
    /// rather than as an empty string that would later be looked up as a printer
    /// name and never match.
    /// </summary>
    public static void Write(Database db, PrinterRouting routing)
    {
        var cleaned = new PrinterRouting(
            Colour: Blank(routing.Colour),
            BlackAndWhite: Blank(routing.BlackAndWhite),
            // Carried, not dropped. These say whether the agent may revise its
            // own choice later; losing them on the way to disk would turn every
            // automatic assignment into one the shop appeared to have made, and
            // the agent would then refuse to correct it when the printer went
            // away.
            ColourAuto: routing.ColourAuto,
            BlackAndWhiteAuto: routing.BlackAndWhiteAuto);
        db.SetState(Key, JsonSerializer.Serialize(cleaned, Json));
    }

    private static string? Blank(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
