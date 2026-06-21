#!/usr/bin/env bash
# build.sh — Strip-Boy patcher pipeline (macOS / Linux).
#
# Reads the original APK (you supply it), produces a patched APK that
# can be sideloaded onto your Android handheld.
#
# Pipeline:
#   1.  apktool d   → extracted source tree (cached if already done)
#   2.  patch manifest + drop smali LauncherActivity     (idempotent)
#   3.  pipboy-patcher (Cecil) on Assembly-CSharp.dll    (idempotent)
#   4.  apktool b   → unsigned APK
#   5.  zipalign    → aligned APK
#   6.  apksigner   → signed APK
#
# Usage:
#     ./scripts/build.sh [path/to/original.apk]
#
# If no path is given, the script looks for the APK at:
#     apk/original.apk                                  (recommended)
#     ./fallout-pip-boy-1-2.apk
#     ~/Downloads/fallout-pip-boy-1-2.apk
#
# Environment overrides:
#     ANDROID_BUILD_TOOLS   path to build-tools/<version>/
#     KEYSTORE              path to signing keystore   [default: apk/debug.keystore]
#     KS_PASS               keystore password          [default: android]
#     KS_ALIAS              keystore key alias         [default: pipboy-debug]
#     FORCE_RESTART         set to "1" to wipe apk/extracted/ before decompile
#
# Outputs:
#     apk/out/pipboy-loopback.apk    — installable APK
#     apk/debug.keystore             — signing key (generated first run)

set -euo pipefail

ROOT="$(cd "$(dirname "$0")/.." && pwd)"
cd "$ROOT"

# ---------------------------------------------------------------------------
# Resolve original APK path.
# ---------------------------------------------------------------------------
DEFAULT_APK_CANDIDATES=(
    "apk/original.apk"
    "./fallout-pip-boy-1-2.apk"
    "$HOME/Downloads/fallout-pip-boy-1-2.apk"
    "$HOME/Downloads/fallout-pip-boy.apk"
)

if [ "${1:-}" != "" ]; then
    APK_IN="$1"
elif [ -n "${APK_IN:-}" ]; then
    : # honor env var
else
    APK_IN=""
    for c in "${DEFAULT_APK_CANDIDATES[@]}"; do
        if [ -f "$c" ]; then APK_IN="$c"; break; fi
    done
fi

if [ -z "${APK_IN:-}" ] || [ ! -f "$APK_IN" ]; then
    cat >&2 <<EOF
ERROR: original Fallout 4 Pip-Boy APK not found.

This script doesn't (and can't) include Bethesda's APK. You need a
personal copy of v1.2 (com.bethsoft.falloutcompanionapp, versionCode 9,
~38 MB). Drop it at any of:

    apk/original.apk
    ./fallout-pip-boy-1-2.apk
    ~/Downloads/fallout-pip-boy-1-2.apk

Or pass the path as the first argument:
    ./scripts/build.sh /path/to/your.apk
EOF
    exit 2
fi

# ---------------------------------------------------------------------------
# Paths.
# ---------------------------------------------------------------------------
EXTRACTED_DIR="apk/extracted"
MANAGED_DIR="apk/managed"
WORK_DIR="apk/work"
OUT_DIR="apk/out"
KEYSTORE="${KEYSTORE:-apk/debug.keystore}"
PATCHER_PROJ="patcher/Patcher.csproj"
PATCHER_DLL="patcher/bin/Release/net10.0/pipboy-patcher.dll"
LAUNCHER_SMALI_SRC="patcher/smali/io/pipboy/thor/LauncherActivity.smali"
LED_BRIDGE_SMALI_SRC="patcher/smali/io/pipboy/thor/LEDBridge.smali"

ORIGINAL_DLL_PATH="$EXTRACTED_DIR/assets/bin/Data/Managed/Assembly-CSharp.dll"
DLL_BACKUP="$MANAGED_DIR/Assembly-CSharp.original.dll"
DLL_PATCHED="$MANAGED_DIR/Assembly-CSharp.patched.dll"
APK_FROM_APKTOOL="$WORK_DIR/from-apktool.apk"
APK_ALIGNED="$WORK_DIR/pipboy-aligned.apk"
APK_FINAL="$OUT_DIR/pipboy-loopback.apk"

KS_ALIAS="${KS_ALIAS:-pipboy-debug}"
KS_PASS="${KS_PASS:-android}"

