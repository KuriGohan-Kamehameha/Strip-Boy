#!/usr/bin/env bash
# build.sh — produce Strip-Boy Flickerd APK from Java sources.
#
# No Gradle. Uses the same Android SDK build-tools Strip-Boy's own
# pipeline uses (apksigner + zipalign + aapt2), plus d8 for the
# .class → .dex step.
#
# Output: flickerd/out/flickerd.apk  (debug-signed, installable)
#
# Same keystore conventions as Strip-Boy:
#   KEYSTORE   defaults to ../apk/debug.keystore (shared with Strip-Boy)
#   KS_PASS    defaults to "android"
#   KS_ALIAS   defaults to "pipboy-debug"

set -euo pipefail

ROOT="$(cd "$(dirname "$0")/.." && pwd)"
cd "$ROOT"

OUT="$ROOT/out"
mkdir -p "$OUT/classes"

# --- toolchain discovery (same logic as ../scripts/build.sh) -----------
detect_build_tools() {
    if [ -n "${ANDROID_BUILD_TOOLS:-}" ] && [ -x "$ANDROID_BUILD_TOOLS/apksigner" ]; then
        echo "$ANDROID_BUILD_TOOLS"; return
    fi
    local roots=()
    [ -n "${ANDROID_HOME:-}" ] && roots+=("$ANDROID_HOME/build-tools")
    [ -n "${ANDROID_SDK_ROOT:-}" ] && roots+=("$ANDROID_SDK_ROOT/build-tools")
    case "$(uname -s)" in
        Darwin) roots+=("$HOME/Library/Android/sdk/build-tools") ;;
        Linux)  roots+=("$HOME/Android/Sdk/build-tools" "$HOME/android-sdk/build-tools") ;;
    esac
    for root in "${roots[@]}"; do
        [ -d "$root" ] || continue
        local v
        v="$(ls -1 "$root" | sort -rV | while read -r x; do
            [ -x "$root/$x/apksigner" ] && [ -x "$root/$x/d8" ] && echo "$root/$x" && break
        done)"
        [ -n "$v" ] && echo "$v" && return
    done
}

BT="$(detect_build_tools)"
[ -n "$BT" ] || { echo "ERROR: Android build-tools (with apksigner + d8) not found" >&2; exit 2; }

# Find android.jar — same SDK root that has build-tools/.
SDK_ROOT="$(dirname "$(dirname "$BT")")"
ANDROID_JAR=""
for v in $(ls -1 "$SDK_ROOT/platforms" 2>/dev/null | sort -rV); do
    if [ -f "$SDK_ROOT/platforms/$v/android.jar" ]; then
        ANDROID_JAR="$SDK_ROOT/platforms/$v/android.jar"
        break
    fi
done
[ -n "$ANDROID_JAR" ] || { echo "ERROR: android.jar not found under $SDK_ROOT/platforms" >&2; exit 2; }

KEYSTORE="${KEYSTORE:-$ROOT/../apk/debug.keystore}"
KS_PASS="${KS_PASS:-android}"
KS_ALIAS="${KS_ALIAS:-pipboy-debug}"

echo "[flickerd] build-tools: $BT"
echo "[flickerd] android.jar: $ANDROID_JAR"
echo "[flickerd] keystore:    $KEYSTORE"

# --- 1. javac -----------------------------------------------------------
echo "[flickerd] 1/5  javac"
find src -name '*.java' > "$OUT/sources.txt"
javac -source 1.8 -target 1.8 -bootclasspath "$ANDROID_JAR" \
    -d "$OUT/classes" @"$OUT/sources.txt"

# --- 2. d8 → dex --------------------------------------------------------
echo "[flickerd] 2/5  d8"
rm -f "$OUT/classes.dex"
"$BT/d8" --output "$OUT" --lib "$ANDROID_JAR" \
    $(find "$OUT/classes" -name '*.class')

# --- 3. aapt2 link → unsigned APK ---------------------------------------
echo "[flickerd] 3/5  aapt2 link"
rm -f "$OUT/flickerd-unsigned.apk"
"$BT/aapt2" link -o "$OUT/flickerd-unsigned.apk" \
    -I "$ANDROID_JAR" \
    --manifest AndroidManifest.xml

# Add the dex to the APK (aapt2 link doesn't include it).
echo "[flickerd] 3.5  zip in classes.dex"
( cd "$OUT" && zip -j flickerd-unsigned.apk classes.dex >/dev/null )

# --- 4. zipalign --------------------------------------------------------
echo "[flickerd] 4/5  zipalign"
rm -f "$OUT/flickerd-aligned.apk"
"$BT/zipalign" -p -f 4 "$OUT/flickerd-unsigned.apk" "$OUT/flickerd-aligned.apk"

# --- 5. apksigner -------------------------------------------------------
[ -f "$KEYSTORE" ] || {
    echo "[flickerd] generating debug keystore (one-time): $KEYSTORE"
    keytool -genkey -v \
        -keystore "$KEYSTORE" \
        -alias "$KS_ALIAS" \
        -keyalg RSA -keysize 2048 -validity 10000 \
        -storepass "$KS_PASS" -keypass "$KS_PASS" \
        -dname "CN=Strip-Boy, OU=local, O=local, C=US" 2>&1 | tail -3
}

echo "[flickerd] 5/5  apksigner"
rm -f "$OUT/flickerd.apk"
"$BT/apksigner" sign \
    --ks "$KEYSTORE" \
    --ks-pass "pass:$KS_PASS" \
    --key-pass "pass:$KS_PASS" \
    --ks-key-alias "$KS_ALIAS" \
    --out "$OUT/flickerd.apk" \
    "$OUT/flickerd-aligned.apk"

SZ=$(stat -f '%z' "$OUT/flickerd.apk" 2>/dev/null || stat -c '%s' "$OUT/flickerd.apk")
echo "[flickerd] DONE — $OUT/flickerd.apk (${SZ} bytes)"
echo
echo "Install: adb install -r $OUT/flickerd.apk"
echo "Uninstall: adb uninstall io.pipboy.thor.flickerd"
