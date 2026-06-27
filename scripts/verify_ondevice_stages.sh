#!/usr/bin/env bash
# verify_ondevice_stages.sh — off-device validation of the on-device patch
# stages 3-5 (Option A).
#
# The on-device arm64 patcher (stage 2) cannot run on a Mac/Linux dev host, but
# the stage 3-5 LOGIC (smali inject, binary-AXML manifest edit, repackage +
# apksig sign) is pure JVM. This driver runs that EXACT code path — the app's
# PatchAssembler, exercised by the :app:testDebugUnitTest harness — against the
# real original companion APK, using the desktop Cecil patcher's DLL output as
# the stage-2 stand-in, then structurally compares the result to the known-good
# reference produced by scripts/build.sh.
#
# Checks (all must pass):
#   1. apksigner verify          → the produced APK is validly signed.
#   2. aapt2 dump xmltree diff    → manifest edits match the reference.
#   3. dexdump                    → io.pipboy.thor.{LauncherActivity,LEDBridge,
#                                   LEDClear} are present in the dex set.
#   4. sha256                     → in-APK Assembly-CSharp.dll == patched DLL.
#
# Final install + on-device launch is corporeality-gated (needs the AYN Thor).
#
# Usage:
#   ./scripts/verify_ondevice_stages.sh [path/to/original.apk]
#
# Env overrides:
#   ANDROID_BUILD_TOOLS   build-tools dir (else auto-detected)
#   KEYSTORE / KS_PASS / KS_ALIAS   signing key (default apk/debug.keystore)

set -euo pipefail

ROOT="$(cd "$(dirname "$0")/.." && pwd)"
cd "$ROOT"

APK_IN="${1:-apk/original.apk}"
[ -f "$APK_IN" ] || { echo "ERROR: original APK not found at $APK_IN" >&2; exit 2; }
# Resolve to an absolute path: the gradle test runs with the module dir as CWD,
# so a repo-relative path would not resolve inside the harness.
APK_IN="$(cd "$(dirname "$APK_IN")" && pwd)/$(basename "$APK_IN")"

WRAP="$ROOT/thor-launch-wrapper"
SCRATCH="$(mktemp -d "${TMPDIR:-/tmp}/thor-verify.XXXXXX")"
trap 'rm -rf "$SCRATCH"' EXIT

KEYSTORE="${KEYSTORE:-$ROOT/apk/debug.keystore}"
[ -f "$KEYSTORE" ] && KEYSTORE="$(cd "$(dirname "$KEYSTORE")" && pwd)/$(basename "$KEYSTORE")"
KS_PASS="${KS_PASS:-android}"
KS_ALIAS="${KS_ALIAS:-pipboy-debug}"

JAVA_HOME_17="$(/usr/libexec/java_home -v 17 2>/dev/null || echo "${JAVA_HOME:-}")"
[ -n "$JAVA_HOME_17" ] || { echo "ERROR: need a JDK 17 (set JAVA_HOME)" >&2; exit 3; }
export JAVA_HOME="$JAVA_HOME_17"

# ---- Tool discovery --------------------------------------------------------
detect_build_tools() {
    if [ -n "${ANDROID_BUILD_TOOLS:-}" ] && [ -x "$ANDROID_BUILD_TOOLS/apksigner" ]; then
        echo "$ANDROID_BUILD_TOOLS"; return
    fi
    local roots=()
    [ -n "${ANDROID_SDK_ROOT:-}" ] && roots+=("$ANDROID_SDK_ROOT/build-tools")
    [ -n "${ANDROID_HOME:-}" ] && roots+=("$ANDROID_HOME/build-tools")
    roots+=("$HOME/Library/Android/sdk/build-tools" "$HOME/Android/Sdk/build-tools")
    local r found
    for r in "${roots[@]}"; do
        [ -d "$r" ] || continue
        found="$(ls -1 "$r" 2>/dev/null | sort -rV | while read -r v; do
            [ -x "$r/$v/apksigner" ] && echo "$r/$v" && break; done)"
        [ -n "$found" ] && { echo "$found"; return; }
    done
}
BT="$(detect_build_tools)"
[ -n "$BT" ] || { echo "ERROR: Android build-tools (apksigner/aapt2/dexdump) not found" >&2; exit 3; }
APKSIGNER="$BT/apksigner"; AAPT2="$BT/aapt2"; DEXDUMP="$BT/dexdump"
command -v dotnet >/dev/null || { echo "ERROR: dotnet not on PATH" >&2; exit 3; }

echo "[verify] build-tools: $BT"
echo "[verify] scratch: $SCRATCH"

