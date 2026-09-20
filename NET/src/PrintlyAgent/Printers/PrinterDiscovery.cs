using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Microsoft.Extensions.Logging;
using PrintlyAgent.Models;
using PaperKind = System.Drawing.Printing.PaperKind;
using PrinterSettings = System.Drawing.Printing.PrinterSettings;

using PrintlyAgent.Printing;

namespace PrintlyAgent.Printers;

/// <summary>
/// Enumerates locally installed and connected printers - port of
/// printers/PrinterDiscovery.kt.
///
/// <para>
/// Kotlin reads the machine through <c>javax.print</c>, the JVM's own OS-backed
/// print service registry, which it chose as the direct equivalent of the Python
/// agent's <c>printers.py</c> (which uses <c>win32print.EnumPrinters</c>). The
/// .NET port goes to the same place by the shorter road: the name list and the
/// paper sizes come from <c>System.Drawing.Printing.PrinterSettings</c>, and the
/// queue status and the colour/duplex capabilities come from winspool and GDI
/// directly - the very APIs <c>javax.print</c> and <c>win32print</c> are both
/// sitting on top of.
/// </para>
///
/// <para>
/// That last part is not a preference. <c>PrinterSettings.SupportsColor</c> and
/// <c>CanDuplex</c> are plain <c>bool</c>s, and they get there by taking
/// <c>DeviceCapabilities</c>'s -1 - which means "this driver does not answer that
/// question" - and comparing it against zero, so "would not say" arrives as
/// <c>true</c>. <c>LocalPrinter.ColorCapable</c> is a <c>bool?</c> precisely
/// because that third answer has to survive: guessing <c>false</c> makes the
/// selector refuse printers that work, guessing <c>true</c> sends colour jobs to
/// mono lasers. So the capability calls are made here, and -1 is kept as null.
/// </para>
///
/// <para>
/// Never raises for a single bad driver - a printer that cannot be queried is
/// reported as UNKNOWN status with no known capabilities rather than dropped. A
/// printer missing from this list is invisible to the selector, which is a worse
/// failure than a printer the selector can see but knows nothing about.
/// </para>
/// </summary>
[SupportedOSPlatform("windows")]
public static class PrinterDiscovery
{
    /// <summary>
    /// The four sizes the backend can order, and the Windows paper kinds that
    /// count as each.
    ///
    /// <para>
    /// A list per size, as in Kotlin, because the mapping is not one-to-one.
    /// Kotlin matches a single <c>MediaSizeName</c> (ISO_A4) because
    /// <c>javax.print</c> has already collapsed the Windows DEVMODE constants
    /// down to it; .NET hands the DEVMODE kinds over unreduced, so
    /// DMPAPER_A4_SMALL and DMPAPER_A4_ROTATED arrive as distinct
    /// <see cref="PaperKind"/> values and have to be named here or a driver that
    /// reports only those would look like it cannot do A4 at all - and the
    /// selector would then refuse it (see PrinterSelector.ProvenIncompatible).
    /// </para>
    /// </summary>
    private static readonly IReadOnlyDictionary<PaperSize, PaperKind[]> PaperSizeMap =
        new Dictionary<PaperSize, PaperKind[]>
        {
            [PaperSize.A4] = new[] { PaperKind.A4, PaperKind.A4Small, PaperKind.A4Rotated },
            [PaperSize.A3] = new[] { PaperKind.A3, PaperKind.A3Rotated },
            [PaperSize.LETTER] = new[] { PaperKind.Letter, PaperKind.LetterSmall, PaperKind.LetterRotated },
            [PaperSize.LEGAL] = new[] { PaperKind.Legal },
        };

    /// <summary>
    /// This machine's printers, with their status read fresh every time.
    ///
    /// What a printer *is* - its name, whether it can do colour or duplex, the
    /// paper it takes - comes from the driver and does not change while the
    /// agent runs. What it is *doing* changes constantly, and is the only part
    /// worth asking about again.
    ///
    /// Measured, because it was not obvious: a full discovery took 367ms on a
    /// machine with two printers, 273ms of which was enumerating paper sizes
    /// through PrinterSettings.PaperSizes, which asks the driver. Reading every
    /// printer's status costs 2.6ms. This ran once per job on the print worker,
    /// immediately before printing, so every order paid a third of a second of
    /// idle printer to re-learn things that had not changed since the last one.
    ///
    /// So the shape is cached and the status is not. A printer that goes offline
    /// is still seen to be offline within one job, because that is the part
    /// being re-read.
    ///
    /// A printer that is installed or removed is picked up by the sync sweep,
    /// which passes askDrivers because noticing exactly that is what it is for -
    /// and which is off the print path, so it can afford to. The lifetime below
    /// is a backstop for anything that discovers without it, not the mechanism.
    /// </summary>
    /// <param name="askDrivers">
    /// Ask the drivers again rather than trusting the cached capabilities. Costs
    /// the full third of a second; only worth it away from a waiting printer.
    /// </param>
    public static IReadOnlyList<LocalPrinter> DiscoverPrinters(ILogger? log = null, bool askDrivers = false)
    {
        var shape = CachedShape(log, askDrivers);
        if (shape.Count == 0) return shape;

        var current = new List<LocalPrinter>(shape.Count);
        foreach (var printer in shape)
        {
            // The one thing that can have changed since the last job.
            current.Add(printer with { Status = StatusOf(ReadSpoolerInfo(printer.WindowsPrinterName)) });
        }
        return current;
    }

