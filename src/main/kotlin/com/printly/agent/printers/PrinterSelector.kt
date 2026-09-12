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
    val candidates = printers
        .filter { it.status != PrinterReportedStatus.OFFLINE && it.status != PrinterReportedStatus.ERROR }
        .filterNot { provenIncompatible(item, it) }

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
    // A printer that writes a file is the last thing a shop wants chosen: the
    // order never reaches paper, yet everything downstream reports success
    // and the student is told to come and collect it.
    //
    // This is not hypothetical. "Microsoft Print to PDF" is the Windows
    // default on a great many machines, and it advertises colour and every
    // common paper size - so on a shop PC with a mono laser attached it
    // outscored the real printer on the two things that matter most here
    // (+10 for being the default, +3 for proven paper support) and would have
    // quietly swallowed every job.
    //
    // Penalised rather than excluded, so a machine with nothing but virtual
    // printers - any dev box, and this project's own testing setup - still
    // selects one. The penalty only has to beat the highest real score.
    if (isPrintToFileDriver(printer.windowsPrinterName)) score -= 100
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
