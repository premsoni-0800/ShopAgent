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
/// Deterministic, explainable printer selection - port of
/// printers/PrinterSelector.kt: exclude anything proven incompatible or
/// unusable, score what is left, and never print with the wrong settings just
/// because a printer was available.
/// </summary>
public static class PrinterSelector
{
    public static SelectionResult SelectPrinter(PrintJobItem item, IReadOnlyList<LocalPrinter> printers)
    {
        // Whether this machine has any real printer at all, judged across every
        // printer rather than only the usable ones - a shop whose only laser is
        // offline still has a laser, and the right answer then is "this cannot
        // be printed now", not "written to a file instead".
        var hasPhysicalPrinter = printers.Any(p => !PrintToFile.IsPrintToFileDriver(p.WindowsPrinterName));

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
        if (printer.IsSystemDefault) score += 10;
        if (item.ColorMode == ColorMode.COLOR && printer.ColorCapable == true) score += 3;
        if (item.ColorMode == ColorMode.BLACK_AND_WHITE && printer.ColorCapable != null) score += 1;
        if (item.DuplexMode == DuplexMode.DOUBLE_SIDED && printer.DuplexCapable == true) score += 3;
        if (printer.Sizes.Count > 0 && printer.Sizes.Contains(item.PaperSize)) score += 3;
        // An UNKNOWN capability is not disqualifying (see ProvenIncompatible),
        // but it is not rewarded either - a printer with proven-matching
        // capabilities is always preferred over one that merely might work.
        return score;
    }
}
