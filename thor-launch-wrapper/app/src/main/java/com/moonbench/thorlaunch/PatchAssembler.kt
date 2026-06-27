package com.moonbench.thorlaunch

import com.android.apksig.ApkSigner
import com.reandroid.app.AndroidManifest
import com.reandroid.arsc.chunk.xml.AndroidManifestBlock
import com.reandroid.arsc.chunk.xml.ResXmlElement
import com.reandroid.arsc.value.ValueType
import java.io.File
import java.io.FileOutputStream
import java.security.KeyStore
import java.security.PrivateKey
import java.security.cert.X509Certificate
import java.util.zip.CRC32
import java.util.zip.Deflater
import java.util.zip.ZipEntry
import java.util.zip.ZipFile
import java.util.zip.ZipOutputStream

/**
 * Stages 3-5 of the Option-A on-device companion patch — pure JVM, no Android
 * framework types, so the same code runs on-device (called by [NativePatchEngine])
 * and off-device in the validation harness.
 *
 * - Stage 3: inject a prebuilt `helper.dex` (Kuri's `io.pipboy.thor.*` classes —
 *   no Bethesda bytes) as the next free `classesN.dex`. ART on API 33 loads all
 *   `classesN.dex`; the original `classes.dex` is left untouched.
 * - Stage 4: edit the binary `AndroidManifest.xml` per `scripts/patch_manifest.py`
 *   using ARSCLib (resource references resolved against the APK's `resources.arsc`).
 * - Stage 5: repackage the zip (patched DLL + injected dex + edited manifest, old
 *   v1 signature files dropped) and sign + align with apksig.
 *
 * Every stage is idempotent: re-running against an already-patched APK is a no-op.
 */
object PatchAssembler {

    /** A patch stage produced an unrecoverable error; [message] is user-facing. */
    class PatchStageException(message: String) : Exception(message)

    const val MANAGED_DLL_ENTRY = "assets/bin/Data/Managed/Assembly-CSharp.dll"
    const val MANIFEST_ENTRY = "AndroidManifest.xml"
    const val LAUNCHER_FQN = "io.pipboy.thor.LauncherActivity"
    const val UNITY_FQN = "com.unity3d.player.UnityPlayerNativeActivity"
    const val BIFROST_PERMISSION = "com.moonbench.bifrost.permission.CONTROL_LEDS"
    const val BIFROST_PACKAGE = "com.moonbench.bifrost"

    private const val THEME_NO_DISPLAY = 0x01030055    // @android:style/Theme.NoDisplay
    private const val MAX_DEX_SLOTS = 999              // bound the classesN scan
    private const val ZIP_BUFFER = 1 shl 16            // 64 KiB copy window
    private const val MAX_ZIP_ENTRIES = 100_000        // bound the repackage loop

    private const val ACTION_MAIN = "android.intent.action.MAIN"
    private const val CAT_LAUNCHER = "android.intent.category.LAUNCHER"
    private const val CAT_LEANBACK = "android.intent.category.LEANBACK_LAUNCHER"

    /**
     * Run stages 3-5 against [inputApk], swapping in [patchedDll] (stage-2 output)
     * and [helperDex] (the prebuilt asset), signing with [signer], writing the
     * installable APK to [outApk]. Returns [outApk] on success.
     *
     * @throws PatchStageException on any stage failure (caller maps to PatchOutcome).
     */
    fun assemble(
        inputApk: File,
        patchedDll: File,
        helperDex: File,
        signer: KeyMaterial,
        workDir: File,
        outApk: File
    ): File {
        require(inputApk.isFile) { "input APK missing: ${inputApk.path}" }
        require(patchedDll.isFile && patchedDll.length() > 0) { "patched DLL missing: ${patchedDll.path}" }
        require(helperDex.isFile && helperDex.length() > 0) { "helper dex missing: ${helperDex.path}" }
        check(workDir.isDirectory || workDir.mkdirs()) { "could not create workDir: ${workDir.path}" }

        // Stage 4 first (pure in-memory edit) so the repackage writes it directly.
        val editedManifest = editManifest(inputApk)

        // Stage 3: choose the next free classesN.dex slot from the input entries.
        val dexEntryName = nextFreeDexEntryName(inputApk)

        // Stage 5a: repackage zip with patched DLL + edited manifest + helper dex.
        val unsigned = File(workDir, "companion-unsigned.apk")
        repackage(inputApk, patchedDll, editedManifest, helperDex, dexEntryName, unsigned)

        // Stage 5b: sign + align.
        signApk(unsigned, outApk, signer)
        check(outApk.isFile && outApk.length() > 0) { "signed APK was not produced" }
        return outApk
    }

    // ---- Stage 3 helpers ---------------------------------------------------

