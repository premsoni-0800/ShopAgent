package com.printly.agent.printing

import org.junit.jupiter.api.Assertions.assertFalse
import org.junit.jupiter.api.Assertions.assertTrue
import org.junit.jupiter.api.Test
import javax.print.PrintService
import javax.print.PrintServiceLookup
import javax.print.attribute.standard.Destination

/**
 * Which printers get a file destination attached, and - far more importantly -
 * which do not.
 *
 * A real printer handed a [Destination] does not print the document, it
 * silently writes it to disk. The spooler still reports a completed job, so
 * the agent would mark the order PRINTED, the backend would announce it READY,
 * and the student would come to collect paper that never existed. Nothing
 * downstream can detect that, which is why it is pinned here.
 */
class PrintToFileDriverTest {

    @Test
    fun `windows virtual printers are recognised`() {
        listOf(
            "Microsoft Print to PDF",
            "Microsoft XPS Document Writer",
            "OneNote (Desktop)",
            "Send to OneNote 16",
            "Adobe PDF",
            "CutePDF Writer",
            "Foxit Reader PDF Printer",
        ).forEach { assertTrue(isPrintToFileDriver(it), "$it should be treated as print-to-file") }
    }

    @Test
    fun `real printers are never redirected to a file`() {
        listOf(
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
        ).forEach { assertFalse(isPrintToFileDriver(it), "$it is a real printer and must print on paper") }
    }

    @Test
    fun `matching ignores case and trailing driver suffixes`() {
        assertTrue(isPrintToFileDriver("MICROSOFT PRINT TO PDF"))
        assertTrue(isPrintToFileDriver("Microsoft Print to PDF (Copy 1)"))
    }

    /**
     * The reason the name check exists at all. If this ever starts failing -
     * i.e. Windows begins distinguishing virtual printers by capability - the
     * name list could be replaced by something principled. Until then it
     * cannot: this asserts that the capability carries no information.
     */
    @Test
    fun `Destination support does not distinguish a virtual printer from a real one`() {
        val services: Array<PrintService> = PrintServiceLookup.lookupPrintServices(null, null)
        val virtual = services.filter { isPrintToFileDriver(it.name) }

        // Only meaningful on a machine that actually has one; CI runners and
        // dev boxes do, but this must not fail on one that does not.
        if (virtual.isEmpty()) return

        assertTrue(
            virtual.all { it.isAttributeCategorySupported(Destination::class.java) },
            "a print-to-file driver is expected to accept a Destination",
        )
        assertTrue(
            services.all { it.javaClass.name == "sun.print.Win32PrintService" },
            "every Windows print service is the same implementation class, which is why the capability cannot separate them",
        )
    }
}
