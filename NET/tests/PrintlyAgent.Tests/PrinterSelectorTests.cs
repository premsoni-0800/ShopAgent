using PrintlyAgent.Models;
using PrintlyAgent.Printers;
using Xunit;

namespace PrintlyAgent.Tests;

/// <summary>
/// Port of PrinterSelectorTest.kt, case for case and assertion for assertion.
///
/// The Kotlin names are full sentences in backticks; each is carried over as a
/// DisplayName so the suite still reads as a description of the behaviour
/// rather than a list of method names.
/// </summary>
public class PrinterSelectorTests
{
    private static PrintJobItem Item(
        ColorMode colorMode = ColorMode.BLACK_AND_WHITE,
        DuplexMode duplexMode = DuplexMode.SINGLE_SIDED,
        PaperSize paperSize = PaperSize.A4) =>
        new(
            ItemId: "item-1",
            DocumentId: "doc-1",
            FileName: "f.pdf",
            PaperSize: paperSize,
            ColorMode: colorMode,
            DuplexMode: duplexMode,
            Orientation: Orientation.PORTRAIT,
            Copies: 1,
            PageRange: null,
            DocumentPageCount: 3);

    private static LocalPrinter Printer(
        string name,
        bool isDefault = false,
        PrinterReportedStatus status = PrinterReportedStatus.READY,
        bool? colorCapable = null,
        bool? duplexCapable = null,
        params PaperSize[] paperSizes) =>
        new(name, name, isDefault, status, colorCapable, duplexCapable, new HashSet<PaperSize>(paperSizes));

    [Fact(DisplayName = "excludes offline and error printers")]
    public void ExcludesOfflineAndErrorPrinters()
    {
        var printers = new[]
        {
            Printer("Offline", status: PrinterReportedStatus.OFFLINE),
            Printer("Errored", status: PrinterReportedStatus.ERROR),
        };
        var result = PrinterSelector.SelectPrinter(Item(), printers);
        Assert.Null(result.Printer);
        Assert.Equal("PRINTER_INCOMPATIBLE", result.Reason);
    }

    [Fact(DisplayName = "excludes a printer proven not to support color")]
    public void ExcludesAPrinterProvenNotToSupportColor()
    {
        var printers = new[] { Printer("BW-only", colorCapable: false) };
        var result = PrinterSelector.SelectPrinter(Item(colorMode: ColorMode.COLOR), printers);
        Assert.Null(result.Printer);
    }

    [Fact(DisplayName = "does not exclude a printer with unknown color capability")]
    public void DoesNotExcludeAPrinterWithUnknownColorCapability()
    {
        // An UNKNOWN capability is never treated as proven-incompatible - it
        // just scores lower than a proven match.
        var printers = new[] { Printer("Unknown-caps", colorCapable: null) };
        var result = PrinterSelector.SelectPrinter(Item(colorMode: ColorMode.COLOR), printers);
        Assert.Equal("Unknown-caps", result.Printer?.WindowsPrinterName);
    }

    [Fact(DisplayName = "prefers the system default when otherwise equal")]
    public void PrefersTheSystemDefaultWhenOtherwiseEqual()
    {
        var printers = new[] { Printer("Other"), Printer("Default", isDefault: true) };
        var result = PrinterSelector.SelectPrinter(Item(), printers);
        Assert.Equal("Default", result.Printer?.WindowsPrinterName);
    }

    [Fact(DisplayName = "among non-default printers prefers proven capability matches")]
    public void AmongNonDefaultPrintersPrefersProvenCapabilityMatches()
    {
        var printers = new[]
        {
            Printer("UnknownCaps"),
            Printer("ColorCapable", colorCapable: true, duplexCapable: true, paperSizes: PaperSize.A4),
        };
        var result = PrinterSelector.SelectPrinter(
            Item(colorMode: ColorMode.COLOR, duplexMode: DuplexMode.DOUBLE_SIDED), printers);
        Assert.Equal("ColorCapable", result.Printer?.WindowsPrinterName);
    }