    /**
     * Pick the next free `classesN.dex` name. If a helper dex with our classes is
     * already present (idempotent re-run), reuse that exact slot so we overwrite it.
     */
    fun nextFreeDexEntryName(apk: File): String {
        val existing = HashSet<String>()
        ZipFile(apk).use { zip ->
            val entries = zip.entries()
            var count = 0
            while (entries.hasMoreElements() && count < MAX_ZIP_ENTRIES) {
                existing.add(entries.nextElement().name)
                count++
            }
        }
        // classes.dex, then classes2.dex, classes3.dex … (ART's load order).
        var index = 2
        while (index <= MAX_DEX_SLOTS) {
            val name = "classes$index.dex"
            if (!existing.contains(name)) return name
            index++
        }
        throw PatchStageException("no free classesN.dex slot below $MAX_DEX_SLOTS")
    }

    // ---- Stage 4 helpers ---------------------------------------------------

    /** Edit the binary AndroidManifest of [apk] per patch_manifest.py; return AXML bytes. */
    fun editManifest(apk: File): ByteArray {
        val manifest = loadManifestBlock(apk)
        patchLauncher(manifest)
        patchBifrostIntegration(manifest)
        manifest.refreshFull()
        return manifest.getBytes()
    }

    private fun loadManifestBlock(apk: File): AndroidManifestBlock {
        ZipFile(apk).use { zip ->
            val entry = zip.getEntry(MANIFEST_ENTRY)
                ?: throw PatchStageException("APK has no $MANIFEST_ENTRY")
            zip.getInputStream(entry).use { return AndroidManifestBlock.load(it) }
        }
    }

    /**
     * 1. Strip MAIN/LAUNCHER/LEANBACK_LAUNCHER intent-filters from Unity's activity.
     * 2. Add io.pipboy.thor.LauncherActivity (NoDisplay, exported, noHistory,
     *    excludeFromRecents) with a MAIN+LAUNCHER+LEANBACK_LAUNCHER filter.
     * Idempotent: no-op if LauncherActivity is already present.
     */
    private fun patchLauncher(manifest: AndroidManifestBlock) {
        if (findActivity(manifest, LAUNCHER_FQN) != null) return

        val unity = findActivity(manifest, UNITY_FQN)
            ?: throw PatchStageException("manifest has no <activity> for $UNITY_FQN")
        stripLauncherFilters(unity)

        val labelRef = applicationLabelReference(manifest)
        val app = manifest.getOrCreateApplicationElement()
        val launcher = app.newElement(AndroidManifest.TAG_activity)
        setAndroidString(launcher, AndroidManifest.ID_name, AndroidManifest.NAME_name, LAUNCHER_FQN)
        setAndroidReference(launcher, AndroidManifest.ID_theme, AndroidManifest.NAME_theme, THEME_NO_DISPLAY)
        setAndroidReference(launcher, AndroidManifest.ID_label, AndroidManifest.NAME_label, labelRef)
        setAndroidBoolean(launcher, AndroidManifest.ID_exported, AndroidManifest.NAME_exported, true)
        setAndroidBoolean(launcher, ID_NO_HISTORY, NAME_NO_HISTORY, true)
        setAndroidBoolean(launcher, ID_EXCLUDE_FROM_RECENTS, NAME_EXCLUDE_FROM_RECENTS, true)
        addLauncherIntentFilter(launcher)
    }

    private fun stripLauncherFilters(activity: ResXmlElement) {
        activity.removeElementsIf { node ->
            node is ResXmlElement &&
                node.name == AndroidManifest.TAG_intent_filter &&
                filterIsLauncher(node)
        }
    }

    private fun filterIsLauncher(filter: ResXmlElement): Boolean {
        var isMain = false
        var isLauncher = false
        val actions = filter.getElements(AndroidManifest.TAG_action)
        while (actions.hasNext()) {
            if (androidName(actions.next()) == ACTION_MAIN) isMain = true
        }
        val cats = filter.getElements(AndroidManifest.TAG_category)
        while (cats.hasNext()) {
            val n = androidName(cats.next())
            if (n == CAT_LAUNCHER || n == CAT_LEANBACK) isLauncher = true
        }
        return isMain || isLauncher
    }

    private fun addLauncherIntentFilter(activity: ResXmlElement) {
        val filter = activity.newElement(AndroidManifest.TAG_intent_filter)
        setAndroidString(
            filter.newElement(AndroidManifest.TAG_action),
            AndroidManifest.ID_name, AndroidManifest.NAME_name, ACTION_MAIN
        )
        setAndroidString(
            filter.newElement(AndroidManifest.TAG_category),
            AndroidManifest.ID_name, AndroidManifest.NAME_name, CAT_LAUNCHER
        )
        setAndroidString(
            filter.newElement(AndroidManifest.TAG_category),
            AndroidManifest.ID_name, AndroidManifest.NAME_name, CAT_LEANBACK
        )
    }

