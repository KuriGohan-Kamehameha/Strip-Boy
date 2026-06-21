#!/usr/bin/env bash
# regression-check.sh — guard the Strip-Boy Cecil patcher against regression.
#
# The patcher injects LED behaviour into Bethesda's Assembly-CSharp.dll by
# locating specific types/methods (PipboyPostEffect, PipboyMenuMovie, …). If a
# game update renames a target, or a patch is edited so it silently stops
# applying (idempotence false-positive), the LEDs quietly go dark with no build
# error. This script asserts every patch still applies and every injected
# symbol is present in the patched DLL.
#
# Requires a prior build (so apk/managed/Assembly-CSharp.original.dll exists)
# and ilspycmd (`dotnet tool install -g ilspycmd`). Exit 0 = all guarded.
set -euo pipefail
cd "$(dirname "$0")/.."

PRISTINE="apk/managed/Assembly-CSharp.original.dll"
fail=0
note() { printf '  %s\n' "$*"; }

if [ ! -f "$PRISTINE" ]; then
    echo "SKIP: pristine DLL not found ($PRISTINE) — run scripts/build.sh first." >&2
    exit 0
fi

ILSPY="$(command -v ilspycmd || echo "$HOME/.dotnet/tools/ilspycmd")"
if [ ! -x "$ILSPY" ] && ! command -v "$ILSPY" >/dev/null 2>&1; then
    echo "SKIP: ilspycmd not found (dotnet tool install -g ilspycmd)." >&2
    exit 0
fi

echo "[regr] building patcher + applying to pristine DLL"
dotnet build patcher/Patcher.csproj -c Debug >/dev/null
OUT="$(dotnet patcher/bin/Debug/net10.0/pipboy-patcher.dll "$PRISTINE" /tmp/regr-patched.dll 2>&1)"
echo "$OUT" | sed 's/^/  /'

echo "[regr] assertions"

# 1. No patch errored.
if echo "$OUT" | grep -q '^\[FAIL'; then note "FAIL: a patch errored"; fail=1; fi

# 2. Each expected patch reported [patch] (applied to the pristine DLL).
for p in LoopbackDiscovery LEDStickBridge FlickerSeed MenuPulse; do
    if echo "$OUT" | grep -q "\[patch\] $p"; then note "ok: $p applied"
    else note "FAIL: $p did not apply (target renamed or idempotence misfire?)"; fail=1; fi
done

# 3. Injected symbols present in the patched DLL. (Dump to a file — the IL is
#    ~250k lines; capturing it into a shell var is fragile under set -e.)
IL_TXT="/tmp/regr-il.txt"
: > "$IL_TXT"
"$ILSPY" -il /tmp/regr-patched.dll -t PipboyPostEffect >> "$IL_TXT" 2>/dev/null || true
"$ILSPY" -il /tmp/regr-patched.dll -t PipboyMenuMovie >> "$IL_TXT" 2>/dev/null || true
for sym in _stripboyFlickerRange menuPulse staticBurst _stripboyLedBridgeCls _stripboyVisibleInstanceId onFlickerToggle onBurst; do
    if grep -q "$sym" "$IL_TXT"; then note "ok: injected '$sym' present"
    else note "FAIL: injected symbol '$sym' missing from patched DLL"; fail=1; fi
done
rm -f "$IL_TXT"

# 4. The Java/smali side defines the methods the Cecil hooks call.
SMALI="patcher/smali/io/pipboy/thor/LEDBridge.smali"
for m in 'sendPulse(Ljava/lang/String;)V' 'menuPulse()V' 'staticBurst()V' 'apply(IIIF)V' 'onFlickerToggle(Z)V' 'onBurst()V'; do
    if grep -q "$m" "$SMALI"; then note "ok: smali $m present"
    else note "FAIL: smali method '$m' missing from LEDBridge.smali"; fail=1; fi
done

rm -f /tmp/regr-patched.dll
if [ "$fail" = 0 ]; then echo "[regr] PASS — all patcher behaviour guarded"; else
    echo "[regr] FAIL — see above" >&2; exit 1; fi
