# Thor launcher — companion auto-patch

Status: **in progress.** Detection + orchestration skeleton landed; the
on-device patch engine is unresolved and needs a decision (see
[The one open decision](#the-one-open-decision)).

## Goal

The launcher should not just *check* for a patched Pip-Boy companion — it
should notice an **unpatched** companion and patch it, ideally with no PC in
the loop.

## What landed

`CompanionPatcher.kt` + `MainActivity` wiring:

- **Tri-state detection.** `CompanionPatcher.detect()` returns
  `NOT_INSTALLED` / `UNPATCHED` / `PATCHED` (PATCHED = the marker activity
  `io.pipboy.thor.LauncherActivity` is present). Previously the launcher only
  distinguished "patched installed" from "everything else", so an unpatched
  companion looked identical to a missing one.
- **Auto-patch trigger.** When state is `UNPATCHED`, `maybeAutoPatchCompanion()`
  fires once per detection cycle (re-armed by "Refresh checks"). It runs the
  orchestration off the main thread.
- **APK acquisition (feasible, implemented).** `locateInstalledApk()` resolves
  the installed companion's `applicationInfo.sourceDir` (the on-disk
  `base.apk`) and reads it.
- **Baseline verification (feasible, implemented).** `verifyBaseline()`
  SHA-256s the APK and compares to the known v1.2 baseline from
  `docs/CHECKSUMS.md` (`974b8833…91ce`, 39 591 950 bytes). The patcher is
  structural, so a non-baseline build may still patch — the check informs, it
  does not hard-block.
- **Install (feasible, implemented).** `installPatchedApk()` hands the produced
  APK to the system installer via a `FileProvider` + `ACTION_VIEW`
  (`REQUEST_INSTALL_PACKAGES`). Android cannot install silently without root or
  device-owner, so this is a one-tap confirm, not zero-tap.
- **Engine seam.** The actual transform sits behind `interface PatchEngine`.
  The shipped default is `UnavailablePatchEngine`, which returns
  `PatchOutcome.Unsupported(reason)` — the flow degrades to a clear "build it on
  PC" message instead of fabricating a patched APK.

## Why the engine is empty

The Strip-Boy patch (see the repo `README.md` "What's patched" table) is:

1. **.NET IL edits** to `Assembly-CSharp.dll` via Mono.Cecil (11 methods).
2. **smali** helper classes + an `<intent-filter>` move, via apktool.
3. **zipalign + apksigner** to produce an installable, signed APK.

None of that toolchain runs on Android:

- There is no mature **CLR-metadata / IL editor for the JVM** (Cecil is .NET
  only). Naive byte-patching of a PE/CLI image is not viable — adding IL shifts
  method RVAs and rewrites metadata heaps.
- apktool needs a desktop JVM + framework; baksmali/smali do not run on ART as-is.

And two obvious shortcuts are **ruled out by the project's clean-room stance**
(`README.md` "Legal", `docs/COPYRIGHT.md` — "nothing in this repo redistributes
Bethesda's code or assets"):

- **Bundling a binary diff** (original → patched) inside the app. A bsdiff of
  `Assembly-CSharp.dll` carries original Bethesda bytes in its stream. Off-limits.
- **Remote patch service.** Uploading the user's companion APK to a host that
  runs the existing patcher transmits Bethesda's APK off-device. Off-limits
  (clean-room + privacy).

## The one open decision

How should the on-device IL patch actually run? Each option preserves
clean-room (it only ever touches the *user's own* APK, on the *user's own*
device, output staying local) but differs a lot in effort and UX:

| Option | Zero-PC? | Effort | Notes |
|--------|----------|--------|-------|
| **A. Bundle a native patcher** | Yes | High | Ship the patcher compiled for Android (`.NET` Android/AOT for the Cecil pass) + an on-device smali step. True one-device flow; large build-system change; arm64 + 32-bit-target concerns. |
| **B. Root-assisted local patch** | Yes (rooted) | Medium | If the Thor is rooted, run a bundled toolchain via `su` and `pm install` for a genuine zero-tap patch+install. Excludes non-rooted devices. |
| **C. Detect + guide (current)** | No | Done | Auto-detect unpatched, verify baseline, then point the user at `scripts/build.sh` on a PC and install the result. Honest, ships now, not "automatic". |

`PatchEngine` is the single seam — whichever option wins drops in as a new
implementation; `MainActivity` and the acquire/verify/install orchestration do
not change.

Recommendation: ship **C** as the floor (it's done and never lies), and pursue
**A** as the real target. **B** only if the Thor is reliably rooted.

## Option A — feasibility findings (2026-06-27)

Empirically probed on the Mac build host (dotnet 10.0.203, NDK 26.3.11579264):

- **The Cecil patcher is NativeAOT-compatible.** `dotnet publish -r osx-arm64
  -p:PublishAot=true` of `patcher/Patcher.csproj` succeeds and the resulting
  native binary runs (`usage: pipboy-patcher <input.dll> <output.dll>`). Only a
  trim warning (IL2104) from Mono.Cecil. So a standalone native patcher binary
  *is* producible — the IL pass does not need a managed runtime at all.
- **Cross-compiling to `linux-bionic-arm64` (Android) on this Mac is blocked.**
  Restore + IL compile succeed, but native codegen fails with
  `The PrivateSdkAssemblies ItemGroup is required` — the ILCompiler cross-pack
  for the target RID is missing. `runtime.linux-bionic-arm64.Microsoft.DotNet.ILCompiler`
  is published only at `8.0.0-preview.6.23329.7`; there is no 10.x build on
  nuget.org, dotnet-experimental, or dotnet10. So .NET 10 NativeAOT→bionic from
  macOS has no matching cross SDK.

Net: the native patcher is real and buildable; only the *build host / toolchain*
for the Android target is unresolved. The on-device side is host-agnostic — it
invokes whatever arm64 binary lands in the APK via `ProcessBuilder`, then runs
the smali inject (dexlib2, prebuilt helper dex), manifest edit (AXML), and
sign+align (apksig) in pure Java. Those stay the same regardless of how the
binary is produced.

### The macOS block is in the cross *orchestration*, not the pack version

Re-probed with .NET 8 (8.0.422) + the `8.0.0-preview.6` bionic cross-pack +
the Mac NDK: macOS still fails with the same `PrivateSdkAssemblies` error. So
the blocker is not the pack version — it is that NativeAOT cross to
`linux-bionic-arm64` is only wired from a **Linux x64 host** (where the host
ILCompiler is `runtime.linux-x64.*`). From `osx-arm64` the private SDK
assemblies are never populated. This is why the binary is built on a Linux
sibling.

## Binary build recipe (pinned)

Build host: **Linux x64**. Toolchain: **.NET 8 SDK** (the bionic cross-pack
only exists at `8.0.0-preview.6.23329.7`) + a **Linux Android NDK** (r26d).

Two extras beyond the obvious recipe are **required** (found empirically — the
build fails without them, even on Linux):

- `<LinkerFlavor>lld</LinkerFlavor>` as a **property** (not a LinkerArg — the
  SDK hardcodes `bfd` for `_targetOS==linux`, and bionic maps to linux, so the
  host GNU `ld` is wrongly selected → `unrecognized emulation mode aarch64linux`).
- A `KnownILCompilerPack` RID override, because the SDK's
  `ILCompilerRuntimeIdentifiers` list omits `linux-bionic-arm64`, so the target
  pack never resolves → empty `IlcSdkPath` → the `PrivateSdkAssemblies` error:

  ```xml
  <ItemGroup>
    <KnownILCompilerPack Update="Microsoft.DotNet.ILCompiler">
      <ILCompilerRuntimeIdentifiers>%(ILCompilerRuntimeIdentifiers);linux-bionic-arm64</ILCompilerRuntimeIdentifiers>
    </KnownILCompilerPack>
  </ItemGroup>
  ```

Verified output: `ELF 64-bit LSB pie executable, ARM aarch64`, 4 497 024 bytes,
sha256 `1ec9af9139c25df01009f0c0298a43a3d32f80edbb1bd2aa1b1ae09e24daba47`,
NEEDED = bionic system libs only (`liblog libdl libz libm libc`), interpreter
`/system/bin/linker64`. Bundled at
`app/src/main/jniLibs/arm64-v8a/libpipboy-patcher.so` and confirmed packaged in
the APK at `lib/arm64-v8a/libpipboy-patcher.so`.

```
# csproj: net8.0, PublishAot=true, AssemblyName pipboy-patcher
#   PackageReference Mono.Cecil 0.11.6
#   PackageReference Microsoft.DotNet.ILCompiler 8.0.0-preview.6.23329.7
#   PackageReference runtime.linux-bionic-arm64.Microsoft.DotNet.ILCompiler 8.0.0-preview.6.23329.7
#   <LinkerArg Include="-Wl,--no-rosegment" /> ; <LinkerArg Include="-llog" />
TC=<ndk>/toolchains/llvm/prebuilt/linux-x86_64
dotnet publish -r linux-bionic-arm64 -c Release \
  -p:CppCompilerAndLinker=$TC/bin/aarch64-linux-android21-clang \
  -p:ObjCopyName=$TC/bin/llvm-objcopy \
  -p:SysRoot=$TC/sysroot
```

Output: an `ELF aarch64` `pipboy-patcher`. It ships in the wrapper at
`app/src/main/jniLibs/arm64-v8a/libpipboy-patcher.so` (the `lib*.so` name +
`useLegacyPackaging`/`extractNativeLibs` is what lets it be exec'd from
`nativeLibraryDir` — Android 33 blocks exec from writable app dirs).

## On-device pipeline — status

`NativePatchEngine` is now the wired engine. Stages:

| Stage | Mechanism | Status |
|-------|-----------|--------|
| 1. Extract `Assembly-CSharp.dll` | `java.util.zip` from the companion APK | **done** |
| 2. IL patch | exec bundled `libpipboy-patcher.so` via `ProcessBuilder` | **done + binary bundled** (packaged in APK; on-device exec is device-gated) |
| 3. smali helper inject | dexlib2 + a prebuilt helper `.dex` from `patcher/smali/io/pipboy/thor/{LauncherActivity,LEDBridge,LEDClear}.smali` (Kuri's own code, no Bethesda bytes) merged into the companion `classes.dex` | **pending** (new dep) |
| 4. manifest intent-filter move | pure-Java AXML edit (desktop equivalent: `scripts/patch_manifest.py`, but that runs on apktool-decoded text; on-device must edit binary AXML) | **pending** (new dep) |
| 5. repackage + zipalign + sign | rebuild the zip, then `apksig` | **pending** (new dep) |

Stages 3-5 are standard binary-APK manipulation, but they produce an APK that
must install and run on the Thor — building them without an on-device iterate
loop risks a silently-broken APK. They are best landed with the device in hand;
each can be structurally checked off-device (`apksigner verify`, dex validation,
AXML re-parse) but final install+launch is corporeality-gated.
| 6. install | `PackageInstaller` / `ACTION_VIEW` (already wired in `CompanionPatcher`) | **done** |

Until stages 3-5 land, `NativePatchEngine.patch()` runs the real on-device IL
patch and then returns `Unsupported("IL stage complete … remaining stages not
wired")` — it never emits a half-built APK, and the wizard falls back to the
PC-build message. New dependencies to add for 3-5: `com.android.tools.smali:smali-dexlib2`
and `com.android.tools.build:apksig`, plus a build step that compiles the two
helper classes to the prebuilt `.dex` asset.

## Verification status

- Compiles: `assembleDebug` BUILD SUCCESSFUL (Kotlin + merged manifest/resources).
- On-device behavior (acquire / verify / install prompt): **corporeality-gated**
  — needs the AYN Thor + a user-supplied original APK; not verified on the build
  host.
