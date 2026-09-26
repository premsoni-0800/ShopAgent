using System.Drawing;
using System.Drawing.Printing;
using System.Runtime.Versioning;
using Microsoft.Extensions.Logging;
using PrintlyAgent.Models;

// System.Drawing.Printing has a PaperSize of its own, and it means something
// different: theirs is one entry in a driver's list of loadable trays, ours is
// the size the customer paid for. An explicit alias beats both namespace
// imports, so the bare name always means the order's size and the driver's is
// spelled out in full wherever it appears.
using PaperSize = PrintlyAgent.Models.PaperSize;
using Orientation = PrintlyAgent.Models.Orientation;

namespace PrintlyAgent.Printing;

public sealed record PrintOptions(
    ColorMode ColorMode,
    DuplexMode DuplexMode,
    PaperSize PaperSize,
    int Copies,
    /// <summary>1-based, e.g. "1-5,8"; null means every page.</summary>
    string? PageRange,
    Orientation Orientation = Orientation.PORTRAIT);

public sealed class PrintSubmissionError : Exception
{
    public PrintSubmissionError(string message, Exception? cause = null) : base(message, cause) { }
}

/// <summary>
/// The driver accepted the job and then stopped making progress.
///
/// Distinct from <see cref="PrintSubmissionError"/> because the outcome is
/// genuinely unknown rather than failed: pages may well have come out before it
/// stalled. Observed on a real 3,100-page job that printed 2,860 pages and then
/// froze - the spooler kept it as "Printing, Retained" and the blocking print
/// call never returned at all.
/// </summary>
public sealed class PrintSubmissionStalled : Exception
{
    public PrintSubmissionStalled(string message) : base(message) { }
}

/// <summary>
/// Submits a validated PDF to a Windows printer through the OS print pipeline.
///
/// Port of printing/PrintSubmission.kt. Kotlin used javax.print, which submits
/// through the Windows spooler; .NET uses System.Drawing.Printing, which submits
/// through the same spooler. Neither talks to a printer directly, which is the
/// rule that matters. PDFium rasterises each page (see
/// <see cref="PdfPageRenderer"/>) onto the page's Graphics with the options
/// applied to PrinterSettings - the same "always ask the driver for exactly
/// this, never assume" rule the Kotlin follows.
///
/// Returns the unique document name this submission was tagged with, so the
/// caller can correlate it against the spooler's own queue afterwards - see
/// SpoolerOutcomePoller. Neither print API hands back a spooler job id directly,
/// so matching by a unique name is the closest available equivalent - a known,
/// deliberate approximation, not a hidden assumption.
/// </summary>
[SupportedOSPlatform("windows")]
public static class PrintSubmission
{
    /// <summary>
    /// The most copies of one document this will ever send to a printer.
    ///
    /// A ceiling rather than a validation rule: the number arrives from the
    /// server and is cast to a short on its way into DEVMODE, so something has
    /// to bound it before the cast does it silently and wrongly. 999 is far
    /// past any real counter order.
    /// </summary>
    private const int MaxCopies = 999;

    /// <summary>
    /// How long a job may make no progress at all before it is treated as stuck.
    ///
    /// Not a total time limit: a 3,000-page document legitimately takes a long
    /// while, and killing it for being big would be worse than the bug. What is
    /// never legitimate is the spooler's own page count standing still - a job
    /// that is working climbs, and one that has stopped climbing has stopped.
    /// </summary>
    private const long StallTimeoutSeconds = 300L;
    private const int ProgressPollSeconds = 15;

    private static readonly Dictionary<PaperSize, PaperKind> PaperSizeToKind = new()
    {
        [PaperSize.A4] = PaperKind.A4,
        [PaperSize.A3] = PaperKind.A3,
        [PaperSize.LETTER] = PaperKind.Letter,
        [PaperSize.LEGAL] = PaperKind.Legal,
    };

