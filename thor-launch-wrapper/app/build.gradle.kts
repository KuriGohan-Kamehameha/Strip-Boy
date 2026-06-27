plugins {
    alias(libs.plugins.android.application)
    alias(libs.plugins.kotlin.android)
}

android {
    namespace = "com.moonbench.thorlaunch"
    compileSdk {
        version = release(36)
    }

    defaultConfig {
        applicationId = "com.moonbench.thorlaunch"
        minSdk = 33
        targetSdk = 36
        versionCode = 1
        versionName = "1.0"

        // The bundled NativeAOT companion patcher is arm64-only (AYN Thor).
        ndk {
            abiFilters += "arm64-v8a"
        }
    }

    buildTypes {
        release {
            isMinifyEnabled = false
            isShrinkResources = false
        }
    }

    // Ship libpipboy-patcher.so uncompressed + extracted to nativeLibraryDir so
    // it can be exec'd on-device (API 33 blocks exec from writable app dirs).
    packaging {
        jniLibs {
            useLegacyPackaging = true
        }
    }

    compileOptions {
        sourceCompatibility = JavaVersion.VERSION_11
        targetCompatibility = JavaVersion.VERSION_11
    }
    kotlinOptions {
        jvmTarget = "11"
    }
}

// Dedicated classpath for the build-time smali assembler (helperDex task). Kept
// out of `implementation` so smali + its deps never ship inside the APK.
val smaliTool: Configuration by configurations.creating

dependencies {
    implementation(libs.androidx.core.ktx)
    implementation(libs.androidx.appcompat)

    // Stage 4 (binary AndroidManifest edit) + Stage 5 (sign + align) — bundled.
    implementation(libs.arsclib)
    implementation(libs.apksig)

    smaliTool(libs.smali)

    testImplementation(libs.junit)
}

// Pass -Dharness.* system properties through to the unit-test JVM so the
// off-device stage 3-5 validation harness can locate its inputs.
tasks.withType<Test>().configureEach {
    listOf(
        "harness.run", "harness.inputApk", "harness.patchedDll", "harness.helperDex",
        "harness.keystore", "harness.storePass", "harness.alias", "harness.keyPass",
        "harness.outApk"
    ).forEach { key ->
        System.getProperty(key)?.let { systemProperty(key, it) }
    }
}

// ---------------------------------------------------------------------------
// Stage-3 prebuild: assemble the three io.pipboy.thor.* helper smali sources
// into a single helper.dex shipped as an app asset. These are Kuri's own
// classes (no Bethesda bytes); on-device they are injected as the next free
// classesN.dex. Build-time only — smali never ships in the APK.
// ---------------------------------------------------------------------------
val smaliSrcDir = rootProject.file("../patcher/smali/io/pipboy/thor")

abstract class BuildHelperDexTask : JavaExec() {
    @get:org.gradle.api.tasks.InputFiles
    abstract val smaliSources: ConfigurableFileCollection

    @get:org.gradle.api.tasks.OutputDirectory
    abstract val outputDir: DirectoryProperty

    @org.gradle.api.tasks.TaskAction
    override fun exec() {
        val sources = smaliSources.files.sortedBy { it.name }
        sources.forEach { check(it.isFile) { "missing smali source: ${it.absolutePath}" } }
        val outDex = outputDir.get().asFile.also { it.mkdirs() }.resolve("helper.dex")
        mainClass.set("com.android.tools.smali.smali.Main")
        args = buildList {
            add("assemble")
            // --api 23 (the companion's targetSdk) emits the baseline dex 035.
            // Higher values made smali emit dex 040 (Android 14 / API 34), which
            // ART on the API-33 Thor refuses to load → the io.pipboy.thor.* helper
            // classes fail at runtime even though install/manifest resolve fine.
            add("--api"); add("23")
            add("--output"); add(outDex.absolutePath)
            sources.forEach { add(it.absolutePath) }
        }
        super.exec()
        check(outDex.isFile && outDex.length() > 0) { "helper.dex was not produced at ${outDex.absolutePath}" }
    }
}

// Stage-3 prebuild: assemble io.pipboy.thor.* smali into assets/helper.dex.
// These are Kuri's own classes (no Bethesda bytes); on-device they are injected
// as the next free classesN.dex. smali runs build-time only — it never ships.
val buildHelperDex by tasks.registering(BuildHelperDexTask::class) {
    group = "build"
    description = "Assemble io.pipboy.thor.* smali into helper.dex (stage-3 asset)."
    classpath = smaliTool
    smaliSources.from(
        listOf("LauncherActivity.smali", "LEDBridge.smali", "LEDClear.smali")
            .map { File(smaliSrcDir, it) }
    )
    outputDir.set(layout.buildDirectory.dir("generated/helper-dex"))
}

// Surface the generated dex to the asset merger as assets/helper.dex.
androidComponents {
    onVariants { variant ->
        variant.sources.assets?.addGeneratedSourceDirectory(
            buildHelperDex,
            wiredWith = BuildHelperDexTask::outputDir
        )
    }
}
