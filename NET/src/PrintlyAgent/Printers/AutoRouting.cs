using PrintlyAgent.Models;
using PrintlyAgent.Printing;

namespace PrintlyAgent.Printers;

/// <summary>
/// Filling in the shop's printer routing for it.
///
/// <para>
/// No Kotlin counterpart - the Kotlin build leaves the routing blank until
/// somebody sets it. Blank works: <see cref="PrinterSelector.SelectPrinter"/>
/// scores the machine and picks on its own, so orders print whether or not
/// anyone has chosen anything. What blank does not do is <em>say</em> so. The
/// routing screen showed two empty boxes, which reads as a setting nobody has
/// configured yet, and a counter that has just installed the agent has no way
/// to tell "decided automatically" from "not working until you pick one".
/// </para>
///
/// <para>
/// So the agent commits to an answer and shows its working. The answer is not a
/// second opinion: it is obtained by asking the selector itself what it would
/// do with a colour job and with a black-and-white one, so automatic routing
/// and actual selection cannot drift apart - change the scoring and this
/// follows it, because it is the same call.
/// </para>
///
/// <para>
/// Only ever fills in what the shop has not. An entry a person chose is theirs,
/// and stays exactly as they left it even when the printer it names is
/// unplugged - see <see cref="PrinterRouting.ColourAuto"/>.
/// </para>
/// </summary>
public static class AutoRouting
{
    /// <summary>
    /// The job the selector is asked about.
    ///
    /// A4, single-sided, one copy: the ordinary order, and the one worth being
    /// right about. It is a probe rather than a real job, and nothing is
    /// printed from it - a routing entry is a preference, so an order that
    /// needs something this probe did not ask for (A3, duplex) still gets the
    /// right machine, because the selector passes over a routed printer that
    /// is proven incompatible with the job in front of it.
    /// </summary>
    private static PrintJobItem Probe(ColorMode colorMode) =>
        new(
            ItemId: "auto-routing-probe",
            DocumentId: "auto-routing-probe",
            FileName: "probe.pdf",
            PaperSize: PaperSize.A4,
            ColorMode: colorMode,
            DuplexMode: DuplexMode.SINGLE_SIDED,
            Orientation: Models.Orientation.PORTRAIT,
            Copies: 1,
            PageRange: null,
            DocumentPageCount: 1);

    /// <summary>
    /// The routing this machine should have, given what is plugged into it.
    ///
    /// <para>
    /// Returns the routing unchanged when there is nothing to do, so the caller
    /// can compare and only write when something actually moved - this runs on
    /// every sweep, and a store that rewrote the same row every two minutes
    /// would be noise in the log and churn on the disk for no change at all.
    /// </para>
    /// </summary>
    public static PrinterRouting Decide(PrinterRouting current, IReadOnlyList<LocalPrinter> printers)
    {
        return current with
        {
            Colour = Side(current.Colour, current.ColourAuto, ColorMode.COLOR, printers),
            ColourAuto = IsAuto(current.Colour, current.ColourAuto),
            BlackAndWhite = Side(
                current.BlackAndWhite, current.BlackAndWhiteAuto, ColorMode.BLACK_AND_WHITE, printers),
            BlackAndWhiteAuto = IsAuto(current.BlackAndWhite, current.BlackAndWhiteAuto),
        };
    }

    /// <summary>
    /// What this side of the routing should name.
    ///
    /// A blank entry is an invitation, not a choice: clearing the box on the
    /// routing screen is how a shop hands the decision back to the agent, so
    /// null counts as automatic whatever the flag says.
    /// </summary>
    private static string? Side(
        string? chosen, bool auto, ColorMode mode, IReadOnlyList<LocalPrinter> printers)
    {
        if (!IsAuto(chosen, auto)) return chosen;

        // PrinterRouting.None, not the current routing: the question is what the
        // machine says, and feeding the previous automatic answer back in would
        // let a printer that has since been unplugged keep recommending itself.
        var picked = PrinterSelector.SelectPrinter(Probe(mode), printers, PrinterRouting.None).Printer;

        // Null when nothing on this machine can do that kind of work - a shop
        // with only a mono laser has no answer for colour, and saying so is
        // better than naming a printer that would fail. The selector reaches
        // the same conclusion at print time and reports PRINTER_INCOMPATIBLE.
        return picked?.WindowsPrinterName;
    }

    private static bool IsAuto(string? chosen, bool auto) => auto || string.IsNullOrWhiteSpace(chosen);
}