# ---------------------------------------------------------------------------
# Tool discovery.
# ---------------------------------------------------------------------------
detect_build_tools() {
    if [ -n "${ANDROID_BUILD_TOOLS:-}" ] && [ -x "$ANDROID_BUILD_TOOLS/apksigner" ]; then
        echo "$ANDROID_BUILD_TOOLS"; return
    fi
    local search_roots=()
    [ -n "${ANDROID_HOME:-}" ] && search_roots+=("$ANDROID_HOME/build-tools")
    [ -n "${ANDROID_SDK_ROOT:-}" ] && search_roots+=("$ANDROID_SDK_ROOT/build-tools")
    case "$(uname -s)" in
        Darwin) search_roots+=("$HOME/Library/Android/sdk/build-tools") ;;
        Linux)  search_roots+=("$HOME/Android/Sdk/build-tools" "$HOME/android-sdk/build-tools") ;;
    esac
    for root in "${search_roots[@]}"; do
        [ -d "$root" ] || continue
        local candidate
        candidate="$(ls -1 "$root" 2>/dev/null | sort -rV | while read -r v; do
            [ -x "$root/$v/apksigner" ] && echo "$root/$v" && break
        done)"
        [ -n "$candidate" ] && echo "$candidate" && return
    done
}

BT="$(detect_build_tools)"
if [ -z "$BT" ]; then
    echo "ERROR: Android build-tools not found (need apksigner + zipalign)." >&2
    echo "Install via Android Studio's SDK Manager, or set ANDROID_BUILD_TOOLS." >&2
    exit 2
fi
APKSIGNER="$BT/apksigner"
ZIPALIGN="$BT/zipalign"

command -v dotnet  >/dev/null || { echo "ERROR: dotnet SDK not on PATH" >&2; exit 2; }
command -v keytool >/dev/null || { echo "ERROR: keytool not on PATH (install any JDK)" >&2; exit 2; }
command -v apktool >/dev/null || { echo "ERROR: apktool not on PATH (brew install apktool / apt install apktool)" >&2; exit 2; }
command -v python3 >/dev/null || { echo "ERROR: python3 not on PATH" >&2; exit 2; }

# ---------------------------------------------------------------------------
# Logging.
# ---------------------------------------------------------------------------
if [ -t 1 ]; then GREEN=$'\033[1;32m'; RED=$'\033[1;31m'; RESET=$'\033[0m'
else GREEN=""; RED=""; RESET=""; fi
log()  { printf '%s[build]%s %s\n' "$GREEN" "$RESET" "$*"; }
die()  { printf '%s[build]%s %s\n' "$RED" "$RESET" "$*" >&2; exit 1; }

mkdir -p "$MANAGED_DIR" "$WORK_DIR" "$OUT_DIR"

log "input APK: $APK_IN"
log "build-tools: $BT"

# ---------------------------------------------------------------------------
# 1. apktool decompile (cached unless FORCE_RESTART or input newer).
# ---------------------------------------------------------------------------
need_decompile=0
if [ "${FORCE_RESTART:-}" = "1" ]; then
    need_decompile=1
elif [ ! -d "$EXTRACTED_DIR" ] || [ ! -f "$EXTRACTED_DIR/AndroidManifest.xml" ]; then
    need_decompile=1
elif [ "$APK_IN" -nt "$EXTRACTED_DIR/AndroidManifest.xml" ]; then
    need_decompile=1
fi

if [ "$need_decompile" = "1" ]; then
    log "1/6  apktool d  (decompile)"
    rm -rf "$EXTRACTED_DIR"
    apktool d "$APK_IN" -o "$EXTRACTED_DIR" -f 2>&1 | tail -1 | sed 's/^/     /'
else
    log "1/6  apktool decompile cached at $EXTRACTED_DIR"
fi

# ---------------------------------------------------------------------------
# 2. Manifest + smali changes (idempotent).
# ---------------------------------------------------------------------------
log "2/6  manifest + smali"
python3 scripts/patch_manifest.py "$EXTRACTED_DIR/AndroidManifest.xml" | sed 's/^/     /'

# Drop our smali files into the smali tree. The Pip-Boy APK has multiple
# smali roots (smali/, smali_classes2/, …); io.pipboy.thor.* is fresh
# package space we own, so always go into the primary smali/.
SMALI_DST_DIR="$EXTRACTED_DIR/smali/io/pipboy/thor"
mkdir -p "$SMALI_DST_DIR"
for src in "$LAUNCHER_SMALI_SRC" "$LED_BRIDGE_SMALI_SRC"; do
    dst="$SMALI_DST_DIR/$(basename "$src")"
    if ! cmp -s "$src" "$dst" 2>/dev/null; then
        cp "$src" "$dst"
        log "     copied $(basename "$src")"
    else
        log "     $(basename "$src") already current"
    fi
