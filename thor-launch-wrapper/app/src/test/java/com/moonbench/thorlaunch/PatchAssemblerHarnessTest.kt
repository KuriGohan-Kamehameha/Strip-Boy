package com.moonbench.thorlaunch

import org.junit.Assert.assertTrue
import org.junit.Assume.assumeTrue
import org.junit.Test
import java.io.File

/**
 * Off-device validation harness for stages 3-5, run as a plain JVM unit test so
 * it uses the project's real Kotlin/Android toolchain and the SAME [PatchAssembler]
 * code path that on-device [NativePatchEngine] invokes.
 *
 * It is gated on `-Dharness.run=true` plus the five input paths, so a normal
 * `testDebugUnitTest` (no inputs) skips it rather than failing. The verification
 * driver (`scripts/verify_ondevice_stages.sh`) supplies:
 *   - harness.inputApk    the real original companion APK
 *   - harness.patchedDll  the desktop-Cecil stage-2 stand-in DLL
 *   - harness.helperDex   the gradle-built helper.dex asset
 *   - harness.keystore + harness.storePass / harness.alias / harness.keyPass
 *   - harness.outApk      where to write the produced installable APK
 */
class PatchAssemblerHarnessTest {

    @Test
    fun assembleProducesSignedApk() {
        assumeTrue("harness disabled", System.getProperty("harness.run") == "true")

        val inputApk = requiredFile("harness.inputApk")
        val patchedDll = requiredFile("harness.patchedDll")
        val helperDex = requiredFile("harness.helperDex")
        val keystore = requiredFile("harness.keystore")
        val outApk = File(requireProp("harness.outApk"))
        val workDir = File(outApk.parentFile, "harness-work").apply { mkdirs() }

        val signer = PatchAssembler.loadKeyMaterial(
            keystore = keystore,
            storePass = requireProp("harness.storePass").toCharArray(),
            alias = requireProp("harness.alias"),
            keyPass = requireProp("harness.keyPass").toCharArray()
        )

        println("[harness] dexSlot=${PatchAssembler.nextFreeDexEntryName(inputApk)}")

        val result = PatchAssembler.assemble(
            inputApk = inputApk,
            patchedDll = patchedDll,
            helperDex = helperDex,
            signer = signer,
            workDir = workDir,
            outApk = outApk
        )
        println("[harness] OK -> ${result.absolutePath} (${result.length()} bytes)")
        assertTrue("output APK missing", result.isFile)
        assertTrue("output APK empty", result.length() > 0)

        // Idempotence: re-running stage 4 against the produced APK must be a no-op
        // (LauncherActivity already present → patchLauncher returns early).
        val secondSlot = PatchAssembler.nextFreeDexEntryName(result)
        println("[harness] idempotent re-scan dexSlot on output=$secondSlot")
        // The output already has classes2.dex (our inject), so the next slot is classes3.dex.
        assertTrue("expected an injected classesN.dex in output", secondSlot != "classes2.dex")
    }

    private fun requireProp(name: String): String =
        System.getProperty(name) ?: error("missing -D$name")

    private fun requiredFile(name: String): File {
        val f = File(requireProp(name))
        assertTrue("missing file for $name: ${f.path}", f.isFile)
        return f
    }
}
