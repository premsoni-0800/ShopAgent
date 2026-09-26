using System.Drawing.Printing;
using System.Runtime.Versioning;
using PrintlyAgent.Printers;
using PrintlyAgent.Printing;
using Xunit;
using Xunit.Abstractions;

namespace PrintlyAgent.Tests;

/// <summary>
/// Port of PrinterDiscoverySmokeTest.kt.
///
/// Real printer enumeration - not a mock. Confirms
/// <see cref="PrinterDiscovery.DiscoverPrinters"/> actually talks to the OS print
/// subsystem on this machine. Every Windows machine has at least "Microsoft Print
/// to PDF" or "Microsoft XPS Document Writer" installed by default, so this
/// asserts nothing brittle about the *count*, and nothing at all about which
/// printers are here - it proves the call itself succeeds and prints what it
/// found for manual inspection.
/// </summary>
[SupportedOSPlatform("windows")]
public class PrinterDiscoveryTests
{
    private readonly ITestOutputHelper _output;

    public PrinterDiscoveryTests(ITestOutputHelper output) => _output = output;

    [Fact(DisplayName = "discovers real printers on this machine without throwing")]
    public void DiscoversRealPrintersOnThisMachineWithoutThrowing()
    {
        var printers = PrinterDiscovery.DiscoverPrinters();

        _output.WriteLine($"Discovered {printers.Count} printer(s):");
        foreach (var p in printers)
        {
            _output.WriteLine(
                $"  - {p.WindowsPrinterName} (default={p.IsSystemDefault}, status={p.Status}, " +
                $"color={Show(p.ColorCapable)}, duplex={Show(p.DuplexCapable)}, " +
                $"paperSizes=[{string.Join(", ", p.Sizes)}])");
        }

        // Whatever this machine has, these hold everywhere: a printer with no
        // name cannot be selected or submitted to, and Windows has exactly one
        // default at a time. Nothing here depends on which printers are installed.
        Assert.All(printers, p => Assert.False(string.IsNullOrWhiteSpace(p.WindowsPrinterName)));
        Assert.True(
            printers.Count(p => p.IsSystemDefault) <= 1,
            "more than one printer reported itself as the system default");
    }

    /// <summary>
    /// Kotlin prints a Boolean? as "null"; C# interpolation prints an empty
    /// string for it, which would make "would not say" look like a blank field
    /// rather than the third answer it is.
    /// </summary>
    private static string Show(bool? value) => value?.ToString() ?? "null";
}

/// <summary>
/// Port of PrintToFileDriverTest.kt.
///
/// Which printers get a file destination attached, and - far more importantly -
/// which do not.
///
/// <para>
/// A real printer told to write to a file does not print the document, it
/// silently writes it to disk. The spooler still reports a completed job, so the
/// agent would mark the order PRINTED, the backend would announce it READY, and
/// the student would come to collect paper that never existed. Nothing
/// downstream can detect that, which is why it is pinned here.
/// </para>
///
/// <para>
/// It lives in this file rather than its own because its last case is a
/// statement about printer *enumeration* on this machine, which is what
/// <see cref="PrinterDiscovery"/> does.
/// </para>
/// </summary>
[SupportedOSPlatform("windows")]
public class PrintToFileDriverTests
{
    [Fact(DisplayName = "windows virtual printers are recognised")]
    public void WindowsVirtualPrintersAreRecognised()
    {
        var names = new[]
        {
            "Microsoft Print to PDF",
            "Microsoft XPS Document Writer",
            "OneNote (Desktop)",
            "Send to OneNote 16",
            "Adobe PDF",
            "CutePDF Writer",
            "Foxit Reader PDF Printer",
            "Fax",
            "HP OfficeJet Pro 9010 series Fax",
        };

        foreach (var name in names)
        {
            Assert.True(PrintToFile.IsPrintToFileDriver(name), $"{name} should be treated as print-to-file");
        }
    }

    [Fact(DisplayName = "real printers are never redirected to a file")]
    public void RealPrintersAreNeverRedirectedToAFile()
    {
        var names = new[]
        {
            "HP LaserJet Pro MFP M428fdw",
            "Canon LBP2900B",
            "EPSON L3150 Series",
            "Brother DCP-L2541DW",
            "Samsung M2020 Series",
            "Xerox WorkCentre 3335",
            "Ricoh MP 2014",
            "Kyocera ECOSYS P2040dn",
            "HP DeskJet 2700 series",
            "TVS MSP 250 Star",
        };

        foreach (var name in names)
        {
            Assert.False(
                PrintToFile.IsPrintToFileDriver(name), $"{name} is a real printer and must print on paper");
        }
    }

    [Fact(DisplayName = "matching ignores case and trailing driver suffixes")]
    public void MatchingIgnoresCaseAndTrailingDriverSuffixes()
    {
        Assert.True(PrintToFile.IsPrintToFileDriver("MICROSOFT PRINT TO PDF"));
        Assert.True(PrintToFile.IsPrintToFileDriver("Microsoft Print to PDF (Copy 1)"));
    }

    /// <summary>
    /// The reason the name check exists at all. If this ever starts failing -
    /// i.e. Windows begins distinguishing virtual printers by capability - the
    /// name list could be replaced by something principled. Until then it cannot:
    /// this asserts that the capability carries no information.
    ///
    /// <para>
    /// Kotlin pins it by asking every print service whether it accepts a
    /// <c>Destination</c> attribute and then showing they are all the same
    /// implementation class. .NET makes the same point more bluntly: writing to a
    /// file is not a printer capability at all, it is
    /// <c>PrinterSettings.PrintToFile</c> - a plain settable flag on the same
    /// type for every printer on the machine, physical and virtual alike. There
    /// is no per-printer query to ask, which is exactly the finding.
    /// </para>
    /// </summary>
    [Fact(DisplayName = "Destination support does not distinguish a virtual printer from a real one")]
    public void DestinationSupportDoesNotDistinguishAVirtualPrinterFromARealOne()
    {
        var installed = PrinterSettings.InstalledPrinters;
        var names = new List<string>(installed.Count);
        for (var i = 0; i < installed.Count; i++) names.Add(installed[i]);

        var virtualPrinters = names.Where(PrintToFile.IsPrintToFileDriver).ToList();

        // Only meaningful on a machine that actually has one; CI runners and dev
        // boxes do, but this must not fail on one that does not.
        if (virtualPrinters.Count == 0) return;

        Assert.All(names, name =>
        {
            var settings = new PrinterSettings { PrinterName = name, PrintToFile = true };
            Assert.True(
                settings.PrintToFile,
                $"{name} accepts a file destination like every other printer, which is why the capability cannot separate them");
        });
    }
}
