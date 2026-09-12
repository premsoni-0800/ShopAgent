package com.printly.agent.printing

import com.printly.agent.models.ColorMode
import com.printly.agent.models.DuplexMode
import com.printly.agent.models.PaperSize
import org.apache.pdfbox.pdmodel.PDDocument
import org.apache.pdfbox.pdmodel.PDPage
import org.junit.jupiter.api.Assertions.assertTrue
import org.junit.jupiter.api.Test
import java.nio.file.Files
import javax.print.PrintServiceLookup

/**
 * Confirms printing to a print-to-file driver (Microsoft Print to PDF) never
 * blocks on a native Save-As dialog - it must complete synchronously and
 * leave a real PDF behind, since nothing is there to click through such a
 * dialog on an unattended shop PC.
 */
class PrintToFileDialogSmokeTest {

    @Test
    fun `printing to Microsoft Print to PDF completes without a dialog and writes a file`() {
        val service = PrintServiceLookup.lookupPrintServices(null, null)
            .firstOrNull { it.name.contains("Microsoft Print to PDF", ignoreCase = true) }
        if (service == null) {
            println("Microsoft Print to PDF not installed on this machine - skipping")
            return
        }

        val pdfPath = Files.createTempFile("printly-dialog-smoke", ".pdf")
        PDDocument().use { doc ->
            doc.addPage(PDPage())
            doc.save(pdfPath.toFile())
        }

        val options = PrintOptions(ColorMode.BLACK_AND_WHITE, DuplexMode.SINGLE_SIDED, PaperSize.A4, 1, null)

        val start = System.currentTimeMillis()
        val jobNameToken = printPdf(service, pdfPath, options, documentPageCount = 1)
        val elapsedMs = System.currentTimeMillis() - start

        println("printPdf returned in ${elapsedMs}ms, jobNameToken=$jobNameToken")
        // A blocked Save-As dialog waits on a human indefinitely; this bound
        // only needs to be well past worst-case cold-start driver init
        // (observed ~15s on first use in this process) - not a performance
        // assertion, just distinguishing "slow" from "hung forever."
        assertTrue(elapsedMs < 60_000, "printPdf took ${elapsedMs}ms - looks like it blocked on a dialog")

        val outputFile = virtualPrinterOutputDir().resolve("$jobNameToken.pdf")
        assertTrue(Files.exists(outputFile), "expected output PDF at $outputFile")
        Files.deleteIfExists(outputFile)
        Files.deleteIfExists(pdfPath)
    }
}
