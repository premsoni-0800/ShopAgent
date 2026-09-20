using PrintlyAgent.Models;
using PrintlyAgent.Printing;

namespace PrintlyAgent.Printers;

/// <summary>
/// A printer as this machine reports it.
///
/// <c>ColorCapable</c> and <c>DuplexCapable</c> are nullable on purpose and the
/// null means "the driver would not say reliably" - a third answer that is
/// neither yes nor no, and never guessed. Collapsing it to false would make the
/// selector refuse printers that work; collapsing it to true would send colour
/// jobs to mono lasers. Both matter, so the distinction is carried.
/// </summary>
public sealed record LocalPrinter(
    string WindowsPrinterName,
    string DisplayName,
    bool IsSystemDefault,
    PrinterReportedStatus Status,
    bool? ColorCapable,
    bool? DuplexCapable,
    IReadOnlySet<PaperSize>? PaperSizes = null)
{
    public IReadOnlySet<PaperSize> Sizes => PaperSizes ?? new HashSet<PaperSize>();
}

public sealed record SelectionResult(LocalPrinter? Printer, string? Reason = null);

/// <summary>
/// Which printer the shop has said should take colour work, and which should
/// take black and white.
///
/// <para>
/// Two Windows printer names, or null for "decide it from the capabilities", and
/// nothing more. It is deliberately not a rule engine: colour mode is the split
/// a shop with two machines actually works to - the colour one and the cheap
/// mono laser - and it is also the only print option the customer app lets
/// anyone choose, so a rule keyed on anything else would sit unused.
/// </para>
///
/// <para>
/// A name here is a preference, never an override of physics: see
/// <see cref="PrinterSelector.SelectPrinter"/>. A printer that has been unplugged,
/// renamed, or cannot do the job is passed over rather than obeyed.
/// </para>
/// </summary>
public sealed record PrinterRouting(
    string? Colour = null,
    string? BlackAndWhite = null,

    /// <summary>
    /// Whether this side was decided by the agent rather than chosen by the
    /// shop.
    ///
    /// <para>
    /// The distinction is the whole reason automatic routing can exist at all.
    /// An entry the agent filled in is its own to revise when the machine
    /// changes; an entry a person chose is not, and must survive a printer
    /// being unplugged, a sweep, and a restart. Without the flag the two are
    /// indistinguishable the moment they are written, and the agent would
    /// either overwrite the shop's choice or never be able to correct its own.
    /// </para>
    ///
    /// <para>
    /// Defaults to false, which is what a routing stored before this existed
    /// deserialises to - so every setting a shop has already made is read as
    /// theirs and left alone. The safe direction.
    /// </para>
    /// </summary>
    bool ColourAuto = false,
    bool BlackAndWhiteAuto = false)
{
    public static readonly PrinterRouting None = new();

    /// <summary>The printer this shop has named for an item, if it named one.</summary>
    public string? For(PrintJobItem item) =>
        item.ColorMode == ColorMode.COLOR ? Blank(Colour) : Blank(BlackAndWhite);

    private static string? Blank(string? value) => string.IsNullOrWhiteSpace(value) ? null : value;
}

