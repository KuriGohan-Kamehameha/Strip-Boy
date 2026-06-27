package com.moonbench.thorlaunch

import android.content.Context
import android.util.Log
import java.io.File
import java.util.concurrent.TimeUnit
import java.util.zip.ZipFile
import javax.security.auth.x500.X500Principal

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
 * All six stages are wired:
 *  1. extract `Assembly-CSharp.dll` from the companion APK (java.util.zip);
 *  2. IL patch — exec the bundled `libpipboy-patcher.so` (device-gated);
 *  3. inject the prebuilt `helper.dex` asset as the next free `classesN.dex`;
 *  4. edit the binary `AndroidManifest.xml` (ARSCLib) per `patch_manifest.py`;
 *  5. repackage + sign + align (apksig) with an AndroidKeyStore key.
 *
 * Stages 3-5 live in [PatchAssembler] (pure JVM, no Android types) so the same
 * code is validated off-device by `scripts/verify_ondevice_stages.sh`. `patch()`
 * returns [PatchOutcome.Success] with the signed APK when all stages pass, and
 * [PatchOutcome.Failed]/[PatchOutcome.Unsupported] (never a half-built APK) on
 * any failure. See `docs/THOR_AUTO_PATCH.md`.
 */
class NativePatchEngine : PatchEngine {
    override fun patch(context: Context, input: File, workDir: File): PatchOutcome {
        val binary = nativePatcherBinary(context)
            ?: return PatchOutcome.Unsupported(
                "Native patcher ($NATIVE_PATCHER_SO) is not in this build's arm64 " +
                    "native libs. Rebuild the wrapper with the bundled binary."
            )
        // Extract the WHOLE Managed/ dir, not just Assembly-CSharp.dll: the Cecil
        // patcher resolves referenced assemblies (mscorlib, System, UnityEngine…)
        // from the input DLL's directory. The desktop/harness masked this by
        // resolving mscorlib from the host .NET install; the self-contained
        // Android binary has no host runtime, so the siblings must be present or
        // patches that touch mscorlib types (e.g. AutoPickFullscreenMode) fail.
        val managedDir = File(workDir, "managed")
        if (!extractManagedDlls(input, managedDir)) {
            return PatchOutcome.Failed(
                "Could not extract $MANAGED_DIR*.dll from the companion APK."
            )
        }
        val originalDll = File(managedDir, "Assembly-CSharp.dll")
        val patchedDll = File(managedDir, "Assembly-CSharp.patched.dll")
        val ilResult = runNativePatcher(binary, originalDll, patchedDll)
        if (ilResult !is PatchOutcome.Success) {
            return ilResult
        }

        // Stage 3: stage the prebuilt helper dex asset to disk for injection.
        val helperDex = File(workDir, HELPER_DEX_NAME)
        if (!copyAsset(context, HELPER_DEX_ASSET, helperDex)) {
            return PatchOutcome.Failed(
                "Helper dex asset ($HELPER_DEX_ASSET) is missing from this build. " +
                    "Rebuild the wrapper (the helperDex gradle task generates it)."
            )
        }

        // Stage 5 prerequisite: a signing key in app storage (generated once).
        val signer = runCatching { obtainSigner(context) }
            .getOrElse { return PatchOutcome.Failed("Could not obtain signing key: ${it.message}") }

        // Stages 3-5: inject dex, edit manifest (AXML), repackage, sign + align.
        val outApk = File(workDir, OUTPUT_APK_NAME)
        return runCatching {
            PatchAssembler.assemble(
                inputApk = input,
                patchedDll = patchedDll,
                helperDex = helperDex,
                signer = signer,
                workDir = workDir,
                outApk = outApk
            )
            Log.i(TAG, "patched APK assembled at ${outApk.absolutePath} (${outApk.length()} bytes)")
            PatchOutcome.Success(outApk) as PatchOutcome
        }.getOrElse { error ->
            PatchOutcome.Failed("Stage 3-5 assembly failed: ${error.message}")
        }
    }

    private fun copyAsset(context: Context, assetName: String, dest: File): Boolean = runCatching {
        dest.parentFile?.mkdirs()
        context.assets.open(assetName).use { input ->
            dest.outputStream().use { output -> input.copyTo(output) }
        }
        dest.isFile && dest.length() > 0
    }.getOrElse {
        Log.w(TAG, "copyAsset $assetName failed", it)
        false
    }