    /// <summary>How long a printer's capabilities are taken on trust.</summary>
    private static readonly TimeSpan ShapeLifetime = TimeSpan.FromMinutes(2);

    private static readonly object ShapeGate = new();
    private static IReadOnlyList<LocalPrinter>? _shape;
    private static DateTime _shapeReadAt;

    private static IReadOnlyList<LocalPrinter> CachedShape(ILogger? log, bool askDrivers)
    {
        lock (ShapeGate)
        {
            if (!askDrivers && _shape is not null && DateTime.UtcNow - _shapeReadAt < ShapeLifetime) return _shape;
            _shape = QueryPrinters(log);
            _shapeReadAt = DateTime.UtcNow;
            return _shape;
        }
    }

    /// <summary>
    /// Forgets the cached capabilities, so the next discovery asks the drivers
    /// again. For when the set of printers is known to have changed rather than
    /// merely suspected of it.
    /// </summary>
    public static void ForgetCachedPrinters()
    {
        lock (ShapeGate)
        {
            _shape = null;
        }
    }

    /// <summary>
    /// Whether this machine has a printer that puts ink on paper, as opposed to
    /// one that writes a file.
    ///
    /// Asked across every printer rather than only the usable ones: a shop whose
    /// only laser is switched off still has a laser, and the right answer then
    /// is that the order cannot be printed now - not that it should quietly go
    /// to a PDF instead.
    /// </summary>
    public static bool AnyPhysical(IReadOnlyList<LocalPrinter> printers) =>
        printers.Any(p => !PrintToFile.IsPrintToFileDriver(p.WindowsPrinterName));

    /// <summary>
    /// The printers worth telling the shop and the backend about.
    ///
    /// <para>
    /// "Microsoft Print to PDF", "OneNote (Desktop)", the XPS writer and the fax
    /// driver are on every Windows machine whether or not anyone wants them, and
    /// a counter that has a laser has no use for any of them. Reporting them put
    /// four entries in the shop's printer list where one belonged, and made the
    /// routing screen a question about which PDF writer should take colour work.
    /// </para>
    ///
    /// <para>
    /// Only when there is something real to show instead, which is the same rule
    /// <see cref="PrinterSelector.SelectPrinter"/> applies to choosing one - and
    /// deliberately so: a dev box and this project's own test setup have nothing
    /// but these, and a printer list that were empty there would say the agent
    /// had found nothing, while jobs went on printing to file. Both halves
    /// therefore ask <see cref="AnyPhysical"/>, so neither can start hiding a
    /// printer the other is still willing to print with.
    /// </para>
    /// </summary>
    public static IReadOnlyList<LocalPrinter> Reportable(IReadOnlyList<LocalPrinter> printers) =>
        AnyPhysical(printers)
            ? printers.Where(p => !PrintToFile.IsPrintToFileDriver(p.WindowsPrinterName)).ToList()
            : printers;

