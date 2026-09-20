using System.Runtime.Versioning;
using PrintlyAgent.Models;
using PrintlyAgent.Printers;
using PrintlyAgent.Printing;
using Xunit;

namespace PrintlyAgent.Tests;

/// <summary>
/// Which printers the shop is actually shown.
///
/// Every Windows machine carries "Microsoft Print to PDF", "OneNote (Desktop)",
/// the XPS writer and a fax driver whether anyone wants them or not. A counter
/// with a laser has no use for any of them, and reporting them turned the
/// routing screen into a question about which PDF writer should take colour
/// work.
///
/// The rule is shared with <see cref="PrinterSelector"/> rather than duplicated,
/// so the agent can never hide a printer it is still willing to print with -
/// which is what the last case here holds to.
/// </summary>
[SupportedOSPlatform("windows")]
public class PrinterReportableTests
{
    private static LocalPrinter Printer(string name, bool? colorCapable = null) =>
        new(name, name, false, PrinterReportedStatus.READY, colorCapable, null, new HashSet<PaperSize>());

    [Fact(DisplayName = "hides the PDF and OneNote writers when the shop has a real printer")]
    public void HidesVirtualPrintersWhenARealOneExists()
    {
        var printers = new[]
        {
            Printer("Microsoft Print to PDF"),
            Printer("OneNote (Desktop)"),
            Printer("HP LaserJet M1005", colorCapable: false),
        };

        var reported = PrinterDiscovery.Reportable(printers);

        Assert.Equal(new[] { "HP LaserJet M1005" }, reported.Select(p => p.WindowsPrinterName));
    }

    [Fact(DisplayName = "shows them when they are all this machine has")]
    public void ShowsVirtualPrintersWhenTheyAreAllThereIs()
    {
        // A dev box, and this project's own test machine. An empty list here
        // would say the agent had found nothing, while the selector went on
        // printing to file quite happily - the two must not disagree.
        var printers = new[] { Printer("Microsoft Print to PDF"), Printer("OneNote (Desktop)") };

        var reported = PrinterDiscovery.Reportable(printers);

        Assert.Equal(2, reported.Count);
    }

    [Fact(DisplayName = "an offline laser still counts as a real printer, so the writers stay hidden")]
    public void AnOfflineRealPrinterStillCounts()
    {
        // AnyPhysical asks across every printer, not only the usable ones: a
        // shop whose only laser is switched off still has a laser, and the
        // answer then is that the order cannot print now - not that the shop
        // should be offered a PDF writer instead.
        var printers = new[]
        {
            Printer("Microsoft Print to PDF"),
            new LocalPrinter("HP LaserJet M1005", "HP LaserJet M1005", false,
                PrinterReportedStatus.OFFLINE, false, null, new HashSet<PaperSize>()),
        };

        var reported = PrinterDiscovery.Reportable(printers);

        Assert.Equal(new[] { "HP LaserJet M1005" }, reported.Select(p => p.WindowsPrinterName));
    }

    [Fact(DisplayName = "never hides a printer the selector would still print with")]
    public void NeverHidesAPrinterTheSelectorWouldStillUse()
    {
        // The property that matters, stated directly: whatever the machine
        // holds, anything the selector is prepared to choose must be something
        // the shop can see. Both halves ask PrinterDiscovery.AnyPhysical, and
        // this fails the moment one of them stops.
        var machines = new[]
        {
            new[] { Printer("Microsoft Print to PDF") },
            new[] { Printer("Microsoft Print to PDF"), Printer("OneNote (Desktop)") },
            new[] { Printer("Microsoft Print to PDF"), Printer("HP LaserJet M1005", colorCapable: false) },
            new[] { Printer("Canon iR C3025", colorCapable: true), Printer("HP LaserJet M1005", colorCapable: false) },
        };

        foreach (var printers in machines)
        {
            var chosen = PrinterSelector.SelectPrinter(
                new PrintJobItem("i", "d", "f.pdf", PaperSize.A4, ColorMode.BLACK_AND_WHITE,
                    DuplexMode.SINGLE_SIDED, Orientation.PORTRAIT, 1, null, 1),
                printers).Printer;

            if (chosen is null) continue;

            var reported = PrinterDiscovery.Reportable(printers);
            Assert.Contains(chosen.WindowsPrinterName, reported.Select(p => p.WindowsPrinterName));
        }
    }

    [Fact(DisplayName = "the drivers Windows ships with are the ones recognised as writing files")]
    public void TheUsualWindowsVirtualDriversAreRecognised()
    {
        // Named here because the filter is only as good as this list, and a
        // machine at a shop is the wrong place to discover a gap in it.
        Assert.True(PrintToFile.IsPrintToFileDriver("Microsoft Print to PDF"));
        Assert.True(PrintToFile.IsPrintToFileDriver("OneNote (Desktop)"));
        Assert.True(PrintToFile.IsPrintToFileDriver("Microsoft XPS Document Writer"));

        Assert.False(PrintToFile.IsPrintToFileDriver("HP LaserJet M1005"));
        Assert.False(PrintToFile.IsPrintToFileDriver("Canon iR C3025"));

        // The queue Windows creates is called "Fax", not "Microsoft Shared Fax
        // Driver" - the markers are matched against the printer name, and that
        // one is a driver name. It matched nothing until "fax" was added.
        Assert.True(PrintToFile.IsPrintToFileDriver("Microsoft Shared Fax Driver"));
        Assert.True(PrintToFile.IsPrintToFileDriver("Fax"));
        Assert.True(PrintToFile.IsPrintToFileDriver("HP LaserJet MFP M428 fax"));

        // The paper-printing queue of the same multifunction device does not
        // carry the word, and must keep printing.
        Assert.False(PrintToFile.IsPrintToFileDriver("HP LaserJet MFP M428"));
    }

    /// <summary>
    /// The failure the fax marker exists to stop.
    ///
    /// A fax queue is not a printer, but nothing said so, and it scores exactly
    /// what a mono laser scores on a black-and-white A4 job - same colour
    /// match, same known capabilities, same paper size. The tie then fell to
    /// the name, "Fax" sorts before any real printer beginning with a letter
    /// after F, and the shop's order went to the fax queue instead of onto
    /// paper. It also counted as a real printer, which hid the PDF writers
    /// behind it on a machine that had nothing able to print at all.
    /// </summary>
    [Fact(DisplayName = "the Windows fax queue is never chosen over a real printer")]
    public void TheFaxQueueIsNeverChosenOverARealPrinter()
    {
        var printers = new[]
        {
            Printer("Fax", colorCapable: false),
            Printer("HP LaserJet M1005", colorCapable: false),
        };

        var chosen = PrinterSelector.SelectPrinter(
            new PrintJobItem("i", "d", "f.pdf", PaperSize.A4, ColorMode.BLACK_AND_WHITE,
                DuplexMode.SINGLE_SIDED, Orientation.PORTRAIT, 1, null, 1),
            printers).Printer;

        Assert.Equal("HP LaserJet M1005", chosen?.WindowsPrinterName);

        // And it is not something the shop is offered either.
        Assert.DoesNotContain("Fax", PrinterDiscovery.Reportable(printers).Select(p => p.WindowsPrinterName));
    }
}