    public static string PrintPdf(
        string printerName,
        string pdfPath,
        PrintOptions options,
        ILogger? log = null)
    {
        var jobNameToken = $"printly-{Guid.NewGuid()}";

        // The page count is taken from the renderer rather than passed in.
        //
        // It used to come from a validation pass that opened the document
        // beforehand to check it was printable; that pass has been removed, so
        // this is now the first and only time the file is opened. A document
        // that cannot be opened fails here instead of being screened out
        // earlier - which is the deliberate trade: nothing inspects the file
        // before it is sent.
        PdfPageRenderer renderer;
        try
        {
            renderer = new PdfPageRenderer(pdfPath);
        }
        catch (Exception exc)
        {
            throw new PrintSubmissionError($"could not open the PDF for printing: {exc}", exc);
        }

        var pages = PageRange.Resolve(options.PageRange, renderer.PageCount);

        try
        {
            using var document = new PrintDocument();
            document.DocumentName = jobNameToken;
            document.PrinterSettings.PrinterName = printerName;
            // Clamped, not just floored. Copies arrives from the server DTO and
            // is never bounded anywhere on the way here, and the cast is to a
            // *short*: 65536 truncates to 0 and 32768 to -32768, either of
            // which goes into DEVMODE's dmCopies and makes the paper disagree
            // with what the customer paid for - in one direction or, on a
            // driver that reads it unsigned, spectacularly in the other.
            document.PrinterSettings.Copies = (short)Math.Clamp(options.Copies, 1, MaxCopies);
            document.PrinterSettings.Collate = true;

            // Asked of the driver explicitly rather than assumed. A driver that
            // cannot do what was asked will say so; one that was never asked
            // quietly does something else.
            document.DefaultPageSettings.Color = options.ColorMode == ColorMode.COLOR;
            // The orientation the student chose. It was carried all the way here
            // on every job and never applied, so a landscape order came out
            // portrait. Landscape pages bind on the short edge when printed
            // double-sided, the same as any viewer's "flip on short edge".
            var landscape = options.Orientation == Orientation.LANDSCAPE;
            document.DefaultPageSettings.Landscape = landscape;
            document.PrinterSettings.Duplex =
                options.DuplexMode != DuplexMode.DOUBLE_SIDED ? Duplex.Simplex
                : landscape ? Duplex.Horizontal : Duplex.Vertical;

            if (PaperSizeToKind.TryGetValue(options.PaperSize, out var kind))
            {
                foreach (System.Drawing.Printing.PaperSize candidate in document.PrinterSettings.PaperSizes)
                {
                    if (candidate.Kind != kind) continue;
                    document.DefaultPageSettings.PaperSize = candidate;
                    break;
                }
            }

            // A "print-to-file" driver (Microsoft Print to PDF, XPS Document
            // Writer, ...) asks the OS for a destination filename via a native
            // Save-As dialog on every job unless one is supplied up front -
            // fatal for an unattended agent, since nothing is there to click it.
            //
            // Which driver gets one is decided by NAME, and never by asking
            // whether the printer supports printing to a file. That check looks
            // like the principled way to do it and is actively dangerous: every
            // Windows printer - physical or virtual - reports that it can,
            // because "print to file" is a capability Windows offers for any
            // printer. Attaching a file destination to a real printer does not
            // print the document; it silently writes it to disk. The spooler
            // still sees a completed job, so the pipeline would report
            // PRINT_COMPLETED, the order would go READY, and the student would
            // be told to collect paper that was never printed.
            //
            // So the default is to print normally, and only a driver recognised
            // as print-to-file is redirected. Getting that wrong for an unlisted
            // virtual printer costs a stuck dialog on a dev machine; getting it
            // wrong the other way costs a customer their order.
            if (PrintToFile.IsPrintToFileDriver(printerName))
            {
                var outputDir = VirtualPrinterOutputDir();
                Directory.CreateDirectory(outputDir);
                document.PrinterSettings.PrintToFile = true;
                document.PrinterSettings.PrintFileName = Path.Combine(outputDir, $"{jobNameToken}.pdf");
            }

            var pageIndex = 0;
            document.PrintPage += (_, args) =>
            {
                var pageNumber = pages[pageIndex]; // 1-based
                using var image = (Image)renderer.RenderPage(pageNumber - 1).Clone();

                PageLayout.DrawOnSheet(args, image, PdfPageRenderer.RenderDpi);

                pageIndex++;
                args.HasMorePages = pageIndex < pages.Count;
            };

            SubmitAndWatch(document, printerName, jobNameToken, log);
        }
        finally
        {
            renderer.Dispose();
        }

        return jobNameToken;
    }

