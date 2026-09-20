using System.Diagnostics;
using System.Globalization;
using Docnet.Core;
using Docnet.Core.Exceptions;
using Docnet.Core.Models;
using Docnet.Core.Readers;

namespace PrintlyAgent.Printing;

/// <summary>
/// Port of Kotlin's <c>DocumentValidationError : RuntimeException</c>. Named for
/// what it is rather than given the conventional Exception suffix, so the two
/// ports read the same at the call sites that catch it.
/// </summary>
public sealed class DocumentValidationError : Exception
{
    public DocumentValidationError(string message) : base(message)
    {
    }
}

/// <summary>
/// The fetch itself failed, with the status the server gave.
///
/// Separate from <see cref="DocumentValidationError"/> because the two want
/// opposite handling. A document that is empty or is not a PDF will still be
/// empty in two seconds; a 503 from a host that cold-starts will very likely
/// not be. Retrying the first kind is what let one order whose upload never
/// finished sit on the shop's only print worker for three minutes - sixty
/// seconds of download timeout, three times over, with backoff between - while
/// every other order in the shop waited behind it.
/// </summary>
public sealed class DocumentFetchError : Exception
{
    public DocumentFetchError(int statusCode, string message) : base(message) => StatusCode = statusCode;

    public int StatusCode { get; }

    /// <summary>
    /// Whether asking again could plausibly give a different answer.
    ///
    /// 5xx is the server having a bad moment, 408 and 429 are it saying so
    /// outright. Everything else in the 4xx range is a settled "no" - 404 for a
    /// document whose upload never completed, 403 for a signature that has
    /// expired - and asking four more times only delays telling the shop.
    /// </summary>
    public bool WorthRetrying => StatusCode is 408 or 429 || StatusCode >= 500;
}

/// <summary>
/// Getting a customer's document onto this machine and making sure it does not
/// outlive the job - port of printing/Documents.kt.
///
/// Deliberately does not inspect what it downloaded. There was a ValidatePdf
/// here that opened the file and refused an empty, unreadable or encrypted one
/// before it could reach a printer; it was removed on request, so whatever
/// arrives is what gets printed.
/// </summary>
public static class Documents
{
    /// <summary>
    /// Streams a signed R2 URL to a restricted temp file - direct port of the
    /// Python agent's <c>documents.py::download</c>. The caller is responsible
    /// for deleting it once printing has finished, succeeded or not.
    ///
    /// <para>
    /// Kotlin sets the deadline with <c>OkHttpClient.newBuilder().callTimeout()</c>,
    /// which bounds the whole call including the body. <c>HttpClient.Timeout</c>
    /// cannot be changed per request and is shared with every other caller of
    /// this client, so the same deadline is imposed with a linked
    /// CancellationTokenSource - and the body is read under that same token, or
    /// the timeout would cover only the response headers and a stalled download
    /// would hang forever.
    /// </para>
    /// </summary>
    public static async Task<string> DownloadDocumentAsync(
        HttpClient http,
        string url,
        string tempDir,
        long timeoutSeconds,
        CancellationToken ct = default)
    {
        Directory.CreateDirectory(tempDir);
        // Prefixed with this process's id so SweepOrphanedDocuments can tell a
        // file another running agent is still printing from one abandoned by an
        // agent that died.
        var pid = Environment.ProcessId;
        var destination = Path.Combine(tempDir, $"{pid}-{Guid.NewGuid():N}.pdf");

        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(TimeSpan.FromSeconds(timeoutSeconds));

        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        using var response = await http
            .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, deadline.Token)
            .ConfigureAwait(false);

        if (!response.IsSuccessStatusCode)
        {
            throw new DocumentFetchError(
                (int)response.StatusCode,
                $"the document could not be fetched: HTTP {(int)response.StatusCode}");
        }

        try
        {
            await using var output = File.Create(destination);
            await response.Content.CopyToAsync(output, deadline.Token).ConfigureAwait(false);
        }
        catch
        {
            // A half-written customer document, deleted here because nothing
            // else can. The path is only returned on success, so the caller
            // never learns this file exists and its own cleanup cannot reach
            // it - and the orphan sweep deliberately skips files belonging to a
            // live process, which this one is. Left alone it survives until the
            // agent restarts, which on a counter PC is weeks.
            try { File.Delete(destination); } catch (IOException) { }
            catch (UnauthorizedAccessException) { }
            throw;
        }

