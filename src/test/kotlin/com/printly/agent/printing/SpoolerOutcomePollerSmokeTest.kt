package com.printly.agent.printing

import org.junit.jupiter.api.Assertions.assertEquals
import org.junit.jupiter.api.Test
import javax.print.PrintServiceLookup

/**
 * Exercises the real WinSpool `OpenPrinter`/`EnumJobs` JNA bindings (not the
 * injectable fake) against whatever printer is actually installed on this
 * machine - confirms the native struct layout in [SpoolerOutcomePoller]
 * doesn't crash the JVM, which the injectable-lookup unit tests can't catch.
 */
class SpoolerOutcomePollerSmokeTest {

    @Test
    fun `polling a real printer for a job that was never submitted resolves without crashing`() {
        val printerName = PrintServiceLookup.lookupDefaultPrintService()?.name
            ?: PrintServiceLookup.lookupPrintServices(null, null).firstOrNull()?.name
        if (printerName == null) {
            println("No printer installed on this machine - skipping real WinSpool smoke test")
            return
        }

        println("Polling real printer: $printerName")
        val outcome = SpoolerOutcomePoller.pollJobOutcome(printerName, "printly-nonexistent-job-token", timeoutSeconds = 3.0, pollIntervalSeconds = 1.0)
        // No job by that name was ever submitted, so the queue (whatever is
        // in it) never contains a match - "not found, no prior error seen"
        // resolves to COMPLETED, same as a real job that finished and was
        // removed. The point of this test is that the native call sequence
        // completes cleanly at all, not this particular outcome.
        assertEquals(PrintOutcome.COMPLETED, outcome)
    }
}