/// <summary>
/// Deterministic, explainable printer selection - port of
/// printers/PrinterSelector.kt: exclude anything proven incompatible or
/// unusable, score what is left, and never print with the wrong settings just
/// because a printer was available.
/// </summary>
public static class PrinterSelector
{
    public static SelectionResult SelectPrinter(
        PrintJobItem item,
        IReadOnlyList<LocalPrinter> printers,
        PrinterRouting? routing = null)
    {
        // Whether this machine has any real printer at all. Shared with
        // PrinterDiscovery.Reportable, which decides on the same question
        // whether these printers are worth showing the shop - the two must
        // agree, or the agent would print with a printer it had hidden.
        var hasPhysicalPrinter = PrinterDiscovery.AnyPhysical(printers);

        var candidates = printers
            .Where(p => p.Status != PrinterReportedStatus.OFFLINE && p.Status != PrinterReportedStatus.ERROR)
            .Where(p => !ProvenIncompatible(item, p))
            // Never quietly swap a real printer for one that writes a file.
            //
            // Scoring alone was not enough. A mono laser is *proven
            // incompatible* with a colour job and drops out above, leaving
            // "Microsoft Print to PDF" as the only candidate - so the shop's
            // colour order would be written to disk, reported PRINT_COMPLETED,
            // and the student told to come and collect paper that was never
            // printed. Failing with PRINTER_INCOMPATIBLE is the honest answer: a
            // shop with only a mono laser genuinely cannot fulfil a colour
            // order, and needs to be told.
            //
            // Still selectable when there is nothing else on the machine, which
            // is every dev box and this project's own test setup.
            .Where(p => !(hasPhysicalPrinter && PrintToFile.IsPrintToFileDriver(p.WindowsPrinterName)))
            .ToList();

        if (candidates.Count == 0) return new SelectionResult(null, "PRINTER_INCOMPATIBLE");

        // What the shop said, if it said anything and the machine is actually
        // there and able to do the job.
        //
        // Chosen from the candidates rather than ahead of them on purpose: the
        // filters above are about what is *possible*, and a preference cannot
        // make an offline printer print or a mono laser produce colour. A named
        // printer that has dropped off the machine - unplugged, renamed, out of
        // paper - falls through to the scoring below and the order still comes
        // out, which is a far better answer at a counter than refusing because a
        // setting names something that is not there.
        var named = routing?.For(item);
        if (named is not null)
        {
            var preferred = candidates.FirstOrDefault(p =>
                string.Equals(p.WindowsPrinterName, named, StringComparison.OrdinalIgnoreCase));
            if (preferred is not null) return new SelectionResult(preferred);
        }

        var best = candidates
            .OrderByDescending(p => Score(item, p))
            .ThenBy(p => p.WindowsPrinterName, StringComparer.Ordinal)
            .First();

        return new SelectionResult(best);
    }

    private static bool ProvenIncompatible(PrintJobItem item, LocalPrinter printer)
    {
        if (item.ColorMode == ColorMode.COLOR && printer.ColorCapable == false) return true;
        if (item.DuplexMode == DuplexMode.DOUBLE_SIDED && printer.DuplexCapable == false) return true;
        if (printer.Sizes.Count > 0 && !printer.Sizes.Contains(item.PaperSize)) return true;
        return false;
    }

    private static int Score(PrintJobItem item, LocalPrinter printer)
    {
        var score = 0;

        // Colour mode outranks every other signal, the system default included.
        //
        // A shop with two machines has two for this reason: the colour one and
        // the cheap mono laser. Sending black-and-white work to the colour
        // printer is not a wrong print, it is an expensive one - colour toner
        // spent on a job the mono laser was bought to take - and it happened on
        // every single order, because the colour printer is usually the Windows
        // default and IsSystemDefault outscored the capability match. The other
        // direction was never in doubt: a colour job on a mono laser is proven
        // incompatible and excluded outright, above.
        //
        // A printer that would not say is not matched either way. Null means the
        // driver did not answer, and guessing mono to save toner would send
        // colour-capable work to a machine nobody has confirmed can do it.
        if (item.ColorMode == ColorMode.COLOR && printer.ColorCapable == true) score += 20;
        if (item.ColorMode == ColorMode.BLACK_AND_WHITE && printer.ColorCapable == false) score += 20;

        if (printer.IsSystemDefault) score += 10;

        // Known-and-wrong still beats unknown for black and white: a colour
        // printer that says it is one will certainly print the job, where a
        // printer that would not answer may not really be there at all.
        if (item.ColorMode == ColorMode.BLACK_AND_WHITE && printer.ColorCapable != null) score += 1;
        if (item.DuplexMode == DuplexMode.DOUBLE_SIDED && printer.DuplexCapable == true) score += 3;
        if (printer.Sizes.Count > 0 && printer.Sizes.Contains(item.PaperSize)) score += 3;
        // An UNKNOWN capability is not disqualifying (see ProvenIncompatible),
        // but it is not rewarded either - a printer with proven-matching
        // capabilities is always preferred over one that merely might work.
        return score;
    }
}