done

# ---------------------------------------------------------------------------
# 3. Build patcher (lazy) + apply Cecil patches to Assembly-CSharp.dll.
# ---------------------------------------------------------------------------
if [ ! -f "$PATCHER_DLL" ] || [ "patcher/Program.cs" -nt "$PATCHER_DLL" ]; then
    log "3/6  build Cecil patcher (Release)"
    dotnet build "$PATCHER_PROJ" -c Release >/dev/null
else
    log "3/6  Cecil patcher up-to-date"
fi

# Always extract a pristine copy of the DLL straight from the original
# APK (never from the extracted tree — that gets overwritten with the
# patched DLL at the end of each build, so re-using it would re-patch
# an already-patched DLL).
unzip -p "$APK_IN" "assets/bin/Data/Managed/Assembly-CSharp.dll" > "$DLL_BACKUP"
[ -s "$DLL_BACKUP" ] || die "extracted DLL is empty — wrong APK path?"

log "     run patcher"
dotnet "$PATCHER_DLL" "$DLL_BACKUP" "$DLL_PATCHED" | sed 's/^/     /'

# Overwrite the DLL inside the extracted tree with the patched version.
cp "$DLL_PATCHED" "$ORIGINAL_DLL_PATH"

# ---------------------------------------------------------------------------
# 4. apktool build.
# ---------------------------------------------------------------------------
log "4/6  apktool b  (rebuild)"
rm -f "$APK_FROM_APKTOOL"
# Run apktool b; let it write to stderr/stdout directly, and only
# proceed if it exits cleanly. The old pipe `| tail -3 | sed` swallowed
# apktool's non-zero exit code via the trailing pipe stage, which is
# how an upstream smali typo could let the build "succeed" but produce
# a stale APK with the previous classes.dex.
apktool b "$EXTRACTED_DIR" -o "$APK_FROM_APKTOOL" 2>&1 | sed 's/^/     /'
apktool_rc=${PIPESTATUS[0]}
if [ "$apktool_rc" != "0" ]; then
    die "apktool b failed (exit $apktool_rc) — likely a smali assemble error above"
fi
[ -s "$APK_FROM_APKTOOL" ] || die "apktool b produced no output at $APK_FROM_APKTOOL"

# ---------------------------------------------------------------------------
# 5. zipalign.
# ---------------------------------------------------------------------------
log "5/6  zipalign"
rm -f "$APK_ALIGNED"
"$ZIPALIGN" -p -f 4 "$APK_FROM_APKTOOL" "$APK_ALIGNED"

# ---------------------------------------------------------------------------
# 6. Keystore + sign.
# ---------------------------------------------------------------------------
if [ ! -f "$KEYSTORE" ]; then
    log "6/6  generate debug keystore (one-time)"
    keytool -genkey -v \
        -keystore "$KEYSTORE" \
        -alias "$KS_ALIAS" \
        -keyalg RSA -keysize 2048 \
        -validity 10000 \
        -storepass "$KS_PASS" -keypass "$KS_PASS" \
        -dname "CN=Strip-Boy, OU=local, O=local, C=US" 2>&1 | tail -3
else
    log "6/6  reuse existing debug keystore"
fi

log "     sign + verify"
rm -f "$APK_FINAL"
"$APKSIGNER" sign \
    --ks "$KEYSTORE" \
    --ks-pass "pass:$KS_PASS" \
    --key-pass "pass:$KS_PASS" \
    --ks-key-alias "$KS_ALIAS" \
    --out "$APK_FINAL" \
    "$APK_ALIGNED"
"$APKSIGNER" verify "$APK_FINAL" 2>&1 | grep -vE '^WARNING|^$' | sed 's/^/     /'

SZ=$(stat -f '%z' "$APK_FINAL" 2>/dev/null || stat -c '%s' "$APK_FINAL" 2>/dev/null || echo 0)
SZ_MB=$(( SZ / 1048576 ))
log "DONE — $APK_FINAL (~${SZ_MB} MB)"
log ""
log "Install:    adb install -r $APK_FINAL"
log "Uninstall:  adb uninstall com.bethsoft.falloutcompanionapp"
