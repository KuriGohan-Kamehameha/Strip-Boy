package com.moonbench.thorlaunch

import com.reandroid.app.AndroidManifest
import com.reandroid.arsc.chunk.xml.AndroidManifestBlock
import com.reandroid.arsc.chunk.xml.ResXmlElement
import com.reandroid.arsc.value.ValueType
import org.junit.Assert.assertEquals
import org.junit.Assert.assertFalse
import org.junit.Assert.assertNotNull
import org.junit.Assert.assertNull
import org.junit.Assert.assertTrue
import org.junit.Test
import java.io.ByteArrayInputStream

/**
 * Stage-4 manifest edit on a synthetic manifest shaped like the companion's
 * (no Bethesda bytes needed). Pins that LauncherActivity does not exclude the
 * Pip-Boy task from Recents, and that an older patch loses the exclusion.
 */
class PatchAssemblerManifestTest {

    @Test
    fun newPatchLeavesTheTaskInRecents() {
        val manifest = companionManifest()
        PatchAssembler.patchManifest(manifest)

        val reloaded = roundTrip(manifest)
        val launcher = activity(reloaded, PatchAssembler.LAUNCHER_FQN)
        assertNotNull("LauncherActivity added", launcher)
        assertNull("no excludeFromRecents", launcher!!.searchAttributeByResourceId(ID_EXCLUDE_FROM_RECENTS))
        assertTrue("noHistory kept", launcher.searchAttributeByResourceId(ID_NO_HISTORY)!!.valueAsBoolean)
        val unity = activity(reloaded, PatchAssembler.UNITY_FQN)!!
        assertFalse("Unity loses its launcher filter", unity.getElements(AndroidManifest.TAG_intent_filter).hasNext())
    }

    @Test
    fun olderPatchLosesTheExclusionOnRerun() {
        val manifest = companionManifest()
        PatchAssembler.patchManifest(manifest)
        activity(manifest, PatchAssembler.LAUNCHER_FQN)!!
            .getOrCreateAndroidAttribute("excludeFromRecents", ID_EXCLUDE_FROM_RECENTS)
            .setValueAsBoolean(true)
        assertNotNull(activity(manifest, PatchAssembler.LAUNCHER_FQN)!!.searchAttributeByResourceId(ID_EXCLUDE_FROM_RECENTS))

        PatchAssembler.patchManifest(manifest)

        val reloaded = roundTrip(manifest)
        val launchers = reloaded.listActivities().count { name(it) == PatchAssembler.LAUNCHER_FQN }
        assertEquals("still exactly one LauncherActivity", 1, launchers)
        assertNull(activity(reloaded, PatchAssembler.LAUNCHER_FQN)!!.searchAttributeByResourceId(ID_EXCLUDE_FROM_RECENTS))
    }

    private fun companionManifest(): AndroidManifestBlock {
        val manifest = AndroidManifestBlock()
        val app = manifest.getOrCreateApplicationElement()
        app.getOrCreateAndroidAttribute(AndroidManifest.NAME_label, AndroidManifest.ID_label).apply {
            valueType = ValueType.REFERENCE
            data = LABEL_RES_ID
        }
        val unity = app.newElement(AndroidManifest.TAG_activity)
        setName(unity, PatchAssembler.UNITY_FQN)
        val filter = unity.newElement(AndroidManifest.TAG_intent_filter)
        setName(filter.newElement(AndroidManifest.TAG_action), "android.intent.action.MAIN")
        setName(filter.newElement(AndroidManifest.TAG_category), "android.intent.category.LAUNCHER")
        return manifest
    }

    private fun roundTrip(manifest: AndroidManifestBlock): AndroidManifestBlock {
        manifest.refreshFull()
        return AndroidManifestBlock.load(ByteArrayInputStream(manifest.bytes))
    }

    private fun setName(element: ResXmlElement, value: String) {
        element.getOrCreateAndroidAttribute(AndroidManifest.NAME_name, AndroidManifest.ID_name).setValueAsString(value)
    }

    private fun name(element: ResXmlElement): String? =
        element.searchAttributeByResourceId(AndroidManifest.ID_name)?.valueAsString

    private fun activity(manifest: AndroidManifestBlock, fqn: String): ResXmlElement? =
        manifest.listActivities().firstOrNull { name(it) == fqn }

    private companion object {
        const val LABEL_RES_ID = 0x7f0a0001
        const val ID_EXCLUDE_FROM_RECENTS = 0x01010017
        const val ID_NO_HISTORY = 0x0101022d
    }
}
