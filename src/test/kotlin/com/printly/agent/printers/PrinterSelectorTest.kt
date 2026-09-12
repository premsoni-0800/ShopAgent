package com.printly.agent.printers

import com.printly.agent.models.ColorMode
import com.printly.agent.models.DuplexMode
import com.printly.agent.models.Orientation
import com.printly.agent.models.PaperSize
import com.printly.agent.models.PrintJobItem
import com.printly.agent.models.PrinterReportedStatus
import org.junit.jupiter.api.Assertions.assertEquals
import org.junit.jupiter.api.Assertions.assertNull
import org.junit.jupiter.api.Test

class PrinterSelectorTest {

    private fun item(
        colorMode: ColorMode = ColorMode.BLACK_AND_WHITE,
        duplexMode: DuplexMode = DuplexMode.SINGLE_SIDED,
        paperSize: PaperSize = PaperSize.A4,
    ) = PrintJobItem(
        itemId = "item-1", documentId = "doc-1", fileName = "f.pdf",
        paperSize = paperSize, colorMode = colorMode, duplexMode = duplexMode,
        orientation = Orientation.PORTRAIT, copies = 1, pageRange = null, documentPageCount = 3,
    )

    private fun printer(
        name: String,
        isDefault: Boolean = false,
        status: PrinterReportedStatus = PrinterReportedStatus.READY,
        colorCapable: Boolean? = null,
        duplexCapable: Boolean? = null,
        paperSizes: Set<PaperSize> = emptySet(),
    ) = LocalPrinter(name, name, isDefault, status, colorCapable, duplexCapable, paperSizes)

    @Test
    fun `excludes offline and error printers`() {
        val printers = listOf(
            printer("Offline", status = PrinterReportedStatus.OFFLINE),
            printer("Errored", status = PrinterReportedStatus.ERROR),
        )
        val result = selectPrinter(item(), printers)
        assertNull(result.printer)
        assertEquals("PRINTER_INCOMPATIBLE", result.reason)
    }

    @Test
    fun `excludes a printer proven not to support color`() {
        val printers = listOf(printer("BW-only", colorCapable = false))
        val result = selectPrinter(item(colorMode = ColorMode.COLOR), printers)
        assertNull(result.printer)
    }

    @Test
    fun `does not exclude a printer with unknown color capability`() {
        // An UNKNOWN capability is never treated as proven-incompatible - it
        // just scores lower than a proven match.
        val printers = listOf(printer("Unknown-caps", colorCapable = null))
        val result = selectPrinter(item(colorMode = ColorMode.COLOR), printers)
        assertEquals("Unknown-caps", result.printer?.windowsPrinterName)
    }

    @Test
    fun `prefers the system default when otherwise equal`() {
        val printers = listOf(printer("Other"), printer("Default", isDefault = true))
        val result = selectPrinter(item(), printers)
        assertEquals("Default", result.printer?.windowsPrinterName)
    }

    @Test
    fun `among non-default printers prefers proven capability matches`() {
        val printers = listOf(
            printer("UnknownCaps"),
            printer("ColorCapable", colorCapable = true, duplexCapable = true, paperSizes = setOf(PaperSize.A4)),
        )
        val result = selectPrinter(item(colorMode = ColorMode.COLOR, duplexMode = DuplexMode.DOUBLE_SIDED), printers)
        assertEquals("ColorCapable", result.printer?.windowsPrinterName)
    }

    @Test
    fun `excludes a printer whose paper sizes are known and do not include the requested one`() {
        val printers = listOf(printer("LetterOnly", paperSizes = setOf(PaperSize.LETTER)))
        val result = selectPrinter(item(paperSize = PaperSize.A4), printers)
        assertNull(result.printer)
    }

    /**
     * The exact shape of a real shop PC: a mono laser plus Windows' built-in
     * PDF writer, with the PDF writer left as the system default (which it
     * very often is). On score alone the virtual printer wins outright - it is
     * the default (+10) and advertises every common paper size (+3) - and the
     * order would be written to a file, reported as printed, and the student
     * told to collect paper that does not exist.
     */
    @Test
    fun `a real printer beats the system-default PDF writer`() {
        val printers = listOf(
            printer(
                "Microsoft Print to PDF", isDefault = true, colorCapable = true, duplexCapable = true,
                paperSizes = setOf(PaperSize.A4, PaperSize.A3, PaperSize.LETTER, PaperSize.LEGAL),
            ),
            printer(
                "Hewlett-Packard HP LaserJet M1005", isDefault = false, colorCapable = false,
                duplexCapable = true, paperSizes = setOf(PaperSize.A4),
            ),
        )

        val result = selectPrinter(item(), printers)

        assertEquals("Hewlett-Packard HP LaserJet M1005", result.printer?.windowsPrinterName)
    }

    /** Nothing else to print with - a dev box, or this project's own test machine. */
    @Test
    fun `a virtual printer is still chosen when it is the only one`() {
        val printers = listOf(
            printer("Microsoft Print to PDF", isDefault = true, colorCapable = true, paperSizes = setOf(PaperSize.A4)),
        )

        val result = selectPrinter(item(), printers)

        assertEquals("Microsoft Print to PDF", result.printer?.windowsPrinterName)
    }

    /**
     * A shop with only a mono laser genuinely cannot fulfil a colour order,
     * and has to be told so. Falling back to the PDF writer here would write
     * the order to disk, report it printed, and send the student to collect
     * paper that never existed - the failure this whole file exists to stop.
     */
    @Test
    fun `a colour job a real printer cannot do fails rather than becoming a file`() {
        val printers = listOf(
            printer("Microsoft Print to PDF", colorCapable = true, paperSizes = setOf(PaperSize.A4)),
            printer("Hewlett-Packard HP LaserJet M1005", colorCapable = false, paperSizes = setOf(PaperSize.A4)),
        )

        val result = selectPrinter(item(colorMode = ColorMode.COLOR), printers)

        assertNull(result.printer)
        assertEquals("PRINTER_INCOMPATIBLE", result.reason)
    }

    /** Even an offline laser means this machine is a real printer's machine - its jobs wait, they do not become files. */
    @Test
    fun `an offline real printer does not hand the job to the PDF writer`() {
        val printers = listOf(
            printer("Microsoft Print to PDF", isDefault = true, colorCapable = true, paperSizes = setOf(PaperSize.A4)),
            printer("Hewlett-Packard HP LaserJet M1005", status = PrinterReportedStatus.OFFLINE, paperSizes = setOf(PaperSize.A4)),
        )

        val result = selectPrinter(item(), printers)

        assertNull(result.printer)
        assertEquals("PRINTER_INCOMPATIBLE", result.reason)
    }
}
