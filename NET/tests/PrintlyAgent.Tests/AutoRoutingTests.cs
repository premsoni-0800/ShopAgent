using System.Runtime.Versioning;
using PrintlyAgent.Models;
using PrintlyAgent.Printers;
using PrintlyAgent.Printing;
using Xunit;

namespace PrintlyAgent.Tests;

/// <summary>
/// Routing the shop never had to set.
///
/// The selector could always choose on its own, so nothing here is about
/// whether an order prints - it is about the counter being able to see which
/// machine takes what, without anyone configuring it, and about that answer
/// following the hardware while never overwriting a choice a person made.
/// </summary>
[SupportedOSPlatform("windows")]
public class AutoRoutingTests
{
    private static LocalPrinter Printer(string name, bool? colorCapable = null, bool isDefault = false) =>
        new(name, name, isDefault, PrinterReportedStatus.READY, colorCapable, null,
            new HashSet<PaperSize> { PaperSize.A4 });

    private static readonly LocalPrinter Colour = Printer("Canon iR C3025", colorCapable: true);
    private static readonly LocalPrinter Mono = Printer("HP LaserJet M1005", colorCapable: false);

    [Fact(DisplayName = "assigns the colour machine to colour and the mono laser to black and white")]
    public void AssignsEachModeToTheRightMachine()
    {
        var decided = AutoRouting.Decide(PrinterRouting.None, new[] { Colour, Mono });

        Assert.Equal("Canon iR C3025", decided.Colour);
        Assert.Equal("HP LaserJet M1005", decided.BlackAndWhite);
        Assert.True(decided.ColourAuto);
        Assert.True(decided.BlackAndWhiteAuto);
    }

    [Fact(DisplayName = "never overwrites a printer the shop chose itself")]
    public void NeverOverwritesAnExplicitChoice()
    {
        var chosen = new PrinterRouting(Colour: "Canon iR C3025", BlackAndWhite: "Canon iR C3025");

        var decided = AutoRouting.Decide(chosen, new[] { Colour, Mono });

        // Routing black and white to the colour machine is a perfectly good
        // reason - keeping the laser free for a long job - and not the agent's
        // to second-guess.
        Assert.Equal("Canon iR C3025", decided.BlackAndWhite);
        Assert.False(decided.BlackAndWhiteAuto);
        Assert.Equal(chosen, decided);
    }

    [Fact(DisplayName = "revises its own earlier answer when that printer is unplugged")]
    public void RevisesItsOwnAnswerWhenThePrinterGoesAway()
    {
        var earlier = AutoRouting.Decide(PrinterRouting.None, new[] { Colour, Mono });
        Assert.Equal("HP LaserJet M1005", earlier.BlackAndWhite);

        // The laser is unplugged. Only the colour machine is left.
        var after = AutoRouting.Decide(earlier, new[] { Colour });

        Assert.Equal("Canon iR C3025", after.BlackAndWhite);
        Assert.True(after.BlackAndWhiteAuto);
    }

    [Fact(DisplayName = "leaves the shop's choice pointing at a printer that has gone")]
    public void LeavesAnExplicitChoiceAloneEvenWhenItsPrinterIsGone()
    {
        // The printer is away for repair and comes back on Monday. Silently
        // repointing the setting would mean the shop's configuration quietly
        // became something else while nobody was looking - and the order still
        // prints meanwhile, because the selector passes over a routed printer
        // that is not there.
        var chosen = new PrinterRouting(BlackAndWhite: "HP LaserJet M1005");

        var decided = AutoRouting.Decide(chosen, new[] { Colour });

        Assert.Equal("HP LaserJet M1005", decided.BlackAndWhite);
        Assert.False(decided.BlackAndWhiteAuto);
    }

    [Fact(DisplayName = "clearing the box hands the decision back to the agent")]
    public void ClearingTheBoxReturnsItToAutomatic()
    {
        // How a shop undoes a choice: blank it. There is no other affordance,
        // and it must not mean "route nothing".
        var cleared = new PrinterRouting(Colour: null, BlackAndWhite: "   ");

        var decided = AutoRouting.Decide(cleared, new[] { Colour, Mono });

        Assert.Equal("Canon iR C3025", decided.Colour);
        Assert.Equal("HP LaserJet M1005", decided.BlackAndWhite);
        Assert.True(decided.BlackAndWhiteAuto);
    }

    [Fact(DisplayName = "names nothing for colour when the shop has only a mono laser")]
    public void NamesNothingForColourWhenNothingCanDoIt()
    {
        var decided = AutoRouting.Decide(PrinterRouting.None, new[] { Mono });

        // Naming the laser for colour work would be a promise the machine
        // cannot keep. The selector reports PRINTER_INCOMPATIBLE at print time
        // and the shop is told, which is the honest answer.
        Assert.Null(decided.Colour);
        Assert.Equal("HP LaserJet M1005", decided.BlackAndWhite);
    }

    [Fact(DisplayName = "settles, so the sweep does not rewrite the same answer every two minutes")]
    public void IsStableAcrossRepeatedSweeps()
    {
        var printers = new[] { Colour, Mono };

        var first = AutoRouting.Decide(PrinterRouting.None, printers);
        var second = AutoRouting.Decide(first, printers);

        // The caller writes only when this changes, so equality here is what
        // keeps the routing row off the disk and out of the log on every sweep.
        Assert.Equal(first, second);
    }

    [Fact(DisplayName = "does not route to the PDF writer when a real printer exists")]
    public void DoesNotRouteToAVirtualPrinterWhenARealOneExists()
    {
        var printers = new[] { Printer("Microsoft Print to PDF", colorCapable: true, isDefault: true), Mono };

        var decided = AutoRouting.Decide(PrinterRouting.None, printers);

        Assert.Equal("HP LaserJet M1005", decided.BlackAndWhite);
        // Nothing on this machine does colour except a file writer, so there is
        // no honest answer for colour - and the writer is not it.
        Assert.Null(decided.Colour);
    }

    [Fact(DisplayName = "the automatic choice survives a round trip through the store")]
    public void TheAutoFlagsSurviveSerialisation()
    {
        // PrinterRoutingStore.Write rebuilds the record to trim blanks, and used
        // to drop these two fields on the way past - which turned every
        // automatic answer into one the shop appeared to have made, so the
        // agent could never correct it afterwards.
        using var db = new PrintlyAgent.Db.Database(Path.Combine(Path.GetTempPath(),
            $"autorouting-{Guid.NewGuid():N}.db"));

        var decided = AutoRouting.Decide(PrinterRouting.None, new[] { Colour, Mono });
        PrinterRoutingStore.Write(db, decided);

        var reloaded = PrinterRoutingStore.Read(db);

        Assert.Equal(decided, reloaded);
        Assert.True(reloaded.ColourAuto);
        Assert.True(reloaded.BlackAndWhiteAuto);
    }
}