    /** Add CONTROL_LEDS uses-permission + a <queries><package .../> for Bifrost. Idempotent. */
    private fun patchBifrostIntegration(manifest: AndroidManifestBlock) {
        if (manifest.getUsesPermission(BIFROST_PERMISSION) == null) {
            manifest.addUsesPermission(BIFROST_PERMISSION)
        }
        ensureBifrostQuery(manifest)
    }

    private fun ensureBifrostQuery(manifest: AndroidManifestBlock) {
        val root = manifest.getManifestElement()
            ?: throw PatchStageException("manifest has no <manifest> root")
        val queries = root.getOrCreateElement(TAG_QUERIES)
        val packages = queries.getElements(AndroidManifest.TAG_package)
        while (packages.hasNext()) {
            if (androidName(packages.next()) == BIFROST_PACKAGE) return
        }
        setAndroidString(
            queries.newElement(AndroidManifest.TAG_package),
            AndroidManifest.ID_name, AndroidManifest.NAME_name, BIFROST_PACKAGE
        )
    }

    private fun findActivity(manifest: AndroidManifestBlock, fqn: String): ResXmlElement? {
        val activities = manifest.listActivities()
        for (activity in activities) {
            if (androidName(activity) == fqn) return activity
        }
        return null
    }

    /** Read the application's existing @string/app_name label resource id to reuse. */
    private fun applicationLabelReference(manifest: AndroidManifestBlock): Int {
        val ref = manifest.applicationLabelReference
        return ref ?: throw PatchStageException("application has no @string label reference")
    }

    private fun androidName(element: ResXmlElement): String? =
        element.searchAttributeByResourceId(AndroidManifest.ID_name)?.valueAsString

    private fun setAndroidString(element: ResXmlElement, id: Int, name: String, value: String) {
        val attr = element.getOrCreateAndroidAttribute(name, id)
        attr.setValueAsString(value)
    }

    private fun setAndroidBoolean(element: ResXmlElement, id: Int, name: String, value: Boolean) {
        val attr = element.getOrCreateAndroidAttribute(name, id)
        attr.setValueAsBoolean(value)
    }

    private fun setAndroidReference(element: ResXmlElement, id: Int, name: String, resId: Int) {
        val attr = element.getOrCreateAndroidAttribute(name, id)
        attr.valueType = ValueType.REFERENCE
        attr.data = resId
    }

    // ---- Stage 5 helpers ---------------------------------------------------

    /**
     * Rebuild the APK zip: copy every input entry verbatim except the manifest,
     * the managed DLL, and any stale META-INF v1-signature file (dropped — apksig
     * re-signs); swap in [editedManifest] + [patchedDll]; append [helperDex] as
     * [dexEntryName]. Stored (uncompressed) for native libs to keep them aligned.
     */
    private fun repackage(
        inputApk: File,
        patchedDll: File,
        editedManifest: ByteArray,
        helperDex: File,
        dexEntryName: String,
        outApk: File
    ) {
        val dllBytes = patchedDll.readBytes()
        val dexBytes = helperDex.readBytes()
        ZipFile(inputApk).use { zip ->
            ZipOutputStream(outApk.outputStream().buffered()).use { out ->
                out.setLevel(Deflater.BEST_SPEED)
                copyInputEntries(zip, out, editedManifest, dllBytes, dexEntryName)
                // Append (or overwrite) the helper dex slot.
                writeEntry(out, dexEntryName, dexBytes, stored = false)
            }
        }
    }

    private fun copyInputEntries(
        zip: ZipFile,
        out: ZipOutputStream,
        editedManifest: ByteArray,
        dllBytes: ByteArray,
        dexEntryName: String
    ) {
        val entries = zip.entries()
        var count = 0
        while (entries.hasMoreElements() && count < MAX_ZIP_ENTRIES) {
            count++
            val entry = entries.nextElement()
            val name = entry.name
            when {
                entry.isDirectory -> { /* directories are implicit; skip */ }
                isOldSignatureFile(name) -> { /* drop stale v1 signature */ }
                name == dexEntryName -> { /* will be (re)written after the loop */ }
                name == MANIFEST_ENTRY -> writeEntry(out, name, editedManifest, stored = false)
                name == MANAGED_DLL_ENTRY -> writeEntry(out, name, dllBytes, stored = false)
                else -> copyEntry(zip, entry, out)
            }
        }
    }

    private fun isOldSignatureFile(name: String): Boolean {
        if (!name.startsWith("META-INF/")) return false
        val upper = name.uppercase()
        return upper.endsWith(".SF") || upper.endsWith(".RSA") ||
            upper.endsWith(".DSA") || upper.endsWith(".EC") ||
            upper == "META-INF/MANIFEST.MF"
    }

