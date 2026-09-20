using System.Runtime.Versioning;
using PrintlyAgent.Printers;
using PrintlyAgent.Printing;
using Xunit;

namespace PrintlyAgent.Tests;

/// <summary>
/// Telling the shop what is actually wrong with the printer.
///
/// Windows reports a jam, an open cover and an empty cartridge as three
/// different bits, and every one of them was being collapsed into a single
/// ERROR. The shop got "the printer needs attention" and had to walk over and
/// work out which - which is precisely the thing it could have been told.
///
/// None of these is a failed print. Windows holds the job and prints it once
/// the condition clears, so reporting failure here is how a shop ends up
/// printing a document twice.
/// </summary>
[SupportedOSPlatform("windows")]
public class PrinterFaultTests
{
    // winspool.h, PRINTER_STATUS_*
    private const uint PaperJam = 0x00000008;
    private const uint PaperOut = 0x00000010;
    private const uint ManualFeed = 0x00000020;
    private const uint PaperProblem = 0x00000040;
    private const uint Offline = 0x00000080;
    private const uint OutputBinFull = 0x00000800;
    private const uint NotAvailable = 0x00001000;
    private const uint NoToner = 0x00040000;
    private const uint UserIntervention = 0x00100000;
    private const uint OutOfMemory = 0x00200000;
    private const uint DoorOpen = 0x00400000;
    private const uint ServerUnknown = 0x00800000;
    private const uint Error = 0x00000002;
    private const uint WorkOffline = 0x00000400;

    private static PrinterCondition? Condition(uint status, uint attributes = 0) =>
        PrinterDiscovery.ConditionOf(status, attributes);

    [Fact(DisplayName = "each fault is named as the thing somebody has to go and do")]
    public void EachFaultIsNamedSpecifically()
    {
        Assert.Equal(PrinterCondition.PAPER_JAM, Condition(PaperJam));
        Assert.Equal(PrinterCondition.DOOR_OPEN, Condition(DoorOpen));
        Assert.Equal(PrinterCondition.OUT_OF_TONER, Condition(NoToner));
        Assert.Equal(PrinterCondition.OUT_OF_PAPER, Condition(PaperOut));
        Assert.Equal(PrinterCondition.OUTPUT_BIN_FULL, Condition(OutputBinFull));
        Assert.Equal(PrinterCondition.MANUAL_FEED_REQUIRED, Condition(ManualFeed));
        Assert.Equal(PrinterCondition.PAPER_PROBLEM, Condition(PaperProblem));
        Assert.Equal(PrinterCondition.OUT_OF_MEMORY, Condition(OutOfMemory));
    }

    /// <summary>
    /// Switched off at the wall, unplugged, or the PC sharing it has gone. All
    /// three look the same from here and all three need somebody to go and look
    /// at the hardware, which is what the wording says.
    /// </summary>
    [Fact(DisplayName = "a printer that has lost power reads as unreachable")]
    public void LostPowerReadsAsUnreachable()
    {
        Assert.Equal(PrinterCondition.NOT_REACHABLE, Condition(NotAvailable));
        Assert.Equal(PrinterCondition.NOT_REACHABLE, Condition(ServerUnknown));
        Assert.Equal(PrinterCondition.NOT_REACHABLE, Condition(Offline));
    }

    /// <summary>
    /// "Work offline" is a checkbox somebody ticked, not a printer that has
    /// gone missing, and telling them to check it is switched on would be
    /// wrong. It wins over the status bits because it explains them.
    /// </summary>
    [Fact(DisplayName = "the spooler's own work-offline flag is not a hardware fault")]
    public void WorkOfflineIsNotAHardwareFault()
    {
        Assert.Equal(PrinterCondition.OFFLINE, Condition(NotAvailable, attributes: WorkOffline));
    }

    /// <summary>
    /// A carriage fault, a sensor failure, a firmware error - the driver has no
    /// specific bit for any of them and says only that something is wrong. That
    /// is still worth reporting, and NEEDS_ATTENTION keeps the wording that
    /// tells somebody where to start looking.
    /// </summary>
    [Fact(DisplayName = "a fault the driver cannot name still reaches the shop")]
    public void AnUnnamedFaultStillReachesTheShop()
    {
        Assert.Equal(PrinterCondition.NEEDS_ATTENTION, Condition(Error));
        Assert.Equal(PrinterCondition.NEEDS_ATTENTION, Condition(UserIntervention));
        Assert.Contains("check for a jam", PrinterCondition.NEEDS_ATTENTION.Description());
    }

    /// <summary>
    /// To the person standing in front of it, a printer that is jammed and also
    /// has a full output tray is jammed. The generic bits lose to every specific
    /// one, or the detail would be buried by the driver also setting ERROR.
    /// </summary>
    [Fact(DisplayName = "the most actionable fault wins when several are set at once")]
    public void TheMostActionableFaultWins()
    {
        Assert.Equal(PrinterCondition.PAPER_JAM, Condition(PaperJam | OutputBinFull | Error));
        Assert.Equal(PrinterCondition.DOOR_OPEN, Condition(DoorOpen | UserIntervention | Error));
        Assert.Equal(PrinterCondition.OUT_OF_TONER, Condition(NoToner | Error));
    }

    [Fact(DisplayName = "a healthy printer reports nothing wrong")]
    public void AHealthyPrinterReportsNothing()
    {
        Assert.Null(Condition(0));
        // Printing, warming up and idle are not faults.
        Assert.Null(Condition(0x00000400 | 0x00010000 | 0x00000200));
    }

    [Fact(DisplayName = "every condition says what to do about it")]
    public void EveryConditionHasWording()
    {
        foreach (PrinterCondition condition in Enum.GetValues<PrinterCondition>())
        {
            var description = condition.Description();
            Assert.False(string.IsNullOrWhiteSpace(description), $"{condition} has no wording");
            Assert.NotEqual(condition.ToString(), description);
        }
    }
}
