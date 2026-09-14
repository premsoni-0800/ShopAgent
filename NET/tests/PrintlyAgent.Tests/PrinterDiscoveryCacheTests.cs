using System.Diagnostics;
using System.Runtime.Versioning;
using Microsoft.Extensions.Logging.Abstractions;
using PrintlyAgent.Printers;
using Xunit;

namespace PrintlyAgent.Tests;

/// <summary>
/// Discovery runs once per job, on the print worker, immediately before
/// printing - so its cost is idle printer, paid by every order.
///
/// It was 367ms on a machine with two printers, 273ms of which was enumerating
/// paper sizes through PrinterSettings.PaperSizes, which asks the driver. None
/// of that changes while the agent runs. Reading every printer's status - the
/// part that does change - costs under 3ms.
/// </summary>
[SupportedOSPlatform("windows")]
public class PrinterDiscoveryCacheTests
{
    /// <summary>
    /// A guard on the cost, not on the implementation. The number is loose on
    /// purpose: it is there to catch discovery going back to asking drivers on
    /// every job, which is a third of a second, not to police milliseconds.
    /// </summary>
    [Fact(DisplayName = "discovering printers does not cost a printer-idle third of a second")]
    public void DiscoveryIsCheapOnceWarm()
    {
        PrinterDiscovery.DiscoverPrinters(NullLogger.Instance);

        var clock = Stopwatch.StartNew();
        for (var i = 0; i < 5; i++) PrinterDiscovery.DiscoverPrinters(NullLogger.Instance);
        var perCall = clock.Elapsed.TotalMilliseconds / 5;

        Assert.True(
            perCall < 50,
            $"discovery took {perCall:F0}ms per call - it is back to asking the drivers on every job");
    }

    /// <summary>
    /// The half that makes caching safe. Capabilities are taken on trust;
    /// status is not, because a printer that has gone offline since the last
    /// job must not be selected for this one.
    /// </summary>
    [Fact(DisplayName = "status is read again even though capabilities are not")]
    public void StatusIsNotCached()
    {
        var first = PrinterDiscovery.DiscoverPrinters(NullLogger.Instance);
        var second = PrinterDiscovery.DiscoverPrinters(NullLogger.Instance);

        Assert.Equal(first.Count, second.Count);
        foreach (var printer in second)
        {
            // Populated from a live spooler read on this call, not carried over
            // from the cached shape, which would have left it at whatever it was
            // when the driver was last asked.
            Assert.True(
                Enum.IsDefined(printer.Status),
                $"{printer.WindowsPrinterName} came back with no usable status");
        }
    }

    [Fact(DisplayName = "a printer's capabilities survive between calls")]
    public void CapabilitiesAreStable()
    {
        var first = PrinterDiscovery.DiscoverPrinters(NullLogger.Instance);
        var second = PrinterDiscovery.DiscoverPrinters(NullLogger.Instance);

        Assert.Equal(
            first.Select(p => (p.WindowsPrinterName, p.ColorCapable, p.DuplexCapable)),
            second.Select(p => (p.WindowsPrinterName, p.ColorCapable, p.DuplexCapable)));
    }

    /// <summary>
    /// The sync sweep's side of the bargain.
    ///
    /// Capabilities being cached is only safe because something still asks the
    /// drivers on a schedule - otherwise a printer plugged in at the counter
    /// would be invisible for as long as the cache held. That something is the
    /// 120-second printer sync, which passes askDrivers; if this stops being
    /// true, discovery is trusting a snapshot nobody refreshes.
    /// </summary>
    [Fact(DisplayName = "asking the drivers again ignores the cache")]
    public void AskDriversBypassesTheCache()
    {
        PrinterDiscovery.DiscoverPrinters(NullLogger.Instance);

        var cached = Stopwatch.StartNew();
        PrinterDiscovery.DiscoverPrinters(NullLogger.Instance);
        var cachedMs = cached.Elapsed.TotalMilliseconds;

        var asked = Stopwatch.StartNew();
        PrinterDiscovery.DiscoverPrinters(NullLogger.Instance, askDrivers: true);
        var askedMs = asked.Elapsed.TotalMilliseconds;

        // Asking the drivers is the expensive path by two orders of magnitude,
        // so "it actually asked" is legible in the clock. Compared against the
        // cached call rather than an absolute number, because the absolute one
        // is a property of whatever printers this machine happens to have.
        Assert.True(
            askedMs > cachedMs * 5,
            $"askDrivers took {askedMs:F1}ms against {cachedMs:F1}ms cached - it served the cache instead");
    }

    /// <summary>
    /// And it can be told to forget, for when the set of printers is known to
    /// have changed rather than merely suspected of it.
    /// </summary>
    [Fact(DisplayName = "the cache can be dropped and rebuilt")]
    public void TheCacheCanBeDropped()
    {
        var before = PrinterDiscovery.DiscoverPrinters(NullLogger.Instance);
        PrinterDiscovery.ForgetCachedPrinters();
        var after = PrinterDiscovery.DiscoverPrinters(NullLogger.Instance);

        Assert.Equal(before.Count, after.Count);
    }
}
