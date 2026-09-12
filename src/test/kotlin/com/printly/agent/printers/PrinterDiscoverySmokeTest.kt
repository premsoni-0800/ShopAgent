package com.printly.agent.printers

import org.junit.jupiter.api.Test

/**
 * Real `javax.print` printer enumeration - not a mock. Confirms
 * [discoverPrinters] actually talks to the OS print service registry on this
 * machine. Every Windows machine has at least "Microsoft Print to PDF" or
 * "Microsoft XPS Document Writer" installed by default, so this asserts
 * nothing brittle about the *count* - it just proves the call itself
 * succeeds and prints what it found for manual inspection.
 */
class PrinterDiscoverySmokeTest {

    @Test
    fun `discovers real printers on this machine without throwing`() {
        val printers = discoverPrinters()
        println("Discovered ${printers.size} printer(s):")
        for (p in printers) {
            println(
                "  - ${p.windowsPrinterName} (default=${p.isSystemDefault}, status=${p.status}, " +
                    "color=${p.colorCapable}, duplex=${p.duplexCapable}, paperSizes=${p.paperSizes})",
            )
        }
    }
}
