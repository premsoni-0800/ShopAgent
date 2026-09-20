namespace PrintlyAgent.Printing;

/// <summary>
/// Recognising the printers that write a file instead of putting ink on paper.
///
/// Port of the isPrintToFileDriver half of printing/PrintSubmission.kt.
///
/// Matched by name because Windows offers nothing better to match on: physical
/// and virtual printers look the same to the print subsystem and both report a
/// file destination as supported, so there is no capability that separates them.
///
/// Substring matching keeps this working across the suffixes Windows adds to
/// driver names ("OneNote (Desktop)", "Foxit Reader PDF Printer", a "(Copy 1)"
/// on a reinstall). An unrecognised virtual printer simply prints normally and
/// shows its dialog - the safe direction to be wrong in, and visible
/// immediately, unlike the alternative.
/// </summary>
public static class PrintToFile
{
    /// <summary>
    /// Lowercase, matched as substrings. Windows' own virtual drivers plus the
    /// PDF printers commonly installed alongside them.
    /// </summary>
    private static readonly string[] Markers =
    {
        "print to pdf",
        "xps document writer",
        "onenote",
        "adobe pdf",
        "pdfcreator",
        "cutepdf",
        "bullzip",
        "dopdf",
        "primopdf",
        "nitro pdf",
        "foxit reader pdf printer",
        "microsoft shared fax driver",

        // The queue, not the driver.
        //
        // "microsoft shared fax driver" above is a DRIVER name, and everything
        // here is matched against the PRINTER name - which Windows calls
        // simply "Fax". So it matched nothing, and a machine with the fax
        // feature switched on counted as having a real printer: the PDF
        // writers were hidden behind it, and the selector would pick it. On a
        // black-and-white A4 job the fax queue scores exactly what a mono
        // laser scores, and "Fax" sorts first, so it won the tie-break and the
        // order was faxed instead of printed.
        //
        // Matched loosely on purpose, because it also catches the separate fax
        // queue a multifunction printer installs alongside its printing one
        // ("HP LaserJet MFP M428 fax"). The paper-printing queue of such a
        // device does not carry the word, so it is unaffected.
        "fax",
    };

    public static bool IsPrintToFileDriver(string printerName)
    {
        if (string.IsNullOrEmpty(printerName)) return false;
        var name = printerName.ToLowerInvariant();
        return Markers.Any(marker => name.Contains(marker, StringComparison.Ordinal));
    }
}
