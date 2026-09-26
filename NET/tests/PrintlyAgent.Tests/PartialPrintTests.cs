using PrintlyAgent.Db;
using PrintlyAgent.Jobs;
using PrintlyAgent.Models;
using Xunit;

namespace PrintlyAgent.Tests;

/// <summary>
/// An order that cannot print in full prints what it can, and says which files
/// did not come out - file by file, so the shop can print just those.
/// </summary>
public class PartialPrintTests : IDisposable
{
    private readonly string _tempDir = Path.Combine(Path.GetTempPath(), $"printly-partial-{Guid.NewGuid():N}");

    public PartialPrintTests() => Directory.CreateDirectory(_tempDir);

    public void Dispose()
    {
        try { Directory.Delete(_tempDir, recursive: true); } catch (Exception) { /* best effort */ }
    }

    private static PrintJobItem Item(string id, string name, ColorMode colour, Orientation orientation = Orientation.PORTRAIT) =>
        new(id, $"doc-{id}", name, PaperSize.A4, colour, DuplexMode.SINGLE_SIDED, orientation, 1, null, 1);

    [Fact(DisplayName = "the orientation a student chose reaches the printer")]
    public void OrientationIsCarriedIntoThePrintOptions()
    {
        var landscape = JobPipeline.OptionsFor(Item("a", "a.pdf", ColorMode.COLOR, Orientation.LANDSCAPE));
        var portrait = JobPipeline.OptionsFor(Item("b", "b.pdf", ColorMode.BLACK_AND_WHITE));

        Assert.Equal(Orientation.LANDSCAPE, landscape.Orientation);
        Assert.Equal(Orientation.PORTRAIT, portrait.Orientation);
    }

    [Fact(DisplayName = "a partial print names what printed and what did not")]
    public void SummaryNamesTheFilesThatDidNotPrint()
    {
        var colour = Item("c", "poster.pdf", ColorMode.COLOR);
        var mono = Item("m", "notes.pdf", ColorMode.BLACK_AND_WHITE);
        var detail = new PrintJobDetail("job", "order", "AA-1", PrintJobStatus.CLAIMED, new List<PrintJobItem> { colour, mono });

        var summary = JobPipeline.Summary(detail, new List<(PrintJobItem, string)> { (colour, "no colour printer") }, printed: 1);

        Assert.StartsWith("Printed 1 of 2 files.", summary);
        Assert.Contains("poster.pdf - no colour printer", summary);
        Assert.DoesNotContain("notes.pdf", summary);
    }

    [Fact(DisplayName = "a colour file with no colour printer says so in words the counter can act on")]
    public void NoPrinterReasonNamesTheColourPrinter()
    {
        Assert.Contains("colour printer", JobPipeline.NoPrinterReason(Item("c", "c.pdf", ColorMode.COLOR)));
        Assert.DoesNotContain("colour", JobPipeline.NoPrinterReason(Item("m", "m.pdf", ColorMode.BLACK_AND_WHITE)));
    }

    [Fact(DisplayName = "a reprinted file overwrites its own row and keeps its name")]
    public void ItemResultsUpsertPerFile()
    {
        using var db = new Database(Path.Combine(_tempDir, "agent.db"));

        db.UpsertItemResult("job", "c", "order", "poster.pdf", "COLOR", JobPipeline.ITEM_FAILED, null, "no colour printer");
        db.UpsertItemResult("job", "m", "order", "notes.pdf", "BLACK_AND_WHITE", JobPipeline.ITEM_PRINTED, "Mono", null);
        // The single-file reprint does not know the name again; it must not be lost.
        db.UpsertItemResult("job", "c", "order", null, null, JobPipeline.ITEM_PRINTED, "Colour", null);

        var rows = db.ItemResults("job");
        Assert.Equal(2, rows.Count);
        var poster = Assert.Single(rows, r => r.ItemId == "c");
        Assert.Equal(JobPipeline.ITEM_PRINTED, poster.Status);
        Assert.Equal("poster.pdf", poster.FileName);
        Assert.Equal("COLOR", poster.ColorMode);
        Assert.Null(poster.Reason);
    }
}