        return destination;
    }

    /// <summary>
    /// Deletes customer documents left behind by an agent that did not shut down.
    ///
    /// <para>
    /// The pipeline deletes each file in a <c>finally</c>, which covers every way
    /// a job can end - but not the ways the *process* can end. A crash, a kill
    /// from Task Manager, or the counter PC losing power leaves somebody's
    /// coursework sitting in the temp directory indefinitely, on a machine in a
    /// shop. The rule for these files is download, print, delete; this is what
    /// makes it true across a restart as well as across a job.
    /// </para>
    ///
    /// <para>
    /// Only sweeps files whose owning process is gone, so a second agent - or a
    /// long job still spooling in another instance - is never robbed of the
    /// document it is printing. A file whose name predates this scheme, or whose
    /// pid has since been recycled onto a live process, is left for the next run
    /// rather than risked.
    /// </para>
    /// </summary>
    public static int SweepOrphanedDocuments(string tempDir)
    {
        if (!Directory.Exists(tempDir)) return 0;
        var removed = 0;
        try
        {
            foreach (var entry in Directory.EnumerateFiles(tempDir, "*.pdf"))
            {
                var fileName = Path.GetFileName(entry);
                // Kotlin's substringBefore('-') keeps the whole name when there
                // is no dash, which then fails to parse - which is how a file
                // that predates the pid prefix falls through to "leave it".
                var dash = fileName.IndexOf('-');
                var head = dash < 0 ? fileName : fileName[..dash];
                if (!long.TryParse(head, NumberStyles.None, CultureInfo.InvariantCulture, out var pid)) continue;
                if (IsProcessAlive(pid)) continue;

                try
                {
                    if (!File.Exists(entry)) continue;
                    File.Delete(entry);
                    removed++;
                }
                catch (IOException)
                {
                    // Still held open by whoever is printing it. Next run.
                }
                catch (UnauthorizedAccessException)
                {
                }
            }
        }
        catch (Exception)
        {
            // A sweep is housekeeping, never the reason a job fails. Whatever was
            // removed before the directory read went wrong still counts.
        }
        return removed;
    }

    /// <summary>
    /// Whether a process with this id is running right now.
    ///
    /// <para>
    /// Kotlin asks <c>ProcessHandle.of(pid)</c>, which is empty for pid 0 -
    /// "pid 0 is not a real process handle on any supported OS", as the sweep
    /// test puts it. .NET does not agree: <c>Process.GetProcessById(0)</c>
    /// cheerfully returns the Windows System Idle Process, which is always
    /// running. Left unguarded that flips the sweep's whole meaning for the id
    /// the tests use to mean "dead", so the guard is explicit - and correct
    /// besides, since no agent has ever had pid 0.
    /// </para>
    ///
    /// <para>
    /// Anything else that goes wrong counts as alive. A file is deleted only when
    /// its owner is *known* to be gone; "could not tell" must not become "delete
    /// the document another agent is printing".
    /// </para>
    /// </summary>
    private static bool IsProcessAlive(long pid)
    {
        if (pid <= 0 || pid > int.MaxValue) return false;
        try
        {
            using var process = Process.GetProcessById((int)pid);
            return !process.HasExited;
        }
        catch (ArgumentException)
        {
            // The definitive answer: the OS has no process with this id.
            return false;
        }
        catch (Exception)
        {
            return true;
        }
    }

    /// <summary>
    /// <c>FPDF_ERR_PASSWORD</c> from fpdfview.h - PDFium's "this document needs a
    /// password I was not given", surfaced by Docnet as the exception's raw code.
    ///
    /// <para>
    /// Its neighbour FPDF_ERR_SECURITY (5, "unsupported security scheme") is
    /// deliberately left to the general branch: PDFBox does not recognise those
    /// documents either, so Kotlin reports them as unreadable rather than as
    /// password-protected, and the two builds say the same thing about the same
    /// file.
    /// </para>
    /// </summary>
    private const uint FpdfErrPassword = 4;
}
