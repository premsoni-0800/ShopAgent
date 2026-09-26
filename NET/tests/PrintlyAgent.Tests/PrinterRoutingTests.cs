using PrintlyAgent.Db;
using PrintlyAgent.Models;
using PrintlyAgent.Printers;
using PrintlyAgent.Printing;
using Xunit;

namespace PrintlyAgent.Tests;

/// <summary>
/// The shop's choice of which printer takes colour and which takes black and
/// white.
///
/// The rule being pinned here is that the choice is a <em>preference</em>, not
/// an override: it decides between printers that could all do the job, and never
/// sends work to one that cannot, or to one that is not switched on. A setting
/// that could stop an order printing would be worse than no setting at all -
/// nobody at a counter is going to connect "this order will not print" to a
/// dropdown somebody set weeks ago.
/// </summary>
public class PrinterRoutingTests
{
    private static PrintJobItem Item(ColorMode mode) => new(
        ItemId: "item-1",
        DocumentId: "doc-1",
        FileName: "thesis.pdf",
        PaperSize: PaperSize.A4,
        ColorMode: mode,
        DuplexMode: DuplexMode.SINGLE_SIDED,
        Orientation: Orientation.PORTRAIT,
        Copies: 1,
        PageRange: null,
        DocumentPageCount: 1);

    private static LocalPrinter Printer(
        string name,
        bool isDefault = false,
        bool? colour = true,
        PrinterReportedStatus status = PrinterReportedStatus.READY) =>
        new(name, name, isDefault, status, colour, DuplexCapable: true);

    [Fact]
    public void ColourWorkGoesToTheColourPrinterEvenWhenAnotherIsTheSystemDefault()
    {
        // The system default is worth more than every capability bonus combined,
        // which is why it wins every time without a routing rule - and why this
        // is the case worth proving.
        var printers = new[] { Printer("Front Desk", isDefault: true), Printer("Colour Laser") };
        var routing = new PrinterRouting(Colour: "Colour Laser", BlackAndWhite: "Front Desk");

        var chosen = PrinterSelector.SelectPrinter(Item(ColorMode.COLOR), printers, routing);

        Assert.Equal("Colour Laser", chosen.Printer?.WindowsPrinterName);
    }

    [Fact]
    public void BlackAndWhiteWorkGoesToTheMonoPrinter()
    {
        var printers = new[] { Printer("Colour Laser", isDefault: true), Printer("Mono Laser", colour: false) };
        var routing = new PrinterRouting(Colour: "Colour Laser", BlackAndWhite: "Mono Laser");

        var chosen = PrinterSelector.SelectPrinter(Item(ColorMode.BLACK_AND_WHITE), printers, routing);

        Assert.Equal("Mono Laser", chosen.Printer?.WindowsPrinterName);
    }

    [Fact]
    public void AnOfflineNamedPrinterIsPassedOverRatherThanObeyed()
    {
        // The named machine has been switched off. The order still has to come
        // out: falling back is the difference between "it printed on the other
        // one" and a counter queue that has silently stopped.
        var printers = new[]
        {
            Printer("Colour Laser", status: PrinterReportedStatus.OFFLINE),
            Printer("Front Desk", isDefault: true),
        };
        var routing = new PrinterRouting(Colour: "Colour Laser");

        var chosen = PrinterSelector.SelectPrinter(Item(ColorMode.COLOR), printers, routing);

        Assert.Equal("Front Desk", chosen.Printer?.WindowsPrinterName);
    }

    [Fact]
    public void ANamedPrinterThatCannotDoTheJobIsNotUsedForIt()
    {
        // Somebody pointed colour at the mono laser. Obeying that would print a
        // colour order in grey and report it complete, which is the failure
        // nobody would spot until the student complained.
        var printers = new[] { Printer("Mono Laser", colour: false), Printer("Colour Laser") };
        var routing = new PrinterRouting(Colour: "Mono Laser");

        var chosen = PrinterSelector.SelectPrinter(Item(ColorMode.COLOR), printers, routing);

        Assert.Equal("Colour Laser", chosen.Printer?.WindowsPrinterName);
    }

    [Fact]
    public void ANamedPrinterThatIsNoLongerOnThisMachineFallsBack()
    {
        // Renamed, or unplugged for good. Same rule as offline.
        var printers = new[] { Printer("Front Desk", isDefault: true) };
        var routing = new PrinterRouting(Colour: "A Printer That Left");

        var chosen = PrinterSelector.SelectPrinter(Item(ColorMode.COLOR), printers, routing);

        Assert.Equal("Front Desk", chosen.Printer?.WindowsPrinterName);
    }

    [Fact]
    public void AColourFileNeverFallsBackOntoThePrinterNamedForBlackAndWhite()
    {
        // The mixed-order case: colour printer unplugged, mono laser whose
        // driver will not say it is mono. The colour file must wait for its
        // printer, not come out grey and be reported printed.
        var printers = new[]
        {
            Printer("Colour Laser", status: PrinterReportedStatus.OFFLINE),
            Printer("Mono Laser", isDefault: true, colour: null),
        };
        var routing = new PrinterRouting(Colour: "Colour Laser", BlackAndWhite: "Mono Laser");

        var colour = PrinterSelector.SelectPrinter(Item(ColorMode.COLOR), printers, routing);
        var mono = PrinterSelector.SelectPrinter(Item(ColorMode.BLACK_AND_WHITE), printers, routing);

        Assert.Null(colour.Printer);
        Assert.Equal("Mono Laser", mono.Printer?.WindowsPrinterName);
    }

