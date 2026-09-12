package com.printly.agent.printing

import com.printly.agent.models.ColorMode
import com.printly.agent.models.DuplexMode
import com.printly.agent.models.PaperSize
import org.junit.jupiter.api.Test
import java.nio.file.Files
import java.nio.file.Paths
import java.util.concurrent.Executors
import java.util.concurrent.TimeUnit
import java.util.concurrent.atomic.AtomicInteger
import javax.print.PrintServiceLookup
import kotlin.system.measureNanoTime

/**
 * How long the agent's print pipeline takes for a queue of documents.
 *
 * Runs the agent's own [printPdf] and [SpoolerOutcomePoller] at the same
 * concurrency the agent itself uses, so the number describes the real thing
 * rather than a model of it.
 *
 * Measures render, spool, and the spooler confirming the job finished. It does
 * not include the backend round trips a real job also makes - claim, download,
 * two status reports - which are network-bound and add a roughly constant cost
 * per job on top.
 *
 * Opt-in: it prints real jobs (to a file writer, but real ones), so it stays
 * out of the normal suite and runs only when asked:
 *
 *   ./gradlew test --tests '*PrintThroughputBenchmark*' -DrunBenchmark=true \
 *       -Dbench.jobs=100 -Dbench.file=C:\path\to\10-pg-blank.pdf
 */
class PrintThroughputBenchmark {

    @Test
    fun `time a queue of print jobs`() {
        if (System.getenv("RUN_BENCHMARK") != "true") {
            println("PrintThroughputBenchmark skipped - set RUN_BENCHMARK=true to run it")
            return
        }

        val file = Paths.get(
            System.getenv("BENCH_FILE") ?: """C:\Users\Shridhar\Downloads\10-pg-blank.pdf""",
        )
        val jobs = (System.getenv("BENCH_JOBS") ?: "100").toInt()
        val concurrency = (System.getenv("BENCH_CONCURRENCY") ?: "4").toInt()

        if (!Files.exists(file)) {
            println("benchmark file not found: $file")
            return
        }

        val validated = validatePdf(file)
        val service = PrintServiceLookup.lookupPrintServices(null, null)
            .firstOrNull { it.name.contains("Print to PDF", ignoreCase = true) }
        if (service == null) {
            println("no 'Microsoft Print to PDF' on this machine - skipping")
            return
        }

        println("printer     : ${service.name}")
        println("document    : ${file.fileName} (${validated.pageCount} pages)")
        println("jobs        : $jobs at $concurrency concurrent")
        println()

        val options = PrintOptions(
            colorMode = ColorMode.BLACK_AND_WHITE,
            duplexMode = DuplexMode.SINGLE_SIDED,
            paperSize = PaperSize.A4,
            copies = 1,
            pageRange = null,
        )

        val pool = Executors.newFixedThreadPool(concurrency)
        val done = AtomicInteger()
        val failed = AtomicInteger()
        val latencies = java.util.Collections.synchronizedList(mutableListOf<Long>())
        val startedAt = System.nanoTime()

        repeat(jobs) {
            pool.submit {
                val nanos = measureNanoTime {
                    try {
                        val token = printPdf(service, validated.path, options, validated.pageCount)
                        val outcome = SpoolerOutcomePoller.pollJobOutcome(
                            printerName = service.name,
                            jobNameToken = token,
                            timeoutSeconds = 300.0,
                            pollIntervalSeconds = 0.5,
                        )
                        if (outcome.outcome != PrintOutcome.COMPLETED) failed.incrementAndGet()
                    } catch (exc: Exception) {
                        failed.incrementAndGet()
                    }
                }
                latencies.add(nanos / 1_000_000)
                val n = done.incrementAndGet()
                if (n % 10 == 0) {
                    val secs = (System.nanoTime() - startedAt) / 1e9
                    println("  %3d done  %6.1fs elapsed  %5.1f/min".format(n, secs, n * 60 / secs))
                }
            }
        }

        pool.shutdown()
        pool.awaitTermination(2, TimeUnit.HOURS)

        val seconds = (System.nanoTime() - startedAt) / 1e9
        val sorted = latencies.sorted()
        val pages = jobs * validated.pageCount

        println()
        println("--- $jobs jobs, ${validated.pageCount} pages each ---")
        println("total       : %.1fs (%.1f min)".format(seconds, seconds / 60))
        println("throughput  : %.1f jobs/min, %.0f pages/min".format(jobs * 60 / seconds, pages * 60 / seconds))
        println("per job     : %.2fs average".format(seconds / jobs))
        println("job latency : median %dms, slowest %dms".format(sorted[sorted.size / 2], sorted.last()))
        println("failed      : ${failed.get()}")
    }
}
