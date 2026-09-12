import org.jetbrains.kotlin.gradle.dsl.JvmTarget
import org.gradle.process.ExecOperations
import javax.inject.Inject

plugins {
    kotlin("jvm") version "2.2.20"
    application
    id("org.openjfx.javafxplugin") version "0.1.0"
}

group = "com.printly"
version = "0.1.0"

java {
    toolchain { languageVersion.set(JavaLanguageVersion.of(17)) }
}

repositories { mavenCentral() }

dependencies {
    // --- Coroutines (replaces Python's asyncio) ---
    implementation("org.jetbrains.kotlinx:kotlinx-coroutines-core:1.9.0")

    // --- HTTP + SSE to the Printly backend (replaces httpx) ---
    implementation("com.squareup.okhttp3:okhttp:4.12.0")
    implementation("com.squareup.okhttp3:okhttp-sse:4.12.0")

    // --- JSON - same library the backend itself uses, so DTO field-name
    // conventions read the same on both sides (replaces pydantic) ---
    implementation("com.fasterxml.jackson.core:jackson-databind:2.18.2")
    implementation("com.fasterxml.jackson.module:jackson-module-kotlin:2.18.2")
    implementation("com.fasterxml.jackson.datatype:jackson-datatype-jsr310:2.18.2")

    // --- PDF read/render (replaces pymupdf) - same library PrintlyRenderApi
    // itself uses for batch/separator PDFs ---
    implementation("org.apache.pdfbox:pdfbox:3.0.3")

    // --- Local durable state (replaces stdlib sqlite3) ---
    implementation("org.xerial:sqlite-jdbc:3.47.1.0")

    // --- Windows native interop: WinSpool job-status polling and Credential
    // Manager secret storage only - everything else goes through javax.print
    // and JVM standard APIs (replaces pywin32 + keyring) ---
    implementation("net.java.dev.jna:jna:5.15.0")
    implementation("net.java.dev.jna:jna-platform:5.15.0")

    // --- Desktop UI shell hosting the existing React webui/ unchanged
    // (replaces pywebview) ---
    implementation("org.openjfx:javafx-controls:21.0.5")
    implementation("org.openjfx:javafx-web:21.0.5")

    // --- Test ---
    testImplementation("org.jetbrains.kotlin:kotlin-test-junit5")
    testImplementation("org.junit.jupiter:junit-jupiter:5.11.3")
    testImplementation("io.mockk:mockk:1.13.13")
    testRuntimeOnly("org.junit.platform:junit-platform-launcher")
}

kotlin {
    compilerOptions {
        freeCompilerArgs.addAll("-Xjsr305=strict")
        jvmTarget.set(JvmTarget.JVM_17)
    }
}

application {
    mainClass.set("com.printly.agent.ui.PrintlyAgentAppKt")
}

javafx {
    version = "21.0.5"
    modules = listOf("javafx.controls", "javafx.web")
}

// jpackage - a self-contained Windows application (bundled JRE, no separate
// Java install needed on the shop PC), the "installable Windows application"
// shape the master prompt asks for. Invoked directly via ExecOperations
// (Gradle 9-compatible) rather than the org.beryx.runtime plugin, which
// still calls the removed Project.exec() API and fails outright under
// Gradle 9. Bundles the full local JDK as the runtime image rather than a
// jlink-trimmed one - larger output, but far more robust for an app that
// leans on reflection-heavy libraries (JNA, Jackson) a trimmed module set
// can easily break in subtle ways.
abstract class JPackageTask @Inject constructor(private val execOps: ExecOperations) : DefaultTask() {
    @get:InputDirectory abstract val inputDir: DirectoryProperty
    @get:Input abstract val mainJar: Property<String>
    @get:Input abstract val mainClass: Property<String>
    @get:Input abstract val appName: Property<String>
    @get:Input abstract val appVersionValue: Property<String>
    @get:Input abstract val installerType: Property<String> // "app-image" (no WiX needed) | "msi" | "exe" (both need WiX Toolset)
    @get:OutputDirectory abstract val outputDir: DirectoryProperty

    @TaskAction
    fun run() {
        outputDir.get().asFile.mkdirs()
        val javaHome = System.getProperty("java.home")
        val type = installerType.getOrElse("app-image")

        // jpackage refuses to write over what it made last time, and this task
        // never cleared up after itself - so every rebuild after the first
        // failed with "destination directory already exists" and left the
        // previous artifact sitting there looking current. A failed build that
        // leaves a plausible, months-old installer on disk is worse than one
        // that leaves nothing, because the MSI still installs.
        //
        // Only ever the artifacts this run is about to replace: the two
        // packaging tasks share an output directory, and clearing the whole
        // thing would mean building an MSI deleted the app image.
        val dest = outputDir.get().asFile
        val previous = when (type) {
            "app-image" -> dest.resolve(appName.get())
            else -> dest.resolve("${appName.get()}-${appVersionValue.get()}.$type")
        }
        if (previous.exists() && !previous.deleteRecursively()) {
            throw GradleException(
                "could not clear ${previous.absolutePath} - close the app if it is running, " +
                    "then build again",
            )
        }

        val args = mutableListOf(
            "--type", type,
            "--input", inputDir.get().asFile.absolutePath,
            "--dest", outputDir.get().asFile.absolutePath,
            "--name", appName.get(),
            "--app-version", appVersionValue.get(),
            "--main-jar", mainJar.get(),
            "--main-class", mainClass.get(),
            "--runtime-image", javaHome,
            "--vendor", "Printly",
            "--description", "Printly Print Agent - turns paid shop orders into physical prints",
        )

        // The shop PC's Start menu, taskbar and desktop shortcut all take this.
        // Without it jpackage falls back to the stock Java icon, which tells a
        // shopkeeper looking for "the printing app" nothing at all. Generated
        // by src/main/packaging/MakeIcon.java - see its comment for why the
        // mark is drawn rather than converted from the SVG.
        val icon = project.file("src/main/packaging/printly.ico")
        if (icon.exists()) {
            args += listOf("--icon", icon.absolutePath)
        } else {
            logger.warn("no icon at ${icon.path} - packaging with the default Java icon")
        }

        if (type != "app-image") {
            args += listOf("--win-menu", "--win-shortcut", "--win-dir-chooser")
        }

        execOps.exec {
            commandLine(listOf("$javaHome/bin/jpackage.exe") + args)
        }
    }
}

