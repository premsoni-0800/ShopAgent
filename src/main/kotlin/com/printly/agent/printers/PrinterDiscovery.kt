package com.printly.agent.printers

import com.printly.agent.models.PaperSize
import com.printly.agent.models.PrinterReportedStatus
import java.util.logging.Logger
import javax.print.PrintServiceLookup
import javax.print.attribute.standard.Chromaticity
import javax.print.attribute.standard.Media
import javax.print.attribute.standard.MediaSizeName
import javax.print.attribute.standard.PrinterIsAcceptingJobs
import javax.print.attribute.standard.PrinterStateReasons
import javax.print.attribute.standard.Severity
import javax.print.attribute.standard.Sides

private val log = Logger.getLogger("com.printly.agent.printers.PrinterDiscovery")

data class LocalPrinter(
    val windowsPrinterName: String,
    val displayName: String,
    val isSystemDefault: Boolean,
    val status: PrinterReportedStatus,
    /** Null when the driver would not say reliably - never guessed. */
    val colorCapable: Boolean?,
    val duplexCapable: Boolean?,
    val paperSizes: Set<PaperSize> = emptySet(),
)

private val PAPER_SIZE_MAP: Map<PaperSize, List<MediaSizeName>> = mapOf(
    PaperSize.A4 to listOf(MediaSizeName.ISO_A4),
    PaperSize.A3 to listOf(MediaSizeName.ISO_A3),
    PaperSize.LETTER to listOf(MediaSizeName.NA_LETTER),
    PaperSize.LEGAL to listOf(MediaSizeName.NA_LEGAL),
)

/**
 * Enumerates locally installed and connected printers via `javax.print` -
 * the JVM's own OS-backed print service registry, direct equivalent of the
 * Python agent's `printers.py` (which uses `win32print.EnumPrinters`). Never
 * raises for a single bad driver - a printer that cannot be queried is
 * reported as UNKNOWN status with no known capabilities rather than dropped.
 */
fun discoverPrinters(): List<LocalPrinter> {
    val defaultService = runCatching { PrintServiceLookup.lookupDefaultPrintService() }.getOrNull()
    return PrintServiceLookup.lookupPrintServices(null, null).map { service ->
        runCatching {
            LocalPrinter(
                windowsPrinterName = service.name,
                displayName = service.name,
                isSystemDefault = (service == defaultService),
                status = statusOf(service),
                colorCapable = colorCapable(service),
                duplexCapable = duplexCapable(service),
                paperSizes = paperSizes(service),
            )
        }.getOrElse { exc ->
            log.warning("printer_query_failed name=${service.name} error=$exc")
            LocalPrinter(service.name, service.name, false, PrinterReportedStatus.UNKNOWN, null, null, emptySet())
        }
    }
}

private fun statusOf(service: javax.print.PrintService): PrinterReportedStatus {
    val accepting = service.getAttribute(PrinterIsAcceptingJobs::class.java)
    if (accepting == PrinterIsAcceptingJobs.NOT_ACCEPTING_JOBS) return PrinterReportedStatus.OFFLINE

    val reasons = service.getAttribute(PrinterStateReasons::class.java)
    if (reasons != null && reasons.values.any { it == Severity.ERROR }) return PrinterReportedStatus.ERROR

    return PrinterReportedStatus.READY
}

private fun colorCapable(service: javax.print.PrintService): Boolean? {
    val values = service.getSupportedAttributeValues(Chromaticity::class.java, null, null) as? Array<*> ?: return null
    return values.any { it == Chromaticity.COLOR }
}

private fun duplexCapable(service: javax.print.PrintService): Boolean? {
    val values = service.getSupportedAttributeValues(Sides::class.java, null, null) as? Array<*> ?: return null
    return values.any { it == Sides.DUPLEX || it == Sides.TUMBLE }
}

private fun paperSizes(service: javax.print.PrintService): Set<PaperSize> {
    val values = service.getSupportedAttributeValues(Media::class.java, null, null) as? Array<*> ?: return emptySet()
    val mediaSizeNames = values.filterIsInstance<MediaSizeName>().toSet()
    return PAPER_SIZE_MAP.filterValues { candidates -> candidates.any { it in mediaSizeNames } }.keys
}