    private fun copyEntry(zip: ZipFile, entry: ZipEntry, out: ZipOutputStream) {
        // Preserve STORED vs DEFLATED so .so/.png stay uncompressed for alignment.
        val stored = entry.method == ZipEntry.STORED
        val bytes = zip.getInputStream(entry).use { it.readBytes() }
        writeEntry(out, entry.name, bytes, stored)
    }

    private fun writeEntry(out: ZipOutputStream, name: String, bytes: ByteArray, stored: Boolean) {
        val entry = ZipEntry(name)
        if (stored) {
            entry.method = ZipEntry.STORED
            entry.size = bytes.size.toLong()
            entry.compressedSize = bytes.size.toLong()
            val crc = CRC32()
            crc.update(bytes)
            entry.crc = crc.value
        } else {
            entry.method = ZipEntry.DEFLATED
        }
        out.putNextEntry(entry)
        var offset = 0
        while (offset < bytes.size) {
            val len = minOf(ZIP_BUFFER, bytes.size - offset)
            out.write(bytes, offset, len)
            offset += len
        }
        out.closeEntry()
    }

    private fun signApk(input: File, output: File, key: KeyMaterial) {
        if (output.exists()) check(output.delete()) { "could not clear stale output: ${output.path}" }
        val signerConfig = ApkSigner.SignerConfig.Builder(
            key.alias, key.privateKey, key.certificateChain
        ).build()
        // Do NOT pin minSdkVersion: apksig reads it from the APK's manifest
        // (the original companion declares minSdk 14). Pinning 33 would make the
        // v1 (JAR) signature use SHA-256 digests, which apksigner rejects when
        // verifying for API 14-17. Letting apksig auto-detect emits SHA-1 v1
        // digests (valid for API 14+) plus the modern v2/v3 schemes — matching
        // the desktop reference's signature set.
        ApkSigner.Builder(listOf(signerConfig))
            .setInputApk(input)
            .setOutputApk(output)
            .setV1SigningEnabled(true)
            .setV2SigningEnabled(true)
            .setV3SigningEnabled(true)
            .setAlignFileSize(true)
            .build()
            .sign()
    }

    /** Private key + certificate chain for signing (loaded from a keystore). */
    data class KeyMaterial(
        val alias: String,
        val privateKey: PrivateKey,
        val certificateChain: List<X509Certificate>
    )

    /**
     * Load [KeyMaterial] from a PKCS12/JKS keystore file. Caller owns keystore
     * creation (on-device: app files dir; harness: scratch).
     */
    fun loadKeyMaterial(
        keystore: File,
        storePass: CharArray,
        alias: String,
        keyPass: CharArray
    ): KeyMaterial {
        require(keystore.isFile) { "keystore missing: ${keystore.path}" }
        val ks = KeyStore.getInstance(detectKeystoreType(keystore))
        keystore.inputStream().use { ks.load(it, storePass) }
        val key = ks.getKey(alias, keyPass) as? PrivateKey
            ?: throw PatchStageException("keystore has no private key for alias '$alias'")
        val chain = ks.getCertificateChain(alias)
            ?: throw PatchStageException("keystore has no certificate chain for alias '$alias'")
        val x509 = chain.mapNotNull { it as? X509Certificate }
        if (x509.isEmpty()) throw PatchStageException("certificate chain for '$alias' is not X.509")
        return KeyMaterial(alias, key, x509)
    }

    private fun detectKeystoreType(keystore: File): String {
        // PKCS12 magic is 0x30; JKS magic is 0xFEEDFEED. Default to PKCS12.
        val head = ByteArray(4)
        keystore.inputStream().use { it.read(head) }
        val isJks = head[0] == 0xFE.toByte() && head[1] == 0xED.toByte() &&
            head[2] == 0xFE.toByte() && head[3] == 0xED.toByte()
        return if (isJks) "JKS" else "PKCS12"
    }

    // ARSCLib's AndroidManifest exposes ID_/NAME_ for most attrs but not these two.
    private const val ID_NO_HISTORY = 0x0101022d
    private const val NAME_NO_HISTORY = "noHistory"
    private const val ID_EXCLUDE_FROM_RECENTS = 0x01010017
    private const val NAME_EXCLUDE_FROM_RECENTS = "excludeFromRecents"
    private const val TAG_QUERIES = "queries"

    /** Write [bytes] to [dest], creating parent dirs. Used by callers staging the helper dex. */
    fun writeFile(dest: File, bytes: ByteArray) {
        dest.parentFile?.mkdirs()
        FileOutputStream(dest).use { it.write(bytes) }
    }
}
