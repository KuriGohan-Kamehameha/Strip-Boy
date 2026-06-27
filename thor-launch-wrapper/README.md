# Thor Launch Wrapper

Green-on-black Android launcher for the AYN Thor same-device Fallout 4 setup.
It starts the patched Strip-Boy companion on the Thor bottom display, starts
GameNative on the top display with the verified GOG Fallout 4 target, then
removes its own task once both apps are handed off.

## Launch Contract

- Companion package: `com.bethsoft.falloutcompanionapp`
- Patched companion marker: `io.pipboy.thor.LauncherActivity`
- GameNative package: `app.gamenative`
- GameNative action: `app.gamenative.LAUNCH_GAME`
- Fallout 4 GOG app id: `1998527297`
- GameNative source extra: `game_source=GOG`
- Bifrost package: `com.moonbench.bifrost`
- Required Bifrost plugin id: `fallout4-pipboy`
- Bifrost plugin query action: `com.moonbench.bifrost.api.ACTION_QUERY_PLUGIN`

The wrapper intentionally checks for the Strip-Boy marker activity rather than
only checking whether Bethesda's original companion package is installed.
It also separates Bifrost readiness into two checks: Bifrost app installed, and
the `fallout4-pipboy` plugin/profile installed inside Bifrost.

When `launch automatically next time onwards` is checked, the wrapper runs in
headless mode on later launches: it verifies the same prerequisites and starts
GameNative + the patched companion without drawing the wizard. If any check
fails, the wizard is shown again with the failing row visible. When launching
manually with that box unchecked, the wrapper primes both Thor stick LEDs to
green at roughly 30% brightness before handing off; the Pip-Boy/Bifrost live
feed can then take over.

## Build

```bash
JAVA_HOME="$(/usr/libexec/java_home -v 17)" ./gradlew assembleDebug --no-daemon
```

Install the debug APK:

```bash
adb install -r app/build/outputs/apk/debug/app-debug.apk
```

ADB-only smoke launch:

```bash
adb shell am start -n com.moonbench.thorlaunch/.MainActivity \
  --ez com.moonbench.thorlaunch.AUTO_LAUNCH true
```

Expected final state:

- Display #0: `app.gamenative/.MainActivity`
- Display #4: `com.bethsoft.falloutcompanionapp/com.unity3d.player.UnityPlayerNativeActivity`
- No live `com.moonbench.thorlaunch` process after handoff

## Attributions

- Strip-Boy companion patch and Thor Launch Wrapper: Kuri
  ([github/KuriGohan-Kamehameha](https://github.com/KuriGohan-Kamehameha)).
- Bifrost: invented and maintained by Pollux, with plugin and project
  contributions from Kuri.
- Fallout, Pip-Boy, and related marks are owned by Bethesda / ZeniMax and are
  referenced descriptively. This project is not affiliated with or endorsed by
  Bethesda / ZeniMax.