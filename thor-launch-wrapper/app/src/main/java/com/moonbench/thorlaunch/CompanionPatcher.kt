package com.moonbench.thorlaunch

import android.content.ComponentName
import android.content.Context
import android.content.Intent
import android.content.pm.PackageManager
import android.net.Uri
import android.util.Log
import androidx.core.content.FileProvider
import java.io.File
import java.security.MessageDigest

/**
 * Companion-app patch orchestration for the Thor launcher.
 *
 * The launcher historically asked one question — "is a *patched* companion
 * installed?" — and blocked otherwise. This module adds the missing half:
 * classify the companion as [CompanionState.NOT_INSTALLED] /
 * [CompanionState.UNPATCHED] / [CompanionState.PATCHED], and when it is
 * UNPATCHED drive the patch flow: locate the installed base APK, verify it is
 * the known v1.2 baseline, hand it to a [PatchEngine], then install the result.
 *
 * The IL-patch + re-sign step lives behind [PatchEngine] on purpose. The
 * Strip-Boy patch is a Mono.Cecil (.NET IL) rewrite of `Assembly-CSharp.dll`
 * plus smali/manifest edits, zipalign, and apksigner — desktop tooling that
 * does not run on Android, and the project's clean-room stance rules out the
 * usual shortcuts (shipping a Bethesda-derived binary diff, or uploading the
 * user's APK to a remote patch service). No on-device engine ships yet, so the
 * default [UnavailablePatchEngine] reports the gap honestly instead of faking a
 * patch. See `docs/THOR_AUTO_PATCH.md` for the engine options and decision.
 */
enum class CompanionState { NOT_INSTALLED, UNPATCHED, PATCHED }

/** Result of a [PatchEngine.patch] attempt. */
sealed class PatchOutcome {
    /** A signed, installable patched APK was produced at [patchedApk]. */
    data class Success(val patchedApk: File) : PatchOutcome()

    /** No engine can run here (e.g. no on-device toolchain); [reason] is user-facing. */
    data class Unsupported(val reason: String) : PatchOutcome()

    /** An engine ran but failed; [reason] is user-facing. */
    data class Failed(val reason: String) : PatchOutcome()
}

/** Outcome of comparing a candidate APK against the known v1.2 baseline. */
data class BaselineCheck(
    val matches: Boolean,
    val sha256: String,
    val sizeBytes: Long
)

/**
 * Transforms an original companion APK into an installable patched APK.
 * Implementations own the IL rewrite, smali/manifest edits, zipalign, and
 * signing. Kept as an interface so an on-device engine can drop in without the
 * orchestrator (acquire / verify / install) changing.
 */
interface PatchEngine {
    fun patch(context: Context, input: File, workDir: File): PatchOutcome
}

/**
 * Default engine: no on-device patch toolchain is bundled, so this never
 * fabricates a patch — it returns [PatchOutcome.Unsupported] and the caller
 * falls back to PC-build guidance.
 */
class UnavailablePatchEngine : PatchEngine {
    override fun patch(context: Context, input: File, workDir: File): PatchOutcome =
        PatchOutcome.Unsupported(
            "On-device patching is not wired in this build. The Strip-Boy patch " +
                "is a .NET IL + smali rewrite that needs desktop tooling. Build " +
                "the patched APK on a PC with scripts/build.sh, then sideload it."
        )
}

object CompanionPatcher {
    private const val TAG = "ThorCompanionPatcher"

    /** Known-good Bethesda Pip-Boy v1.2 baseline (see `docs/CHECKSUMS.md`). */
    const val BASELINE_SHA256 =
        "974b8833af43def6640a4490a51f809cf1488244ca885df4c1f5632a145a91ce"
    const val BASELINE_SIZE_BYTES = 39_591_950L

    private const val SHA_BUFFER_BYTES = 1 shl 16            // 64 KiB read window
    private const val MAX_SHA_CHUNKS = 8192                  // bound the hash loop (≤512 MiB)
    private const val APK_MIME = "application/vnd.android.package-archive"
    private const val FILEPROVIDER_SUFFIX = ".fileprovider"

    /** Classify the companion package by presence and by the patch marker activity. */
    fun detect(
        pm: PackageManager,
        companionPackage: String,
        markerActivity: String
    ): CompanionState {
        require(companionPackage.isNotBlank()) { "companionPackage must not be blank" }
        if (!isInstalled(pm, companionPackage)) return CompanionState.NOT_INSTALLED
        return if (hasMarkerActivity(pm, companionPackage, markerActivity)) {
            CompanionState.PATCHED
        } else {
            CompanionState.UNPATCHED
        }
    }

    /** Resolve the installed companion's base APK on disk via its applicationInfo. */
    fun locateInstalledApk(pm: PackageManager, companionPackage: String): File? = runCatching {
        val sourceDir = pm.getApplicationInfo(companionPackage, 0).sourceDir
        File(sourceDir).takeIf { it.isFile && it.canRead() }
    }.onFailure { Log.w(TAG, "locateInstalledApk failed for $companionPackage", it) }
        .getOrNull()

    /** SHA-256 + size check of [apk] against the known v1.2 baseline. */
    fun verifyBaseline(apk: File): BaselineCheck {
        val size = apk.length()
        val sha = sha256(apk)
        val matches = sha.equals(BASELINE_SHA256, ignoreCase = true) &&
            size == BASELINE_SIZE_BYTES
        return BaselineCheck(matches = matches, sha256 = sha, sizeBytes = size)
    }

    /**
     * Launch the system package installer for a produced patched APK. Requires
     * the REQUEST_INSTALL_PACKAGES permission and a configured FileProvider.
     * Returns false if the installer could not be started.
     */
    fun installPatchedApk(context: Context, apk: File): Boolean = runCatching {
        require(apk.isFile) { "patched APK missing: ${apk.path}" }
        val uri: Uri = FileProvider.getUriForFile(
            context,
            context.packageName + FILEPROVIDER_SUFFIX,
            apk
        )
        val intent = Intent(Intent.ACTION_VIEW).apply {
            setDataAndType(uri, APK_MIME)
            addFlags(Intent.FLAG_GRANT_READ_URI_PERMISSION or Intent.FLAG_ACTIVITY_NEW_TASK)
        }
        context.startActivity(intent)
        true
    }.onFailure { Log.w(TAG, "installPatchedApk failed", it) }
        .getOrDefault(false)

    /** Whether this app is allowed to request package installs (Settings toggle). */
    fun canRequestInstall(context: Context): Boolean =
        runCatching { context.packageManager.canRequestPackageInstalls() }
            .getOrDefault(false)

    private fun isInstalled(pm: PackageManager, pkg: String): Boolean = runCatching {
        pm.getPackageInfo(pkg, 0)
        true
    }.getOrDefault(false)

    private fun hasMarkerActivity(pm: PackageManager, pkg: String, activity: String): Boolean =
        runCatching {
            pm.getActivityInfo(ComponentName(pkg, activity), 0)
            true
        }.getOrDefault(false)

    private fun sha256(file: File): String {
        val digest = MessageDigest.getInstance("SHA-256")
        file.inputStream().use { stream ->
            val buffer = ByteArray(SHA_BUFFER_BYTES)
            var chunks = 0
            while (chunks < MAX_SHA_CHUNKS) {
                val read = stream.read(buffer)
                if (read < 0) break
                digest.update(buffer, 0, read)
                chunks++
            }
        }
        return digest.digest().joinToString("") { "%02x".format(it) }
    }
}
