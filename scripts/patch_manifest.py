#!/usr/bin/env python3
"""
patch_manifest.py — apply manifest changes for Strip-Boy.

Idempotent. Adds io.pipboy.thor.LauncherActivity as the MAIN/LAUNCHER
activity, strips the launcher intent-filter from UnityPlayerNativeActivity,
so taps on the launcher icon route through our display-redirecting
LauncherActivity first.

Run with the path to the decoded AndroidManifest.xml as the argument:

    python3 patch_manifest.py path/to/extracted/AndroidManifest.xml
"""

import sys
import xml.etree.ElementTree as ET

ANDROID_NS = "http://schemas.android.com/apk/res/android"
ET.register_namespace("android", ANDROID_NS)

NAME_ATTR = f"{{{ANDROID_NS}}}name"
LAUNCHER_FQN = "io.pipboy.thor.LauncherActivity"
UNITY_FQN = "com.unity3d.player.UnityPlayerNativeActivity"

def main(path: str) -> int:
    tree = ET.parse(path)
    root = tree.getroot()
    app = root.find("application")
    if app is None:
        print("ERROR: no <application> element", file=sys.stderr)
        return 2

    # Independent idempotence — the launcher and Bifrost integration can
    # land at different times (e.g. someone adds the permission to an
    # already-launcher-patched extraction). Each phase no-ops if its
    # marker is already present.
    launcher_already = any(
        a.get(NAME_ATTR) == LAUNCHER_FQN for a in app.findall("activity"))

    if launcher_already:
        print(f"skip: {LAUNCHER_FQN} already present in manifest")
    else:
        _patch_launcher(app)

    _patch_bifrost_integration(root)

    tree.write(path, xml_declaration=True, encoding="utf-8")
    return 0


def _patch_launcher(app):
    """1. Strip launcher intent-filter from UnityPlayerNativeActivity."""
    unity_activity = None
    for activity in app.findall("activity"):
        if activity.get(NAME_ATTR) == UNITY_FQN:
            unity_activity = activity
            break
    if unity_activity is None:
        print(f"ERROR: couldn't find <activity> for {UNITY_FQN}", file=sys.stderr)
        return 2

    removed = 0
    for filt in list(unity_activity.findall("intent-filter")):
        # Remove any intent-filter that contains MAIN or LAUNCHER, leave
        # any others (Unity ships none, but defensive).
        actions = filt.findall("action")
        cats = filt.findall("category")
        is_launcher = any(
            c.get(NAME_ATTR) == "android.intent.category.LAUNCHER"
            or c.get(NAME_ATTR) == "android.intent.category.LEANBACK_LAUNCHER"
            for c in cats
        )
        is_main = any(a.get(NAME_ATTR) == "android.intent.action.MAIN" for a in actions)
        if is_launcher or is_main:
            unity_activity.remove(filt)
            removed += 1

    # 2. Insert our LauncherActivity. Translucent + noHistory so it never paints.
    launcher = ET.SubElement(app, "activity")
    launcher.set(NAME_ATTR, LAUNCHER_FQN)
    launcher.set(f"{{{ANDROID_NS}}}exported", "true")
    launcher.set(f"{{{ANDROID_NS}}}label", "@string/app_name")
    launcher.set(f"{{{ANDROID_NS}}}theme", "@android:style/Theme.NoDisplay")
    launcher.set(f"{{{ANDROID_NS}}}noHistory", "true")
    launcher.set(f"{{{ANDROID_NS}}}excludeFromRecents", "true")

    filt = ET.SubElement(launcher, "intent-filter")
    action = ET.SubElement(filt, "action")
    action.set(NAME_ATTR, "android.intent.action.MAIN")
    cat1 = ET.SubElement(filt, "category")
    cat1.set(NAME_ATTR, "android.intent.category.LAUNCHER")
    cat2 = ET.SubElement(filt, "category")
    cat2.set(NAME_ATTR, "android.intent.category.LEANBACK_LAUNCHER")

    print(f"patched: stripped {removed} launcher intent-filter(s) from {UNITY_FQN}, added {LAUNCHER_FQN}")


def _patch_bifrost_integration(root):
    """Add CONTROL_LEDS permission + <queries> block (idempotent)."""
    bifrost_perm = "com.moonbench.bifrost.permission.CONTROL_LEDS"
    bifrost_pkg = "com.moonbench.bifrost"

    perm_added = False
    if not any(p.get(NAME_ATTR) == bifrost_perm for p in root.findall("uses-permission")):
        perm = ET.Element("uses-permission")
        perm.set(NAME_ATTR, bifrost_perm)
        root.insert(0, perm)
        perm_added = True

    pkg_added = False
    queries = root.find("queries")
    if queries is None:
        queries = ET.SubElement(root, "queries")
    if not any(p.get(NAME_ATTR) == bifrost_pkg for p in queries.findall("package")):
        pkg = ET.SubElement(queries, "package")
        pkg.set(NAME_ATTR, bifrost_pkg)
        pkg_added = True

    if perm_added or pkg_added:
        bits = []
        if perm_added: bits.append("CONTROL_LEDS")
        if pkg_added:  bits.append("<queries package=bifrost>")
        print(f"patched: Bifrost integration — {', '.join(bits)}")
    else:
        print("skip: Bifrost integration already present")


if __name__ == "__main__":
    if len(sys.argv) != 2:
        print("usage: patch_manifest.py <AndroidManifest.xml>", file=sys.stderr)
        sys.exit(2)
    sys.exit(main(sys.argv[1]))
