# Copyright & interoperability scope

This project is a **clean-room reimplementation** of the Fallout 4 Gameplay
Companion protocol. None of Bethesda's code is copied here.

## What's in scope

- The **wire protocol** — packet framing, type tags, message kinds. Wire
  protocols are not copyrightable, and reverse-engineering for the purpose
  of interoperability is explicitly protected under DMCA §1201(f) and
  established case law (*Sega v. Accolade*, *Sony v. Connectix*).
- New, original UI code that achieves a similar look-and-feel using
  generic CRT/terminal aesthetics (scan lines, green phosphor, monospaced
  fonts). The aesthetic is genre — *Alien*, *War Games*, *Tron* all did it.

## What's out of scope and must come from the user's own copy

- **Original Bethesda assets** — the "Pip-Boy" logo, the Vault Boy art, the
  in-game UI textures, the radio audio. If you decompile the original APK
  into `apk/`, those assets stay in that directory and are **not** copied
  into the build. The build references the user-supplied APK at install
  time and pulls fonts/sprites only if present.
- **Trademarks** — "Pip-Boy", "Vault-Tec", "Fallout" are Bethesda/ZeniMax
  marks. This app's user-facing name is "PipBoy Thor" only for personal
  installation; do not redistribute under any Bethesda-resembling name.

## Why decompile?

Two reasons:

1. **Validate the protocol.** Community docs of the F4 gameplay companion
   protocol are excellent (see `docs/PROTOCOL.md` for the sources). The
   decompiled app is the ground truth — we check our codec against the
   binary's actual parser/serializer.
2. **Confirm UI layout.** We're not copying the original's UI code, but
   replicating its information architecture (which tabs, which sub-views,
   how data maps to widgets) is fair interop work.

## Assets we ship

- The MIT-licensed [Monocraft](https://github.com/IdreesInc/Monocraft) font
  as a stand-in for the original "Monofonto" (which is what Bethesda used
  internally; the app shipped with a custom variant). Stand-in only.
- All UI drawables are original SVGs in `app/src/main/res/drawable/`.

## Your copies

This project assumes you own:

- A legitimate copy of **Fallout 4** (Steam, GOG, disc — anything you
  legally licensed).
- Optionally: a personal backup of the **original companion APK** you
  installed when it was on the Play Store. Drop it at `apk/original.apk`
  if you want decompile-driven verification.

If you don't own both, stop and acquire them legitimately first.