    /// <summary>
    /// Runs the blocking submit, and gives up on it if the driver stops
    /// responding.
    ///
    /// PrintDocument.Print() blocks until the driver has finished with the whole
    /// document, and there is no timeout on it. When a driver wedges - as
    /// Microsoft Print to PDF did on a 3,100-page job, stalling at 2,860 pages
    /// with the spooler still calling it "Printing" - that call simply never
    /// returns. The job then sits at DOWNLOADED forever: never failed, never
    /// unknown, never surfaced to anyone, while permanently holding one of the
    /// agent's few concurrent print slots. Four of those and the shop stops
    /// printing silently.
    ///
    /// So the submit runs on its own thread and this watches the spooler's page
    /// count beside it. Returning early leaks that thread - it is still blocked
    /// in the driver and cannot be safely killed - which is a deliberate trade:
    /// one parked thread is recoverable on the next restart, a wedged print slot
    /// and an invisible job are not.
    /// </summary>
    private static void SubmitAndWatch(
        PrintDocument document, string printerName, string jobNameToken, ILogger? log)
    {
        // Locals captured by the closure, deliberately NOT fields. More than one
        // print job can be in flight (Settings.MaxConcurrentPrintJobs), and
        // shared state here would let two jobs overwrite each other's
        // completion signal - one would return as soon as the other's driver
        // finished. [ThreadStatic] would be worse still: the watcher thread
        // would never see the failure the submit thread recorded, so a failed
        // print would look like a clean one.
        Exception? submissionFailure = null;
        using var submissionDone = new ManualResetEventSlim(false);

        var submission = new Thread(() =>
        {
            try { document.Print(); }
            catch (Exception exc) { submissionFailure = exc; }
            finally { submissionDone.Set(); }
        })
        {
            IsBackground = true,
            Name = $"printly-submit-{jobNameToken}",
        };

        submission.Start();

        var detector = new StallDetector<SpoolerOutcomePoller.JobProgress>(StallTimeoutSeconds);

        while (true)
        {
            if (submissionDone.Wait(TimeSpan.FromSeconds(ProgressPollSeconds)))
            {
                // Read after the event is set, so the write on the submit thread
                // happens-before this read.
                if (submissionFailure is { } failure)
                {
                    throw new PrintSubmissionError($"printing failed on {printerName}: {failure}", failure);
                }
                return; // the driver finished, one way or another
            }

            // Still printing. Ask whether it is actually moving.
            var progress = SpoolerOutcomePoller.GetJobProgress(printerName, jobNameToken, log);
            var stalledFor = detector.Sample(progress, Stopwatch());
            if (stalledFor is null) continue;

            throw new PrintSubmissionStalled(
                $"the printer stopped responding after {progress?.PagesPrinted ?? 0} pages - " +
                $"it made no progress for {stalledFor}s. Check the printer and the Windows print queue.");
        }
    }

    // A monotonic reading in nanoseconds, not the wall clock: the stall rule
    // measures a duration, and a counter PC resyncing its clock mid-print must
    // not create or erase a stall.
    private static long Stopwatch() =>
        (long)(System.Diagnostics.Stopwatch.GetTimestamp() * (1_000_000_000.0 / System.Diagnostics.Stopwatch.Frequency));

    /// <summary>
    /// Where a print-to-file driver's output actually lands - not a secret, not
    /// customer data retention (it is the agent's own already-printed copy, in
    /// its own app-data folder, not a shop-browsable location).
    /// </summary>
    internal static string VirtualPrinterOutputDir()
    {
        var localAppData = Environment.GetEnvironmentVariable("LOCALAPPDATA");
        var baseDir = !string.IsNullOrEmpty(localAppData)
            ? localAppData
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".printlyagentnet");
        return Path.Combine(baseDir, Core.AppConstants.AppName, "virtual-printer-output");
    }
}