# ---- Stage 2 stand-in: run the desktop Cecil patcher on a pristine DLL ------
echo "[verify] stage-2 stand-in: desktop Cecil patcher"
mkdir -p "$SCRATCH/dll"
PRISTINE="$SCRATCH/dll/Assembly-CSharp.original.dll"
PATCHED="$SCRATCH/dll/Assembly-CSharp.patched.dll"
unzip -p "$APK_IN" "assets/bin/Data/Managed/Assembly-CSharp.dll" > "$PRISTINE"
[ -s "$PRISTINE" ] || { echo "ERROR: empty DLL from $APK_IN" >&2; exit 1; }
dotnet run --project "$ROOT/patcher" -- "$PRISTINE" "$PATCHED" >/dev/null
[ -s "$PATCHED" ] || { echo "ERROR: Cecil patcher produced no DLL" >&2; exit 1; }

# ---- Stage 3 asset: build helper.dex via the wrapper's gradle task ----------
echo "[verify] stage-3 asset: gradle :app:buildHelperDex"
( cd "$WRAP" && ./gradlew -q :app:buildHelperDex )
HELPER_DEX="$WRAP/app/build/generated/helper-dex/helper.dex"
[ -s "$HELPER_DEX" ] || { echo "ERROR: helper.dex not produced" >&2; exit 1; }

# ---- Stages 3-5: run PatchAssembler via the harness unit test ---------------
echo "[verify] stages 3-5: :app:testDebugUnitTest harness"
OUT_APK="$SCRATCH/pipboy-verify.apk"
( cd "$WRAP" && ./gradlew -q :app:testDebugUnitTest --rerun-tasks \
    --tests 'com.moonbench.thorlaunch.PatchAssemblerHarnessTest' \
    -Dharness.run=true \
    -Dharness.inputApk="$APK_IN" \
    -Dharness.patchedDll="$PATCHED" \
    -Dharness.helperDex="$HELPER_DEX" \
    -Dharness.keystore="$KEYSTORE" \
    -Dharness.storePass="$KS_PASS" \
    -Dharness.alias="$KS_ALIAS" \
    -Dharness.keyPass="$KS_PASS" \
    -Dharness.outApk="$OUT_APK" )
[ -s "$OUT_APK" ] || { echo "ERROR: harness produced no APK" >&2; exit 1; }

# ---- Reference (known-good desktop pipeline) --------------------------------
REF="$ROOT/apk/out/pipboy-loopback.apk"
[ -f "$REF" ] || { echo "[verify] building reference (scripts/build.sh)"; ./scripts/build.sh "$APK_IN" >/dev/null; }

fail=0

echo; echo "########## 1. apksigner verify ##########"
if "$APKSIGNER" verify "$OUT_APK"; then echo "PASS — signature valid"; else echo "FAIL"; fail=1; fi

echo; echo "########## 2. manifest semantic diff vs reference ##########"
norm() {
    "$AAPT2" dump xmltree "$1" --file AndroidManifest.xml \
        | sed -E 's/\(line=[0-9]+\)//; s/ \(Raw: "[^"]*"\)//; s/0x[0-9a-fA-F]+/HEX/g' \
        | grep -iE 'LauncherActivity|UnityPlayerNativeActivity|CONTROL_LEDS|com.moonbench.bifrost|category.LAUNCHER|category.LEANBACK|action.MAIN|Theme.NoDisplay|noHistory|excludeFromRecents|:exported|E: queries|E: package|E: intent-filter' \
        | sed -E 's/^[[:space:]]+//' | sort
}
norm "$OUT_APK" > "$SCRATCH/mine.txt"
norm "$REF"     > "$SCRATCH/ref.txt"
if diff "$SCRATCH/ref.txt" "$SCRATCH/mine.txt"; then echo "PASS — manifest matches reference"; else echo "FAIL"; fail=1; fi

echo; echo "########## 3. helper dex class presence ##########"
mkdir -p "$SCRATCH/dex"; ( cd "$SCRATCH/dex" && unzip -o -q "$OUT_APK" 'classes*.dex' )
found="$(for d in "$SCRATCH"/dex/classes*.dex; do "$DEXDUMP" "$d" 2>/dev/null; done \
    | grep -oE 'Lio/pipboy/thor/(LauncherActivity|LEDBridge|LEDClear);' | sort -u)"
echo "$found"
if [ "$(echo "$found" | wc -l | tr -d ' ')" = "3" ]; then echo "PASS — 3 helper classes present"; else echo "FAIL"; fail=1; fi

echo; echo "########## 4. Assembly-CSharp.dll sha match ##########"
in_apk_sha="$(unzip -p "$OUT_APK" 'assets/bin/Data/Managed/Assembly-CSharp.dll' | shasum -a 256 | awk '{print $1}')"
patched_sha="$(shasum -a 256 "$PATCHED" | awk '{print $1}')"
echo "in-apk  : $in_apk_sha"
echo "patched : $patched_sha"
if [ "$in_apk_sha" = "$patched_sha" ]; then echo "PASS — DLL matches stage-2 output"; else echo "FAIL"; fail=1; fi

echo
if [ "$fail" = "0" ]; then
    echo "[verify] ALL CHECKS PASS — $OUT_APK"
    echo "[verify] device-gated remainder: install + launch on the AYN Thor."
else
    echo "[verify] ONE OR MORE CHECKS FAILED" >&2
fi
exit "$fail"
