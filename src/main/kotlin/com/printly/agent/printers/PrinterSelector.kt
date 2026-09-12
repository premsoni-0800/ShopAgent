package com.printly.agent.printers

import com.printly.agent.models.ColorMode
import com.printly.agent.models.DuplexMode
import com.printly.agent.models.PrintJobItem
import com.printly.agent.models.PrinterReportedStatus
import com.printly.agent.printing.isPrintToFileDriver

data class SelectionResult(val printer: LocalPrinter?, val reason: String? = null)

/**
 * Deterministic, explainable printer selection - direct port of the Python
 * agent's `select_printer`: exclude anything proven incompatible or
 * unusable, score what's left, and never print with the wrong settings just
 * because a printer was available.
 */
fun selectPrinter(item: PrintJobItem, printers: List<LocalPrinter>): SelectionResult {
    // Whether this machine has any real printer at all, judged across every
    // printer rather than only the usable ones - a shop whose only laser is
    // offline still has a laser, and the right answer then is "this cannot be
    // printed now", not "written to a file instead".
    val hasPhysicalPrinter = printers.any { !isPrintToFileDriver(it.windowsPrinterName) }

    val candidates = printers
        .filter { it.status != PrinterReportedStatus.OFFLINE && it.status != PrinterReportedStatus.ERROR }
        .filterNot { provenIncompatible(item, it) }
        // Never quietly swap a real printer for one that writes a file.
        //
        // Scoring alone was not enough. A mono laser is *proven incompatible*
        // with a colour job and drops out above, leaving "Microsoft Print to
        // PDF" as the only candidate - so the shop's colour order would be
        // written to disk, reported PRINT_COMPLETED, and the student told to
        // come and collect paper that was never printed. Failing with
        // PRINTER_INCOMPATIBLE is the honest answer: a shop with only a mono
        // laser genuinely cannot fulfil a colour order, and needs to be told.
        //
        // Still selectable when there is nothing else on the machine, which is
        // every dev box and this project's own test setup.
        .filterNot { hasPhysicalPrinter && isPrintToFileDriver(it.windowsPrinterName) }

    if (candidates.isEmpty()) return SelectionResult(printer = null, reason = "PRINTER_INCOMPATIBLE")

    val best = candidates.sortedWith(compareByDescending<LocalPrinter> { score(item, it) }.thenBy { it.windowsPrinterName }).first()
    return SelectionResult(printer = best)
}

private fun provenIncompatible(item: PrintJobItem, printer: LocalPrinter): Boolean {
    if (item.colorMode == ColorMode.COLOR && printer.colorCapable == false) return true
    if (item.duplexMode == DuplexMode.DOUBLE_SIDED && printer.duplexCapable == false) return true
    if (printer.paperSizes.isNotEmpty() && item.paperSize !in printer.paperSizes) return true
    return false
}

private fun score(item: PrintJobItem, printer: LocalPrinter): Int {
    var score = 0
    if (printer.isSystemDefault) score += 10
    if (item.colorMode == ColorMode.COLOR && printer.colorCapable == true) score += 3
    if (item.colorMode == ColorMode.BLACK_AND_WHITE && printer.colorCapable != null) score += 1
    if (item.duplexMode == DuplexMode.DOUBLE_SIDED && printer.duplexCapable == true) score += 3
    if (printer.paperSizes.isNotEmpty() && item.paperSize in printer.paperSizes) score += 3
    // An UNKNOWN capability is not disqualifying (see provenIncompatible), but
    // it is not rewarded either - a printer with proven-matching capabilities
    // is always preferred over one that merely might work.
    return score
}
