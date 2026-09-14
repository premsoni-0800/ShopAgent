using System.Drawing.Printing;
using System.Runtime.Versioning;
using Microsoft.Extensions.Logging;

namespace PrintlyAgent.Printing;

/// <summary>
/// Printing an order by hand, with the shop owner choosing the printer.
///
/// Port of printing/ManualPrint.kt.
///
/// This is the path for when automatic printing is off. The agent is then not
/// going to claim anything, and the dashboard cannot print either - a WebView
/// ignores window.print() outright - so the Print button needed a route of its
/// own.
///
/// It deliberately shows the real Windows print dialog rather than sending the
/// job straight to the default printer. Automatic printing being off is the shop
/// saying "I want to decide", and the decision usually being made is *which*
/// printer: the colour one, the A3 one, the one that is not jammed. Printing
/// silently to whatever Windows defaults to would answer a question nobody
/// asked.
///
/// Rendering goes through PDFium, exactly as the automatic path does, so a
/// document that prints correctly on its own prints correctly here too rather
/// than depending on whatever PDF reader happens to be installed.
/// </summary>
[SupportedOSPlatform("windows")]
public static class ManualPrint
{
    private const int ConnectTimeoutSeconds = 15;
    private const int ReadTimeoutSeconds = 60;

    /// <summary>
    /// The document itself has to be small enough to be a print job rather than
    /// a way to fill the disk. The link is signed and short-lived, but it is
    /// still a URL handed over by a page.
    /// </summary>
    private const long MaxBytes = 200L * 1024 * 1024;

    /// <summary>
    /// Downloads <paramref name="url"/>, shows the print dialog, and prints what
    /// the owner chose.
    ///
    /// Returns printed=false with no error when the dialog was cancelled - that
    /// is an ordinary outcome and not something to apologise for, and the
    /// dashboard needs to tell the two apart so it does not mark an order
    /// printed that nobody printed.
    /// </summary>
    public static async Task<IReadOnlyDictionary<string, object?>> PrintWithDialogAsync(
        string url, string? jobName, ILogger? log = null)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri))
        {
            return Fail("That document link is not valid");
        }
        if (uri.Scheme is not ("http" or "https"))
        {
            return Fail("Only web links can be printed");
        }
        // Said plainly, rather than discovered as a dialog that never opens.
        if (!Environment.UserInteractive)
        {
            return Fail("This computer cannot show a print dialog");
        }
        if (PrinterSettings.InstalledPrinters.Count == 0)
        {
            return Fail("No printers are installed on this computer");
        }

        string? file = null;
        try
        {
            file = await DownloadAsync(uri).ConfigureAwait(false);
            return PrintFile(file, jobName ?? "Printly document", log);
        }
        catch (Exception exc)
        {
            log?.LogWarning(exc, "manual_print_failed");
            return Fail(exc.Message);
        }
        finally
        {
            // The file is a customer's document on a shop counter, so it does
            // not outlive the job that needed it.
            if (file is not null)
            {
                try { File.Delete(file); } catch (IOException) { }
            }
        }
    }

    private static Dictionary<string, object?> Fail(string error) =>
        new() { ["ok"] = false, ["error"] = error };

    private static async Task<string> DownloadAsync(Uri uri)
    {
        using var http = new HttpClient
        {
            Timeout = TimeSpan.FromSeconds(ConnectTimeoutSeconds + ReadTimeoutSeconds),
        };

        using var response = await http.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            throw new IOException($"The document could not be fetched (HTTP {(int)response.StatusCode})");
        }

        var target = Path.Combine(Path.GetTempPath(), $"printly-manual-{Guid.NewGuid():N}.pdf");
        await using var source = await response.Content.ReadAsStreamAsync().ConfigureAwait(false);
        await using var output = File.Create(target);

        var buffer = new byte[64 * 1024];
        long total = 0;
        while (true)
        {
            var read = await source.ReadAsync(buffer).ConfigureAwait(false);
            if (read == 0) break;
            total += read;
            if (total > MaxBytes) throw new IOException("That document is too large to print");
            await output.WriteAsync(buffer.AsMemory(0, read)).ConfigureAwait(false);
        }

        return target;
    }

    /// <summary>
    /// Shows the dialog on a UI-capable thread and prints there.
    ///
    /// The print dialog is a modal window. Shown from a worker thread it can
    /// come up behind the application window or not at all, which on a shop
    /// counter reads as the Print button doing nothing - so it is run on a
    /// single-threaded-apartment thread with an owner window in front, and
    /// waited on.
    /// </summary>
    private static IReadOnlyDictionary<string, object?> PrintFile(string file, string jobName, ILogger? log)
    {
        IReadOnlyDictionary<string, object?> result = Fail("The print dialog could not be shown");

        var ui = new Thread(() =>
        {
            using var renderer = new PdfPageRenderer(file);
            using var document = new PrintDocument();
            document.DocumentName = jobName;

            var pageIndex = 0;
            document.PrintPage += (_, args) =>
            {
                using var image = (System.Drawing.Image)renderer.RenderPage(pageIndex).Clone();
                var bounds = args.MarginBounds;
                var scale = Math.Min((double)bounds.Width / image.Width, (double)bounds.Height / image.Height);
                args.Graphics!.DrawImage(
                    image, bounds.Left, bounds.Top, (int)(image.Width * scale), (int)(image.Height * scale));
                pageIndex++;
                args.HasMorePages = pageIndex < renderer.PageCount;
            };

            using var dialog = new System.Windows.Forms.PrintDialog
            {
                Document = document,
                AllowSomePages = true,
                UseEXDialog = true,
            };

            // Windows gives a modal dialog whatever window of this process is in
            // front. A WebView2 host has one, but this can also be reached with
            // no window up at all - so an owner is provided either way: a 1x1
            // top-most form, alive only while the dialog is.
            using var anchor = new System.Windows.Forms.Form
            {
                StartPosition = System.Windows.Forms.FormStartPosition.CenterScreen,
                FormBorderStyle = System.Windows.Forms.FormBorderStyle.None,
                Size = new System.Drawing.Size(1, 1),
                TopMost = true,
                ShowInTaskbar = false,
            };
            anchor.Show();
            anchor.BringToFront();

            try
            {
                if (dialog.ShowDialog(anchor) != System.Windows.Forms.DialogResult.OK)
                {
                    log?.LogInformation("manual_print_cancelled job={JobName}", jobName);
                    result = new Dictionary<string, object?> { ["ok"] = true, ["printed"] = false };
                    return;
                }

                log?.LogInformation("manual_print_dialog_accepted job={JobName}", jobName);
                document.Print();
                log?.LogInformation(
                    "manual_print_submitted job={JobName} printer={Printer}",
                    jobName, document.PrinterSettings.PrinterName);

                result = new Dictionary<string, object?>
                {
                    ["ok"] = true,
                    ["printed"] = true,
                    ["printer"] = document.PrinterSettings.PrinterName,
                };
            }
            catch (Exception exc)
            {
                log?.LogWarning(exc, "manual_print_dialog_failed");
                result = Fail(exc.Message);
            }
            finally
            {
                anchor.Close();
            }
        });

        // STA is required for a Windows common dialog. Without it ShowDialog
        // throws, or worse, returns without ever painting.
        ui.SetApartmentState(ApartmentState.STA);
        ui.IsBackground = true;
        ui.Start();
        ui.Join();

        return result;
    }
}