fun registerJPackageTask(name: String, type: String) = tasks.register<JPackageTask>(name) {
    dependsOn("installDist")
    group = "distribution"
    inputDir.set(layout.buildDirectory.dir("install/${project.name}/lib"))
    mainJar.set("${project.name}-${project.version}.jar")
    mainClass.set("com.printly.agent.ui.PrintlyAgentAppKt")
    appName.set("PrintlyAgent")
    appVersionValue.set(project.version.toString())
    installerType.set(type)
    outputDir.set(layout.buildDirectory.dir("jpackage"))
}

// No WiX Toolset required - a runnable folder bundling its own JRE.
registerJPackageTask("jpackageAppImage", "app-image")

// Requires WiX Toolset installed on the build machine (jpackage shells out
// to candle.exe/light.exe for both "msi" and "exe" on Windows) - fails with
// jpackage's own clear error if it isn't.
registerJPackageTask("jpackageMsi", "msi")

tasks.withType<Test> {
    useJUnitPlatform()
    testLogging { events("passed", "skipped", "failed") }
}

// ---------------------------------------------------------------------------
// The shop dashboard, built from source rather than copied in by hand.
//
// The dashboard is a separate repo (premsoni-0800/printlypartner) whose built
// `dist/` is served from inside this app. Vendoring that by hand is how a
// build quietly ships a months-old UI: nothing fails, the bundle is simply
// stale, and the only symptom is a fix that "didn't take".
//
// So the checkout is the source of truth when it is present, and the vendored
// copy under src/main/resources/dashboard is a build output. It stays in git
// because CI and anyone without the dashboard checked out still need to
// produce a working MSI - the task is skipped, with a warning, rather than
// failing the build.
//
// Point it elsewhere with -PdashboardDir=... or PRINTLY_DASHBOARD_DIR.
// ---------------------------------------------------------------------------

val dashboardDir: String = (project.findProperty("dashboardDir") as String?)
    ?: System.getenv("PRINTLY_DASHBOARD_DIR")
    ?: "../printlypartner-web"

val vendoredDashboard = layout.projectDirectory.dir("src/main/resources/dashboard")

abstract class BuildDashboardTask @Inject constructor(private val execOps: ExecOperations) : DefaultTask() {
    @get:Input abstract val sourceDir: Property<String>

    /**
     * The dashboard's own sources. Without these declared, the only input is
     * the *path* to the dashboard, which does not change when the dashboard
     * does - so Gradle called the task up-to-date and packaged whatever bundle
     * happened to be vendored, which is precisely the stale-MSI failure the
     * processResources hook below is supposed to rule out.
     *
     * Empty when the dashboard is not checked out. Deliberately not
     * @SkipWhenEmpty: the task itself handles that case, keeping the vendored
     * copy and saying so, and skipping it outright would lose the warning.
     */
    @get:InputFiles
    @get:PathSensitive(PathSensitivity.RELATIVE)
    abstract val sourceFiles: ConfigurableFileCollection

    @get:OutputDirectory abstract val outputDir: DirectoryProperty

    @TaskAction
    fun run() {
        val source = project.file(sourceDir.get())
        if (!source.resolve("package.json").exists()) {
            logger.warn(
                "dashboard source not found at ${source.absolutePath} - keeping the vendored bundle as-is. " +
                    "Set -PdashboardDir=<path> to rebuild it from source.",
            )
            return
        }

        // npm is a .cmd on Windows and has no extensionless sibling, so it
        // cannot be exec'd directly the way it can elsewhere.
        val npm = if (System.getProperty("os.name").startsWith("Windows", true)) "npm.cmd" else "npm"
        if (!source.resolve("node_modules").exists()) {
            execOps.exec { commandLine(npm, "ci"); workingDir = source }
        }
        execOps.exec { commandLine(npm, "run", "build"); workingDir = source }

        val dist = source.resolve("dist")
        if (!dist.exists()) throw GradleException("dashboard build produced no dist/ at ${dist.absolutePath}")

        val target = outputDir.get().asFile
        target.deleteRecursively()
        target.mkdirs()
        dist.copyRecursively(target, overwrite = true)
        logger.lifecycle("dashboard rebuilt from ${source.absolutePath}")
    }
}

val buildDashboard = tasks.register<BuildDashboardTask>("buildDashboard") {
    group = "build"
    description = "Builds the shop dashboard and vendors it into src/main/resources/dashboard"
    sourceDir.set(dashboardDir)
    // Everything npm actually reads to produce the bundle. node_modules and
    // dist are excluded because they are inputs to nothing and would make
    // fingerprinting cost more than the build being fingerprinted.
    sourceFiles.from(
        fileTree(dashboardDir) {
            include("src/**", "public/**", "index.html", "package.json", "package-lock.json")
            include("vite.config.*", "tailwind.config.*", "postcss.config.*")
        },
    )
    outputDir.set(vendoredDashboard)
}

// Anything that packages the app gets the current dashboard, so an MSI can
// never ship a bundle older than the checkout it was built from.
tasks.named("processResources") { dependsOn(buildDashboard) }
