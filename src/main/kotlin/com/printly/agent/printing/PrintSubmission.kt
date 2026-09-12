package com.printly.agent.printing

import com.printly.agent.models.ColorMode
import com.printly.agent.models.DuplexMode
import com.printly.agent.models.PaperSize
import org.apache.pdfbox.Loader
import org.apache.pdfbox.rendering.PDFRenderer
import java.awt.Graphics
import java.awt.Graphics2D
import java.awt.print.PageFormat
import java.awt.print.Printable
import java.awt.print.PrinterJob
import java.nio.file.Path
import java.util.UUID
import javax.print.PrintService
import javax.print.attribute.HashPrintRequestAttributeSet
import javax.print.attribute.standard.Chromaticity
import javax.print.attribute.standard.Copies
import javax.print.attribute.standard.JobName
import javax.print.attribute.standard.MediaSizeName
import javax.print.attribute.standard.Sides

data class PrintOptions(
    val colorMode: ColorMode,
    val duplexMode: DuplexMode,
    val paperSize: PaperSize,
    val copies: Int,
    /** 1-based, e.g. "1-5,8"; null means every page. */
    val pageRange: String?,
)

class PrintSubmissionError(message: String, cause: Throwable? = null) : RuntimeException(message, cause)

private val PAPER_SIZE_TO_MEDIA: Map<PaperSize, MediaSizeName> = mapOf(
    PaperSize.A4 to MediaSizeName.ISO_A4,
    PaperSize.A3 to MediaSizeName.ISO_A3,
    PaperSize.LETTER to MediaSizeName.NA_LETTER,
    PaperSize.LEGAL to MediaSizeName.NA_LEGAL,
)

/**
 * Submits a validated PDF to a Windows print service through `javax.print` -
 * the JDK's own OS-backed print pipeline, which itself submits through the
 * Windows print spooler exactly as the master prompt's "never talk to the
 * printer directly" rule requires. PDFBox rasterizes each page (already a
 * required dependency for PDF validation) onto an image `javax.print` hands
 * to the driver with [options] applied via a `PrintRequestAttributeSet` - the
 * same "always ask the driver for exactly this, never assume" rule the
 * Python agent's DEVMODE round-trip follows.
 *
 * Returns the unique `JobName` token this submission was tagged with, so the
 * caller can correlate it against the spooler's own queue afterwards - see
 * [SpoolerOutcomePoller]. `javax.print` never hands back a spooler job id
 * directly (unlike `win32gui.StartDoc`), so matching by a unique job name is
 * the closest available equivalent - a known, deliberate approximation, not
 * a hidden assumption.
 */
fun printPdf(service: PrintService, pdfPath: Path, options: PrintOptions, documentPageCount: Int): String {
    val pages = resolvePages(options.pageRange, documentPageCount)
    val jobNameToken = "printly-${UUID.randomUUID()}"

    val document = try {
        Loader.loadPDF(pdfPath.toFile())
    } catch (exc: Exception) {
        throw PrintSubmissionError("could not reopen validated PDF for printing: $exc", exc)
    }

    try {
        val renderer = PDFRenderer(document)
        val printerJob = PrinterJob.getPrinterJob()
        printerJob.printService = service

        printerJob.setPrintable(object : Printable {
            override fun print(graphics: Graphics, pageFormat: PageFormat, pageIndex: Int): Int {
                if (pageIndex >= pages.size) return Printable.NO_SUCH_PAGE
                val pageNumber = pages[pageIndex] // 1-based
                val image = renderer.renderImageWithDPI(pageNumber - 1, 150f)
                val g2d = graphics as Graphics2D

                // Uniform scale - fit-to-page without distorting aspect ratio,
                // matching a normal PDF-to-printer viewer's default rather
                // than stretching the image.
                val scale = minOf(pageFormat.imageableWidth / image.width, pageFormat.imageableHeight / image.height)
                g2d.translate(pageFormat.imageableX, pageFormat.imageableY)
                g2d.drawImage(image, 0, 0, (image.width * scale).toInt(), (image.height * scale).toInt(), null)
                return Printable.PAGE_EXISTS
            }
        })

        val attributes = HashPrintRequestAttributeSet()
        attributes.add(JobName(jobNameToken, null))
        attributes.add(Copies(maxOf(1, options.copies)))
        attributes.add(if (options.colorMode == ColorMode.COLOR) Chromaticity.COLOR else Chromaticity.MONOCHROME)
        attributes.add(if (options.duplexMode == DuplexMode.DOUBLE_SIDED) Sides.DUPLEX else Sides.ONE_SIDED)
        PAPER_SIZE_TO_MEDIA[options.paperSize]?.let { attributes.add(it) }

        try {
            printerJob.print(attributes)
        } catch (exc: Exception) {
            throw PrintSubmissionError("printing failed on ${service.name}: $exc", exc)
        }
    } finally {
        document.close()
    }

    return jobNameToken
}

/** 1-based page numbers, e.g. "1-5,8" -> [1,2,3,4,5,8]. Never trusts a range past [documentPageCount]. */
private fun resolvePages(pageRange: String?, documentPageCount: Int): List<Int> {
    if (pageRange.isNullOrBlank()) return (1..documentPageCount).toList()

    val pages = mutableListOf<Int>()
    for (part in pageRange.split(",")) {
        val trimmed = part.trim()
        if (trimmed.isEmpty()) continue
        val (start, end) = if ("-" in trimmed) {
            val (s, e) = trimmed.split("-", limit = 2)
            s.trim().toInt() to e.trim().toInt()
        } else {
            val n = trimmed.toInt()
            n to n
        }
        for (page in start..end) if (page in 1..documentPageCount) pages.add(page)
    }
    return pages.ifEmpty { (1..documentPageCount).toList() }
}
