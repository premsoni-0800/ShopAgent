using PrintlyAgent.Db;
using Xunit;

namespace PrintlyAgent.Tests;

/// <summary>
/// The local record of what is plugged into this machine.
///
/// Nothing added rows but the sweep and nothing ever removed them, so the table
/// was a history of every printer the agent had ever seen rather than a
/// statement of what it has - each one frozen at whatever status it last
/// reported. A row saying READY for a laser that went back to the supplier in
/// March is worse than no row at all.
/// </summary>
public class PrinterTableTests : IDisposable
{
    private readonly string _path = Path.Combine(Path.GetTempPath(), $"printers-{Guid.NewGuid():N}.db");
    private readonly Database _db;

    public PrinterTableTests() => _db = new Database(_path);

    public void Dispose()
    {
        _db.Dispose();
        try { File.Delete(_path); } catch (IOException) { /* a temp file the OS will take care of */ }
        GC.SuppressFinalize(this);
    }

    private void Upsert(string name) =>
        _db.UpsertPrinter(name, name, "True", "True", "A4", "READY", false);

    [Fact(DisplayName = "a printer that has gone stops being recorded")]
    public void APrinterThatHasGoneStopsBeingRecorded()
    {
        Upsert("HP LaserJet M1005");
        Upsert("Canon iR C3025");

        // The next sweep finds only the Canon - the laser was unplugged.
        Upsert("Canon iR C3025");
        _db.PruneMissingPrinters(new[] { "Canon iR C3025" });

        Assert.Equal(new[] { "Canon iR C3025" }, _db.PrinterNames());
    }

    [Fact(DisplayName = "the printers still present are left alone")]
    public void ThePrintersStillPresentAreLeftAlone()
    {
        Upsert("HP LaserJet M1005");
        Upsert("Canon iR C3025");

        _db.PruneMissingPrinters(new[] { "Canon iR C3025", "HP LaserJet M1005" });

        Assert.Equal(new[] { "Canon iR C3025", "HP LaserJet M1005" }, _db.PrinterNames());
    }

    [Fact(DisplayName = "no printers at all is a real answer, not a reason to keep the old ones")]
    public void AnEmptySweepClearsTheTable()
    {
        Upsert("HP LaserJet M1005");

        _db.PruneMissingPrinters(Array.Empty<string>());

        Assert.Empty(_db.PrinterNames());
    }

    [Fact(DisplayName = "a printer named with an apostrophe survives the prune")]
    public void APrinterNameWithAnApostropheIsHandled()
    {
        // The name is whatever the driver installed, and it is the key this
        // table is deleted by. Interpolating it into the SQL would have thrown
        // here, or worse, deleted the wrong rows.
        Upsert("Reception's Brother HL-L2320D");
        Upsert("Canon iR C3025");

        _db.PruneMissingPrinters(new[] { "Reception's Brother HL-L2320D" });

        Assert.Equal(new[] { "Reception's Brother HL-L2320D" }, _db.PrinterNames());
    }
}