    [Fact]
    public void ABlackAndWhiteFileNeverFallsBackOntoThePrinterNamedForColour()
    {
        var printers = new[]
        {
            Printer("Colour Laser", isDefault: true),
            Printer("Mono Laser", colour: false, status: PrinterReportedStatus.OFFLINE),
        };
        var routing = new PrinterRouting(Colour: "Colour Laser", BlackAndWhite: "Mono Laser");

        var chosen = PrinterSelector.SelectPrinter(Item(ColorMode.BLACK_AND_WHITE), printers, routing);

        Assert.Null(chosen.Printer);
    }

    [Fact]
    public void OnePrinterNamedForBothModesStillTakesBoth()
    {
        var printers = new[] { Printer("Only One", isDefault: true) };
        var routing = new PrinterRouting(Colour: "Only One", BlackAndWhite: "Only One");

        Assert.Equal("Only One", PrinterSelector.SelectPrinter(Item(ColorMode.COLOR), printers, routing).Printer?.WindowsPrinterName);
        Assert.Equal("Only One", PrinterSelector.SelectPrinter(Item(ColorMode.BLACK_AND_WHITE), printers, routing).Printer?.WindowsPrinterName);
    }

    [Fact]
    public void WithNoRoutingAtAllNothingAboutSelectionChanges()
    {
        var printers = new[] { Printer("Front Desk", isDefault: true), Printer("Colour Laser") };

        var withoutRouting = PrinterSelector.SelectPrinter(Item(ColorMode.COLOR), printers);
        var withEmptyRouting = PrinterSelector.SelectPrinter(Item(ColorMode.COLOR), printers, PrinterRouting.None);

        Assert.Equal("Front Desk", withoutRouting.Printer?.WindowsPrinterName);
        Assert.Equal(withoutRouting.Printer?.WindowsPrinterName, withEmptyRouting.Printer?.WindowsPrinterName);
    }

    [Fact]
    public void ABlankNameMeansDecideItFromTheCapabilities()
    {
        // The dropdown's "Choose automatically" option. Stored blank, and it
        // must not be looked up as a printer called "".
        var printers = new[] { Printer("Front Desk", isDefault: true), Printer("Colour Laser") };
        var routing = new PrinterRouting(Colour: "   ", BlackAndWhite: null);

        var chosen = PrinterSelector.SelectPrinter(Item(ColorMode.COLOR), printers, routing);

        Assert.Equal("Front Desk", chosen.Printer?.WindowsPrinterName);
    }

    // --- persistence --------------------------------------------------------

    [Fact]
    public void TheChoiceSurvivesBeingWrittenAndReadBack()
    {
        WithDatabase(db =>
        {
            PrinterRoutingStore.Write(db, new PrinterRouting("Colour Laser", "Mono Laser"));

            var read = PrinterRoutingStore.Read(db);

            Assert.Equal("Colour Laser", read.Colour);
            Assert.Equal("Mono Laser", read.BlackAndWhite);
        });
    }

    [Fact]
    public void NothingSavedYetReadsAsDecideItAutomatically()
    {
        WithDatabase(db =>
        {
            Assert.Null(PrinterRoutingStore.Read(db).Colour);
            Assert.Null(PrinterRoutingStore.Read(db).BlackAndWhite);
        });
    }

    [Fact]
    public void AnUnreadableSettingFallsBackInsteadOfStoppingThePrinter()
    {
        WithDatabase(db =>
        {
            db.SetState("printer_routing", "{ this is not json");

            // Not an exception. A corrupt setting must cost the preference and
            // nothing else - the order still prints, on whatever fits best.
            Assert.Null(PrinterRoutingStore.Read(db).Colour);
        });
    }

    [Fact]
    public void AWhitespaceChoiceIsStoredAsNoChoice()
    {
        WithDatabase(db =>
        {
            PrinterRoutingStore.Write(db, new PrinterRouting("  ", "Mono Laser"));

            var read = PrinterRoutingStore.Read(db);

            Assert.Null(read.Colour);
            Assert.Equal("Mono Laser", read.BlackAndWhite);
        });
    }

    /// <summary>
    /// Runs <paramref name="body"/> against a throwaway database, and deletes the
    /// file afterwards.
    ///
    /// The connection is closed before the delete, deliberately: SQLite holds
    /// the file open until it is disposed, and a `using var` in the test body
    /// would not have released it by the time a `finally` tried to delete it.
    /// </summary>
    private static void WithDatabase(Action<Database> body)
    {
        var path = Path.Combine(Path.GetTempPath(), $"printly-routing-{Guid.NewGuid():N}.db");
        try
        {
            using (var db = new Database(path)) body(db);
        }
        finally
        {
            try { File.Delete(path); } catch (IOException) { /* a stray WAL file; harmless in temp */ }
        }
    }
}