    private static IReadOnlyList<LocalPrinter> QueryPrinters(ILogger? log = null)
    {
        string? defaultName;
        try
        {
            // Equivalent of PrintServiceLookup.lookupDefaultPrintService(): a
            // PrinterSettings nobody has named reports the system default.
            defaultName = new PrinterSettings().PrinterName;
        }
        catch (Exception)
        {
            defaultName = null;
        }

        var installed = PrinterSettings.InstalledPrinters;
        var printers = new List<LocalPrinter>(installed.Count);

        for (var i = 0; i < installed.Count; i++)
        {
            var name = installed[i];
            try
            {
                // One spooler round trip per printer, reused three ways: the
                // queue status, the offline attribute, and the port name that
                // DeviceCapabilities wants alongside the printer name.
                var spooler = ReadSpoolerInfo(name);
                var settings = new PrinterSettings { PrinterName = name };

                printers.Add(new LocalPrinter(
                    WindowsPrinterName: name,
                    DisplayName: name,
                    IsSystemDefault: defaultName is not null
                                     && string.Equals(name, defaultName, StringComparison.OrdinalIgnoreCase),
                    Status: StatusOf(spooler),
                    ColorCapable: ColorCapable(name, spooler?.Port),
                    DuplexCapable: DuplexCapable(name, spooler?.Port),
                    PaperSizes: PaperSizesOf(settings)));
            }
            catch (Exception exc)
            {
                // ILogger's Log* helpers are extension methods, which `?.` cannot
                // invoke, hence the explicit null check.
                if (log is not null) log.LogWarning("printer_query_failed name={Name} error={Error}", name, exc);
                printers.Add(new LocalPrinter(
                    name, name, false, PrinterReportedStatus.UNKNOWN, null, null, new HashSet<PaperSize>()));
            }
        }

        return printers;
    }

    /// <summary>
    /// The printer's status, in the four-value vocabulary the backend speaks.
    ///
    /// <para>
    /// Kotlin asks two questions in order - is the queue accepting jobs
    /// (<c>PrinterIsAcceptingJobs</c>), and does any printer state reason carry
    /// <c>Severity.ERROR</c> - and this asks the same two of the bits those
    /// attributes are derived from. "Offline" first, because a printer that is
    /// switched off also reports paper-out and every other stale condition it
    /// was last in, and OFFLINE is the more useful of the two answers.
    /// </para>
    ///
    /// <para>
    /// UNKNOWN when the spooler would not answer at all. That is the same
    /// meaning Kotlin's catch-all gives it: not "fine" and not "broken", just
    /// unqueryable - and the selector treats it as usable rather than excluding
    /// it, so a printer the agent cannot interrogate still prints.
    /// </para>
    ///
    /// <para>
    /// PRINTER_STATUS_PAUSED is deliberately not an error and not offline: a
    /// paused queue still accepts jobs and prints them the moment it resumes,
    /// which is exactly what <c>javax.print</c> reports for it too.
    /// </para>
    /// </summary>
    private static PrinterReportedStatus StatusOf(SpoolerInfo? spooler)
    {
        if (spooler is null) return PrinterReportedStatus.UNKNOWN;
        if ((spooler.Attributes & PrinterAttributeWorkOffline) != 0) return PrinterReportedStatus.OFFLINE;
        if ((spooler.Status & OfflineMask) != 0) return PrinterReportedStatus.OFFLINE;
        if ((spooler.Status & ErrorMask) != 0) return PrinterReportedStatus.ERROR;
        return PrinterReportedStatus.READY;
    }

    /// <summary>
    /// Whether the driver says it can print in colour, or will not say.
    ///
    /// <para>
    /// DC_COLORDEVICE answers 1 for colour, 0 for mono and -1 when the driver
    /// does not implement the query. The -1 is the whole reason this is not
    /// <c>PrinterSettings.SupportsColor</c>: that property returns the same call
    /// as <c>!= 0</c>, which turns "would not say" into "yes, colour" and hands
    /// colour jobs to mono lasers. Kept as null here, exactly as Kotlin returns
    /// null when <c>getSupportedAttributeValues(Chromaticity)</c> is not
    /// supported.
    /// </para>
    /// </summary>
    private static bool? ColorCapable(string printerName, string? port)
    {
        var value = DeviceCapabilitiesW(printerName, port, DcColorDevice, IntPtr.Zero, IntPtr.Zero);
        return value < 0 ? null : value != 0;
    }

    /// <summary>
    /// Whether the driver says it can print both sides, or will not say.
    ///
    /// <para>
    /// DC_DUPLEX collapses Kotlin's two-sided values - <c>Sides.DUPLEX</c> and
    /// <c>Sides.TUMBLE</c>, long-edge and short-edge binding - into one answer,
    /// which is all the selector ever asks. -1 stays null for the same reason as
    /// colour: <c>PrinterSettings.CanDuplex</c> would report it as duplex-capable
    /// and the job would come out single-sided with nobody told.
    /// </para>
    /// </summary>
    private static bool? DuplexCapable(string printerName, string? port)
    {
        var value = DeviceCapabilitiesW(printerName, port, DcDuplex, IntPtr.Zero, IntPtr.Zero);
        return value < 0 ? null : value != 0;
    }