    /**
     * Obtain the signing key from the AndroidKeyStore, generating a self-signed
     * RSA key (with its auto-generated X.509 cert) on first use. The key is
     * hardware/OS-backed and never leaves the device; it only re-signs the user's
     * own companion APK (clean-room: nothing Bethesda-derived is created or moved).
     *
     * AndroidKeyStore keys are non-exportable, but apksig signs through the
     * [java.security.PrivateKey]'s `Signature` provider, so the key never has to
     * be extracted.
     */
    private fun obtainSigner(context: Context): PatchAssembler.KeyMaterial {
        val ks = java.security.KeyStore.getInstance(ANDROID_KEYSTORE).apply { load(null) }
        if (!ks.containsAlias(KEYSTORE_ALIAS)) {
            generateAndroidKeystoreKey()
            ks.load(null)
        }
        val key = ks.getKey(KEYSTORE_ALIAS, null) as? java.security.PrivateKey
            ?: throw IllegalStateException("AndroidKeyStore has no private key for $KEYSTORE_ALIAS")
        val chain = ks.getCertificateChain(KEYSTORE_ALIAS)
            ?: throw IllegalStateException("AndroidKeyStore has no cert chain for $KEYSTORE_ALIAS")
        val x509 = chain.mapNotNull { it as? java.security.cert.X509Certificate }
        if (x509.isEmpty()) throw IllegalStateException("cert chain for $KEYSTORE_ALIAS is not X.509")
        return PatchAssembler.KeyMaterial(KEYSTORE_ALIAS, key, x509)
    }

    private fun generateAndroidKeystoreKey() {
        val notBefore = java.util.Date()
        val notAfter = java.util.Date(notBefore.time + CERT_VALIDITY_MS)
        val spec = android.security.keystore.KeyGenParameterSpec.Builder(
            KEYSTORE_ALIAS,
            android.security.keystore.KeyProperties.PURPOSE_SIGN
        )
            .setKeySize(RSA_KEY_BITS)
            // SHA-1 is required for the v1 (JAR) signature the original companion's
            // minSdk 14 mandates; SHA-256/512 cover the v2/v3 schemes.
            .setDigests(
                android.security.keystore.KeyProperties.DIGEST_SHA1,
                android.security.keystore.KeyProperties.DIGEST_SHA256,
                android.security.keystore.KeyProperties.DIGEST_SHA512
            )
            .setSignaturePaddings(android.security.keystore.KeyProperties.SIGNATURE_PADDING_RSA_PKCS1)
            .setCertificateSubject(X500Principal(KEYSTORE_DNAME))
            .setCertificateSerialNumber(java.math.BigInteger.ONE)
            .setCertificateNotBefore(notBefore)
            .setCertificateNotAfter(notAfter)
            .build()
        val generator = java.security.KeyPairGenerator.getInstance(
            android.security.keystore.KeyProperties.KEY_ALGORITHM_RSA,
            ANDROID_KEYSTORE
        )
        generator.initialize(spec)
        generator.generateKeyPair()
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

    /**
     * Extract every `.dll` under `assets/bin/Data/Managed/` into [destDir] (flat,
     * by basename) so the Cecil patcher's assembly resolver finds Assembly-CSharp's
     * sibling references (mscorlib, System, UnityEngine...). Returns true only if
     * Assembly-CSharp.dll was present and extracted.
     */
    private fun extractManagedDlls(apk: File, destDir: File): Boolean = runCatching {
        destDir.mkdirs()
        var sawMain = false
        ZipFile(apk).use { zip ->
            val entries = zip.entries()
            var count = 0
            while (entries.hasMoreElements() && count < MAX_ZIP_ENTRIES) {
                count++
                val entry = entries.nextElement()
                val name = entry.name
                if (entry.isDirectory || !name.startsWith(MANAGED_DIR) || !name.endsWith(".dll")) {
                    continue
                }
                val dest = File(destDir, name.substringAfterLast('/'))
                zip.getInputStream(entry).use { input ->
                    dest.outputStream().use { output -> input.copyTo(output) }
                }
                if (dest.name == MANAGED_DLL_NAME) sawMain = dest.isFile && dest.length() > 0
            }
        }
        sawMain
    }.getOrElse {
        Log.w(TAG, "extractManagedDlls failed", it)
        false
    }

    private companion object {
        const val TAG = "ThorNativePatchEngine"
        const val NATIVE_PATCHER_SO = "libpipboy-patcher.so"
        const val MANAGED_DIR = "assets/bin/Data/Managed/"
        const val MANAGED_DLL_NAME = "Assembly-CSharp.dll"
        const val MAX_ZIP_ENTRIES = 100_000
        const val PATCH_TIMEOUT_SECONDS = 120L
        const val LOG_OUTPUT_CHARS = 400

        const val HELPER_DEX_ASSET = "helper.dex"
        const val HELPER_DEX_NAME = "helper.dex"
        const val OUTPUT_APK_NAME = "pipboy-patched.apk"

        const val ANDROID_KEYSTORE = "AndroidKeyStore"
        const val KEYSTORE_ALIAS = "thorlaunch-companion-signer"
        const val KEYSTORE_DNAME = "CN=Strip-Boy, OU=local, O=local, C=US"
        const val RSA_KEY_BITS = 2048
        const val CERT_VALIDITY_MS = 30L * 365L * 24L * 60L * 60L * 1000L // ~30 years
    }
}