    [Fact(DisplayName = "excludes a printer whose paper sizes are known and do not include the requested one")]
    public void ExcludesAPrinterWhosePaperSizesDoNotIncludeTheRequestedOne()
    {
        var printers = new[] { Printer("LetterOnly", paperSizes: PaperSize.LETTER) };
        var result = PrinterSelector.SelectPrinter(Item(paperSize: PaperSize.A4), printers);
        Assert.Null(result.Printer);
    }

    /// <summary>
    /// The exact shape of a real shop PC: a mono laser plus Windows' built-in
    /// PDF writer, with the PDF writer left as the system default (which it very
    /// often is). On score alone the virtual printer wins outright - it is the
    /// default (+10) and advertises every common paper size (+3) - and the order
    /// would be written to a file, reported as printed, and the student told to
    /// collect paper that does not exist.
    /// </summary>
    [Fact(DisplayName = "a real printer beats the system-default PDF writer")]
    public void ARealPrinterBeatsTheSystemDefaultPdfWriter()
    {
        var printers = new[]
        {
            Printer("Microsoft Print to PDF", isDefault: true, colorCapable: true, duplexCapable: true,
                paperSizes: new[] { PaperSize.A4, PaperSize.A3, PaperSize.LETTER, PaperSize.LEGAL }),
            Printer("Hewlett-Packard HP LaserJet M1005", isDefault: false, colorCapable: false,
                duplexCapable: true, paperSizes: PaperSize.A4),
        };

        var result = PrinterSelector.SelectPrinter(Item(), printers);

        Assert.Equal("Hewlett-Packard HP LaserJet M1005", result.Printer?.WindowsPrinterName);
    }

    /// <summary>Nothing else to print with - a dev box, or this project's own test machine.</summary>
    [Fact(DisplayName = "a virtual printer is still chosen when it is the only one")]
    public void AVirtualPrinterIsStillChosenWhenItIsTheOnlyOne()
    {
        var printers = new[]
        {
            Printer("Microsoft Print to PDF", isDefault: true, colorCapable: true, paperSizes: PaperSize.A4),
        };

        var result = PrinterSelector.SelectPrinter(Item(), printers);

        Assert.Equal("Microsoft Print to PDF", result.Printer?.WindowsPrinterName);
    }

    /// <summary>
    /// A shop with only a mono laser genuinely cannot fulfil a colour order, and
    /// has to be told so. Falling back to the PDF writer here would write the
    /// order to disk, report it printed, and send the student to collect paper
    /// that never existed - the failure this whole file exists to stop.
    /// </summary>
    [Fact(DisplayName = "a colour job a real printer cannot do fails rather than becoming a file")]
    public void AColourJobARealPrinterCannotDoFailsRatherThanBecomingAFile()
    {
        var printers = new[]
        {
            Printer("Microsoft Print to PDF", colorCapable: true, paperSizes: PaperSize.A4),
            Printer("Hewlett-Packard HP LaserJet M1005", colorCapable: false, paperSizes: PaperSize.A4),
        };

        var result = PrinterSelector.SelectPrinter(Item(colorMode: ColorMode.COLOR), printers);

        Assert.Null(result.Printer);
        Assert.Equal("PRINTER_INCOMPATIBLE", result.Reason);
    }

    /// <summary>
    /// Even an offline laser means this machine is a real printer's machine -
    /// its jobs wait, they do not become files.
    /// </summary>
    [Fact(DisplayName = "an offline real printer does not hand the job to the PDF writer")]
    public void AnOfflineRealPrinterDoesNotHandTheJobToThePdfWriter()
    {
        var printers = new[]
        {
            Printer("Microsoft Print to PDF", isDefault: true, colorCapable: true, paperSizes: PaperSize.A4),
            Printer("Hewlett-Packard HP LaserJet M1005", status: PrinterReportedStatus.OFFLINE,
                paperSizes: PaperSize.A4),
        };

        var result = PrinterSelector.SelectPrinter(Item(), printers);

        Assert.Null(result.Printer);
        Assert.Equal("PRINTER_INCOMPATIBLE", result.Reason);
    }
}