    /// <summary>
    /// The orderable sizes this printer advertises. Empty means "the driver did
    /// not list any", never "it supports none" - the selector reads an empty set
    /// as unknown and does not exclude on it.
    /// </summary>
    private static IReadOnlySet<PaperSize> PaperSizesOf(PrinterSettings settings)
    {
        var kinds = new HashSet<PaperKind>();
        var available = settings.PaperSizes;
        for (var i = 0; i < available.Count; i++) kinds.Add(available[i].Kind);

        var supported = new HashSet<PaperSize>();
        foreach (var (size, candidates) in PaperSizeMap)
        {
            if (candidates.Any(kinds.Contains)) supported.Add(size);
        }
        return supported;
    }

    /// <summary>
    /// What is wrong with this printer right now, named specifically enough to
    /// act on, or null when nothing is.
    ///
    /// Windows knows the difference between a jam, an open cover and an empty
    /// cartridge, and every one of those was being collapsed into
    /// <see cref="ErrorMask"/> and reported as a single ERROR. The shop was told
    /// "the printer needs attention" and had to walk over and work out which -
    /// which is the one piece of information it could have been given.
    ///
    /// Ordered most actionable first. A printer that is jammed and also has a
    /// full output tray is, to the person standing in front of it, jammed.
    /// </summary>
    public static PrinterCondition? CurrentCondition(string printerName)
    {
        var spooler = ReadSpoolerInfo(printerName);
        if (spooler is null) return null;
        return ConditionOf(spooler.Status, spooler.Attributes);
    }

    internal static PrinterCondition? ConditionOf(uint status, uint attributes)
    {
        // Set deliberately by a person, so it is reported as the spooler's own
        // "work offline" rather than as a printer that has gone missing.
        if ((attributes & PrinterAttributeWorkOffline) != 0) return PrinterCondition.OFFLINE;

        foreach (var (bit, condition) in Faults)
        {
            if ((status & bit) != 0) return condition;
        }
        return null;
    }

    /// <summary>
    /// Device faults, most actionable first.
    ///
    /// PrinterStatusError is last on purpose: it is the driver saying "something
    /// is wrong" without saying what, so any bit that does say what is worth
    /// more. A carriage fault, a sensor failure and anything else the driver has
    /// no specific bit for arrive here, which is why NEEDS_ATTENTION keeps its
    /// "check for a jam, an open cover, or a cartridge" wording - for those it
    /// really is the best that can be said.
    /// </summary>
    private static readonly (uint Bit, PrinterCondition Condition)[] Faults =
    {
        (PrinterStatusPaperJam, PrinterCondition.PAPER_JAM),
        (PrinterStatusDoorOpen, PrinterCondition.DOOR_OPEN),
        (PrinterStatusNoToner, PrinterCondition.OUT_OF_TONER),
        (PrinterStatusPaperOut, PrinterCondition.OUT_OF_PAPER),
        (PrinterStatusOutputBinFull, PrinterCondition.OUTPUT_BIN_FULL),
        (PrinterStatusManualFeed, PrinterCondition.MANUAL_FEED_REQUIRED),
        (PrinterStatusPaperProblem, PrinterCondition.PAPER_PROBLEM),
        (PrinterStatusOutOfMemory, PrinterCondition.OUT_OF_MEMORY),
        // Switched off, unplugged, or the machine sharing it has gone. Nothing
        // comes out until somebody goes and looks at the hardware.
        (PrinterStatusNotAvailable, PrinterCondition.NOT_REACHABLE),
        (PrinterStatusServerUnknown, PrinterCondition.NOT_REACHABLE),
        (PrinterStatusOffline, PrinterCondition.NOT_REACHABLE),
        (PrinterStatusUserIntervention, PrinterCondition.NEEDS_ATTENTION),
        (PrinterStatusError, PrinterCondition.NEEDS_ATTENTION),
    };

    /// <summary>What one GetPrinter call is read for. Null when it could not be made.</summary>
    private sealed record SpoolerInfo(uint Status, uint Attributes, string? Port);

