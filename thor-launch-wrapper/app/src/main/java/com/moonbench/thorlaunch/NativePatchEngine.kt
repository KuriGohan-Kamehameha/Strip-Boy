package com.moonbench.thorlaunch

import android.content.Context
import android.util.Log
import java.io.File
import java.util.concurrent.TimeUnit
import java.util.zip.ZipFile

/**
 * Option-A engine: runs the bundled NativeAOT Cecil patcher on-device to do the
 * IL stage of the Strip-Boy patch, then (staged) smali inject + manifest edit +
 * sign.
 *
 * The patcher is a `linux-bionic-arm64` native executable cross-built from
 * `patcher/Patcher.csproj` and shipped as `arm64-v8a/libpipboy-patcher.so`.
 * Android API 33 only permits executing binaries from `nativeLibraryDir`
 * (exec from writable app dirs is blocked), hence the `lib*.so` packaging +
 * `useLegacyPackaging` so it is extracted there.
 *
 * Status: the **IL stage** (extract `Assembly-CSharp.dll`, run the native
 * patcher, get the patched DLL) is implemented. The remaining stages — smali
 * helper inject (dexlib2 + a prebuilt helper dex), manifest intent-filter move
 * (AXML), repackage, and zipalign + apksig signing — are not wired yet. Until
 * they land, `patch()` returns [PatchOutcome.Unsupported] after the IL stage
 * rather than a half-built APK. See `docs/THOR_AUTO_PATCH.md`.
 */
class NativePatchEngine : PatchEngine {
    override fun patch(context: Context, input: File, workDir: File): PatchOutcome {
        val binary = nativePatcherBinary(context)
            ?: return PatchOutcome.Unsupported(
                "Native patcher ($NATIVE_PATCHER_SO) is not in this build's arm64 " +
                    "native libs. Rebuild the wrapper with the bundled binary."
            )
        val originalDll = File(workDir, "Assembly-CSharp.original.dll")
        val patchedDll = File(workDir, "Assembly-CSharp.patched.dll")
        if (!extractEntry(input, MANAGED_DLL_ENTRY, originalDll)) {
            return PatchOutcome.Failed(
                "Could not extract $MANAGED_DLL_ENTRY from the companion APK."
            )
        }
        val ilResult = runNativePatcher(binary, originalDll, patchedDll)
        if (ilResult !is PatchOutcome.Success) {
            return ilResult
        }

        // Stages 2-4 pending: smali helper inject (dexlib2 + prebuilt helper
        // dex), manifest intent-filter move (AXML), repackage, zipalign + sign
        // (apksig). Until those land we do not emit an installable APK.
        return PatchOutcome.Unsupported(
            "IL stage complete (Assembly-CSharp.dll patched on-device). Remaining " +
                "stages — smali inject, manifest edit, sign — are not wired yet; " +
                "build the installable APK on a PC with scripts/build.sh for now."
        )
    }

    private fun runNativePatcher(binary: File, inDll: File, outDll: File): PatchOutcome =
        runCatching {
            val process = ProcessBuilder(
                binary.absolutePath,
                inDll.absolutePath,
                outDll.absolutePath
            ).redirectErrorStream(true).start()
            val output = process.inputStream.bufferedReader().use { it.readText() }
            val finished = process.waitFor(PATCH_TIMEOUT_SECONDS, TimeUnit.SECONDS)
            if (!finished) {
                process.destroyForcibly()
                return PatchOutcome.Failed("Native patcher timed out after ${PATCH_TIMEOUT_SECONDS}s.")
            }
            val code = process.exitValue()
            Log.i(TAG, "native patcher exit=$code out=${output.take(LOG_OUTPUT_CHARS)}")
            if (code == 0 && outDll.isFile && outDll.length() > 0) {
                PatchOutcome.Success(outDll)
            } else {
                PatchOutcome.Failed(
                    "Native patcher failed (exit=$code): ${output.take(LOG_OUTPUT_CHARS)}"
                )
            }
        }.getOrElse { PatchOutcome.Failed("Native patcher could not start: ${it.message}") }

    private fun nativePatcherBinary(context: Context): File? {
        val dir = context.applicationInfo.nativeLibraryDir ?: return null
        return File(dir, NATIVE_PATCHER_SO).takeIf { it.isFile && it.canExecute() }
    }

    private fun extractEntry(apk: File, entryName: String, dest: File): Boolean = runCatching {
        ZipFile(apk).use { zip ->
            val entry = zip.getEntry(entryName) ?: return false
            dest.parentFile?.mkdirs()
            zip.getInputStream(entry).use { input ->
                dest.outputStream().use { output -> input.copyTo(output) }
            }
        }
        dest.isFile && dest.length() > 0
    }.getOrDefault(false)

    private companion object {
        const val TAG = "ThorNativePatchEngine"
        const val NATIVE_PATCHER_SO = "libpipboy-patcher.so"
        const val MANAGED_DLL_ENTRY = "assets/bin/Data/Managed/Assembly-CSharp.dll"
        const val PATCH_TIMEOUT_SECONDS = 120L
        const val LOG_OUTPUT_CHARS = 400
    }
}
