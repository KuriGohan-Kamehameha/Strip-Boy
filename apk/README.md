# apk/

This directory holds **user-supplied** and **build-derived** artifacts.
Nothing in here is committed to git — see the top-level `.gitignore`.

## What goes where

| Path                                        | Role         | Source                                                         |
|---------------------------------------------|--------------|----------------------------------------------------------------|
| `apk/original.apk`                          | **you provide**  | Your personal copy of Bethesda's *Fallout 4 Pip-Boy* v1.2 APK (`com.bethsoft.falloutcompanionapp`, versionCode 9, ~38 MB). |
| `apk/managed/Assembly-CSharp.original.dll`  | regenerated  | Extracted from `original.apk` by `scripts/build.sh`.           |
| `apk/managed/Assembly-CSharp.patched.dll`   | regenerated  | Output of `patcher/` — input DLL with the 3 IL patches applied.|
| `apk/decompiled/`                           | optional ref | `ilspycmd` decompile of the original DLL, used for inspection. |
| `apk/work/`                                 | scratch      | zip + zipalign intermediates.                                  |
| `apk/debug.keystore`                        | generated    | Self-signed debug key for re-signing the patched APK.          |
| `apk/out/pipboy-loopback.apk`               | **deliverable** | Installable APK after `scripts/build.sh` succeeds.          |

## Getting `original.apk`

This project does not (and legally cannot) host Bethesda's APK. If you
installed *Fallout 4 Pip-Boy* on Android while it was on the Play Store
(2015–2018), pull your existing install:

```
adb shell pm path com.bethsoft.falloutcompanionapp
adb pull /data/app/.../base.apk apk/original.apk
```

Otherwise, source a personal-backup copy from a reputable APK archive
(e.g. APKMirror keeps verified historical Bethesda uploads).

The SHA-256 of the v1.2 release APK is recorded in `docs/CHECKSUMS.md`
so you can confirm you have the same build the patcher was developed
against. If yours hashes differently, the patch may still apply — the
patcher is structural — but it's untested against other builds.