    private static SpoolerInfo? ReadSpoolerInfo(string printerName)
    {
        if (!OpenPrinterW(printerName, out var handle, IntPtr.Zero)) return null;
        try
        {
            // Two-call idiom: ask for the size, then for the data. PRINTER_INFO_2
            // is variable length - the strings are packed into the tail of the
            // same buffer - so there is no fixed size to allocate up front.
            _ = GetPrinterW(handle, 2, IntPtr.Zero, 0, out var needed);
            if (needed <= 0) return null;

            var buffer = Marshal.AllocHGlobal(needed);
            try
            {
                if (!GetPrinterW(handle, 2, buffer, needed, out _)) return null;
                var info = Marshal.PtrToStructure<PRINTER_INFO_2>(buffer);
                // Read out of the buffer before it is freed, and copied rather
                // than marshalled as a string field: the pointer belongs to this
                // buffer, and letting the marshaller own it would have it free
                // memory it did not allocate.
                var port = info.pPortName == IntPtr.Zero ? null : Marshal.PtrToStringUni(info.pPortName);
                return new SpoolerInfo(info.Status, info.Attributes, port);
            }
            finally
            {
                Marshal.FreeHGlobal(buffer);
            }
        }
        finally
        {
            ClosePrinter(handle);
        }
    }

    // ------------------------------------------------------------------------
    // winspool.drv / GDI interop. Laid out field for field against winspool.h so
    // the layout can be audited against the header rather than against this port.
    // ------------------------------------------------------------------------

    // wingdi.h
    private const short DcDuplex = 7;
    private const short DcColorDevice = 32;

    // winspool.h - PRINTER_ATTRIBUTE_*
    private const uint PrinterAttributeWorkOffline = 0x00000400;

    // winspool.h - PRINTER_STATUS_*
    private const uint PrinterStatusError = 0x00000002;
    private const uint PrinterStatusPaperJam = 0x00000008;
    private const uint PrinterStatusPaperOut = 0x00000010;
    private const uint PrinterStatusPaperProblem = 0x00000040;
    private const uint PrinterStatusManualFeed = 0x00000020;
    private const uint PrinterStatusOffline = 0x00000080;
    private const uint PrinterStatusOutputBinFull = 0x00000800;
    private const uint PrinterStatusNotAvailable = 0x00001000;
    private const uint PrinterStatusNoToner = 0x00040000;
    private const uint PrinterStatusUserIntervention = 0x00100000;
    private const uint PrinterStatusOutOfMemory = 0x00200000;
    private const uint PrinterStatusDoorOpen = 0x00400000;
    private const uint PrinterStatusServerUnknown = 0x00800000;

    /// <summary>The printer is not reachable, so nothing will come out of it now.</summary>
    private const uint OfflineMask = PrinterStatusOffline | PrinterStatusNotAvailable;

    /// <summary>
    /// Conditions <c>javax.print</c> surfaces as a PrinterStateReason with
    /// Severity.ERROR - the printer is there, and something is wrong with it.
    /// </summary>
    private const uint ErrorMask =
        PrinterStatusError | PrinterStatusPaperJam | PrinterStatusPaperOut | PrinterStatusPaperProblem |
        PrinterStatusOutputBinFull | PrinterStatusNoToner | PrinterStatusUserIntervention |
        PrinterStatusOutOfMemory | PrinterStatusDoorOpen | PrinterStatusServerUnknown;

    // The marshaller fills these in; nothing in managed code ever assigns them,
    // and most are never read - they exist only to give the struct the byte
    // layout the spooler writes.
#pragma warning disable 0649, 0169

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct PRINTER_INFO_2
    {
        public IntPtr pServerName;
        public IntPtr pPrinterName;
        public IntPtr pShareName;
        public IntPtr pPortName;
        public IntPtr pDriverName;
        public IntPtr pComment;
        public IntPtr pLocation;
        public IntPtr pDevMode;
        public IntPtr pSepFile;
        public IntPtr pPrintProcessor;
        public IntPtr pDatatype;
        public IntPtr pParameters;
        public IntPtr pSecurityDescriptor;
        public uint Attributes;
        public uint Priority;
        public uint DefaultPriority;
        public uint StartTime;
        public uint UntilTime;
        public uint Status;
        public uint cJobs;
        public uint AveragePPM;
    }

#pragma warning restore 0649, 0169

    [DllImport("winspool.drv", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool OpenPrinterW(string pPrinterName, out IntPtr phPrinter, IntPtr pDefault);

    [DllImport("winspool.drv", SetLastError = true)]
    private static extern bool ClosePrinter(IntPtr hPrinter);

    [DllImport("winspool.drv", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool GetPrinterW(
        IntPtr hPrinter, int level, IntPtr pPrinter, int cbBuf, out int pcbNeeded);

    /// <summary>
    /// GDI's driver capability query. Returns -1 when the driver does not
    /// implement the capability being asked about, which is the answer this
    /// whole file exists to preserve.
    /// </summary>
    [DllImport("winspool.drv", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern int DeviceCapabilitiesW(
        string pDevice, string? pPort, short fwCapability, IntPtr pOutput, IntPtr pDevMode);
}
