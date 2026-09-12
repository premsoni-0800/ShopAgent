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
