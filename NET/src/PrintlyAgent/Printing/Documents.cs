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
        return await DownloadDocumentToAsync(http, url, destination, timeoutSeconds, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// The same download, to a caller-chosen path.
    ///
    /// <para>
    /// Split out for the held-document store, whose filenames have to be worked
    /// out rather than invented: the process that eventually prints a held order
    /// is very often not the one that fetched it, so there is nobody left to
    /// remember a random name. Kept as the one implementation both paths call
    /// rather than a second copy, because "downloads a customer's document" is
    /// not a thing worth having two of.
    /// </para>
    /// </summary>
    public static async Task<string> DownloadDocumentToAsync(
        HttpClient http,
        string url,
        string destination,
        long timeoutSeconds,
        CancellationToken ct = default)
    {
        var parent = Path.GetDirectoryName(destination);
        if (!string.IsNullOrEmpty(parent)) Directory.CreateDirectory(parent);

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

        await using (var output = File.Create(destination))
        {
            await response.Content.CopyToAsync(output, deadline.Token).ConfigureAwait(false);
        }

        return destination;
    }

    /// <summary>
    /// Where a held order's document lives: one directory per job, one file per
    /// item, both named after ids the backend already gave us.
    ///
    /// <para>
    /// Deterministic on purpose, and it is the whole reason this is a method
    /// rather than a name chosen at download time. A held order is fetched in
    /// the morning and printed after lunch, very often by a different process -
    /// the shop closes the app, Windows updates, the counter PC is restarted.
    /// Anything remembered only in memory is gone by then; anything random needs
    /// an index to find it again, which is one more thing that can disagree with
    /// the disk. The job id and the item id are both already in the local row
    /// and in the job detail, so the path can simply be recomputed whenever it
    /// is wanted.
    /// </para>
    /// </summary>
    public static string HeldDocumentPath(string heldDir, string jobId, string itemId) =>
        Path.Combine(heldDir, SanitiseId(jobId), SanitiseId(itemId) + ".pdf");

    /// <summary>
    /// Moves a freshly downloaded document into the held store.
    ///
    /// <para>
    /// A move rather than a copy, so there is never a window in which the same
    /// customer document exists twice on a shop's disk, and so the temp copy
    /// cannot be left behind for the orphan sweep to find. Overwrite is on
    /// because that is what makes re-fetching a missing held file idempotent.
    /// </para>
    /// </summary>
    public static string StoreHeldDocument(string source, string heldDir, string jobId, string itemId)
    {
        var destination = HeldDocumentPath(heldDir, jobId, itemId);
        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        File.Move(source, destination, overwrite: true);
        return destination;
    }

    /// <summary>
    /// Deletes a job's held directory once its pages have gone to a printer.
    ///
    /// <para>
    /// Best-effort and silent: the documents have printed by the time this runs,
    /// so a file that cannot be removed is a housekeeping problem, not something
    /// to fail an order over. A job that was never held has no directory here,
    /// which is why the normal print path can call this unconditionally.
    /// </para>
    /// </summary>
    public static void DiscardHeldDocuments(string heldDir, string jobId)
    {
        try
        {
            var dir = Path.Combine(heldDir, SanitiseId(jobId));
            if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
        }
        catch
        {
            // Nothing here is worth interrupting a finished print for.
        }
    }

    /// <summary>
    /// Keeps an id to the characters that are safe in a path segment.
    ///
    /// <para>
    /// These ids are server-generated and have never been anything but hex and
    /// dashes, so in practice this changes nothing - it is here because they are
    /// used to build a filesystem path, and a path built from a value this
    /// process did not choose is worth being uninteresting about. A stray
    /// separator would otherwise write a customer's document somewhere other
    /// than the held store.
    /// </para>
    /// </summary>
    private static string SanitiseId(string id) =>
        string.Create(id.Length, id, (span, source) =>
        {
            for (var i = 0; i < source.Length; i++)
            {
                var c = source[i];
                span[i] = char.IsLetterOrDigit(c) || c == '-' || c == '_' ? c : '_';
            }
        });

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
    ///
    /// <para>
    /// Reaches only <paramref name="tempDir"/> itself, and not one level down.
    /// That is what keeps held documents safe without this method needing to
    /// know they exist: they live under <c>Settings.HeldDir</c>, a sibling
    /// directory, and for them a dead owning process means the agent was
    /// restarted between the shop accepting an order and its student arriving -
    /// which is the ordinary case, not a leak.
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
