using System.Text;
using PrintlyAgent.Printing;
using Xunit;

namespace PrintlyAgent.Tests;

/// <summary>
/// Port of DocumentSweepTest.kt, case for case.
///
/// <para>
/// Customer documents must not outlive the job that printed them. The pipeline
/// deletes each one in a <c>finally</c>, which covers every way a job can end but
/// none of the ways the process can - so this is the half that survives a crash
/// or a power cut on the counter PC.
/// </para>
///
/// <para>
/// The risk in a sweep is the opposite mistake: deleting the file another agent
/// is printing from right now. Both directions are covered here.
/// </para>
///
/// <para>
/// JUnit's @TempDir becomes a directory made per test and removed afterwards -
/// xUnit constructs the class once per test, so each case gets its own, which is
/// the point of @TempDir. Nothing here ever touches the agent's real temp
/// directory: a sweep test pointed at that would delete a live job's document.
/// </para>
/// </summary>
public class DocumentsTests : IDisposable
{
    /// <summary>No process has this id: pid 0 is not a real process handle on any supported OS.</summary>
    private const long DeadPid = 0L;

    private readonly string _tempDir;

    public DocumentsTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "printly-document-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_tempDir, recursive: true); }
        catch (IOException) { /* a file still held; the temp dir is disposable either way */ }
    }

    // --- sweeping ------------------------------------------------------------

    [Fact(DisplayName = "a document left by a dead agent is removed")]
    public void ADocumentLeftByADeadAgentIsRemoved()
    {
        var orphan = Path.Combine(_tempDir, $"{DeadPid}-abc123.pdf");
        File.WriteAllText(orphan, "%PDF-1.4");

        Assert.Equal(1, Documents.SweepOrphanedDocuments(_tempDir));
        Assert.False(File.Exists(orphan), "a document whose agent is gone must not survive a restart");
    }

    [Fact(DisplayName = "a document this running agent owns is left alone")]
    public void ADocumentThisRunningAgentOwnsIsLeftAlone()
    {
        var live = Path.Combine(_tempDir, $"{Environment.ProcessId}-live.pdf");
        File.WriteAllText(live, "%PDF-1.4");

        Assert.Equal(0, Documents.SweepOrphanedDocuments(_tempDir));
        Assert.True(File.Exists(live), "deleting a document mid-print would fail the job it belongs to");
    }

    /// <summary>
    /// Files written before the pid naming existed, and anything else that finds
    /// its way in here, have no owner this can check. Left alone on purpose:
    /// guessing wrong deletes a live job's document, and the alternative costs
    /// only that one stale file stays until it is cleaned up by hand.
    /// </summary>
    [Fact(DisplayName = "a file with no readable owner is left alone")]
    public void AFileWithNoReadableOwnerIsLeftAlone()
    {
        var legacy = Path.Combine(_tempDir, "deadbeefcafe.pdf");
        File.WriteAllText(legacy, "%PDF-1.4");

        Assert.Equal(0, Documents.SweepOrphanedDocuments(_tempDir));
        Assert.True(File.Exists(legacy));
    }

    [Fact(DisplayName = "non-pdf files in the temp directory are never touched")]
    public void NonPdfFilesInTheTempDirectoryAreNeverTouched()
    {
        var other = Path.Combine(_tempDir, $"{DeadPid}-notes.txt");
        File.WriteAllText(other, "not a customer document");

        Assert.Equal(0, Documents.SweepOrphanedDocuments(_tempDir));
        Assert.True(File.Exists(other));
    }

    /// <summary>Called before the first job on a fresh install, when the directory may not exist yet.</summary>
    [Fact(DisplayName = "a missing temp directory is not an error")]
    public void AMissingTempDirectoryIsNotAnError()
    {
        Assert.Equal(0, Documents.SweepOrphanedDocuments(Path.Combine(_tempDir, "never-created")));
    }

    // --- validation ----------------------------------------------------------
    //
    // Not in the Kotlin suite. They are here because this is the one part of
    // Documents.kt whose engine changed in the port - PDFBox's Loader.loadPDF
    // became PDFium through Docnet - and the rejections are what stands between a
    // corrupt or locked document and a printer that would otherwise be handed it.

    /// <summary>
    /// A minimal but genuine two-page PDF, written by hand rather than pulled
    /// from a fixture so the test knows exactly how many pages should come back.
    /// </summary>
    private string WriteTwoPagePdf()
    {
        const string content = "0 0 0 rg 100 400 400 300 re f";
        var objects = new List<string>
        {
            "<< /Type /Catalog /Pages 2 0 R >>",
            "<< /Type /Pages /Kids [3 0 R 4 0 R] /Count 2 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 595 842] /Contents 5 0 R >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 595 842] >>",
            $"<< /Length {content.Length} >>\nstream\n{content}\nendstream",
        };

        var pdf = new StringBuilder();
        var offsets = new List<int> { 0 };
        pdf.Append("%PDF-1.4\n");
        for (var i = 0; i < objects.Count; i++)
        {
            offsets.Add(pdf.Length);
            pdf.Append($"{i + 1} 0 obj\n{objects[i]}\nendobj\n");
        }

        var xrefAt = pdf.Length;
        pdf.Append($"xref\n0 {objects.Count + 1}\n0000000000 65535 f \n");
        for (var i = 1; i <= objects.Count; i++) pdf.Append($"{offsets[i]:D10} 00000 n \n");
        pdf.Append($"trailer\n<< /Size {objects.Count + 1} /Root 1 0 R >>\nstartxref\n{xrefAt}\n%%EOF\n");

        var path = Path.Combine(_tempDir, "two-page.pdf");
        File.WriteAllText(path, pdf.ToString(), Encoding.ASCII);
        return path;
    }
}
