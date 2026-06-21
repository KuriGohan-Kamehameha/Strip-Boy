# LEDBridge — AYN Thor analog-stick RGB driver.
#
# Drives the SN3112L (left) and SN3112R (right) LED controllers
# directly via /sys/class/sn3112{l,r}/led/brightness. The sysfs
# nodes are world-writable (rw-rw-rw-) on AYN Thor stock firmware,
# so no privileged permission is required. This is the same path
# Moonbench's Bifrost LED utility uses — a regular installable app
# with no special perms, just sysfs writes formatted as:
#
#   /sys/class/sn3112l/led/brightness  ← "1-R:G:B:A"   (left)
#   /sys/class/sn3112r/led/brightness  ← "2-R:G:B:A"   (right)
#
# where R/G/B are 0..255 colour channels and A is 0..255 intensity.
# The "1-" / "2-" prefix is a per-stick index the kernel driver
# parses; both nodes share the same parser.
#
# Why NOT Settings.System.joystick_led_light_picker_color: AYN's
# vendor SettingsProvider rejects writes to those keys from non-
# privileged UIDs across every public + hidden API path we tested
# (Settings.System.putString, .putStringForUser, .insert via
# ContentResolver, even with WRITE_SETTINGS + WRITE_SECURE_SETTINGS
# pre-granted via `pm grant`). Direct sysfs IS the only path that
# works from a regular app — and it's also what Bifrost uses.
#
# Brightness (A) is mapped from dual_screen_brightness_level
# (0..100, the bottom-screen slider) with a 70 % ceiling and a
# 5 % floor so the LEDs match the user's screen but never wash
# out or disappear:
#
#   A = max(13, (bottom * 178) / 100)        # 178 = 0.70 * 255, 13 = 0.05 * 255
#
# Skip-if-unchanged: the static last(R|G|B|A) fields gate the
# four-channel comparison. Idle 30 Hz ticks of the same colour
# collapse to four int compares each; nothing reaches sysfs.

.class public Lio/pipboy/thor/LEDBridge;
.super Ljava/lang/Object;

.field private static lastR:I
.field private static lastG:I
.field private static lastB:I
.field private static lastAlpha:I

.method static constructor <clinit>()V
    .registers 1
    const/4 v0, -0x1
    sput v0, Lio/pipboy/thor/LEDBridge;->lastR:I
    sput v0, Lio/pipboy/thor/LEDBridge;->lastG:I
    sput v0, Lio/pipboy/thor/LEDBridge;->lastB:I
    sput v0, Lio/pipboy/thor/LEDBridge;->lastAlpha:I
    return-void
.end method

.method public static apply(III)V
    .registers 16
    .param p0, "r"
    .param p1, "g"
    .param p2, "b"

    :try_start
    # Activity activity = UnityPlayer.currentActivity;  bail if null
    sget-object v0, Lcom/unity3d/player/UnityPlayer;->currentActivity:Landroid/app/Activity;
    if-eqz v0, :done

    # cr = activity.getContentResolver();
    invoke-virtual {v0}, Landroid/content/Context;->getContentResolver()Landroid/content/ContentResolver;
    move-result-object v1

    # bottom = Settings.System.getInt(cr, "dual_screen_brightness_level", 50);
    const-string v2, "dual_screen_brightness_level"
    const/16 v3, 0x32
    invoke-static {v1, v2, v3}, Landroid/provider/Settings$System;->getInt(Landroid/content/ContentResolver;Ljava/lang/String;I)I
    move-result v4

    # alpha = max(13, (bottom * 178) / 100);  capped naturally at 178 since bottom ≤ 100
    mul-int/lit16 v5, v4, 0xb2
    div-int/lit8 v5, v5, 0x64
    const/16 v6, 0xd
    invoke-static {v5, v6}, Ljava/lang/Math;->max(II)I
    move-result v5

    # Dedupe on (r, g, b, alpha).
    sget v6, Lio/pipboy/thor/LEDBridge;->lastR:I
    if-ne p0, v6, :differ
    sget v6, Lio/pipboy/thor/LEDBridge;->lastG:I
    if-ne p1, v6, :differ
    sget v6, Lio/pipboy/thor/LEDBridge;->lastB:I
    if-ne p2, v6, :differ
    sget v6, Lio/pipboy/thor/LEDBridge;->lastAlpha:I
    if-eq v5, v6, :done

    :differ
    sput p0, Lio/pipboy/thor/LEDBridge;->lastR:I
    sput p1, Lio/pipboy/thor/LEDBridge;->lastG:I
    sput p2, Lio/pipboy/thor/LEDBridge;->lastB:I
    sput v5, Lio/pipboy/thor/LEDBridge;->lastAlpha:I

    # Diagnostic log
    const-string v6, "strip-boy"
    new-instance v7, Ljava/lang/StringBuilder;
    invoke-direct {v7}, Ljava/lang/StringBuilder;-><init>()V
    const-string v8, "apply r="
    invoke-virtual {v7, v8}, Ljava/lang/StringBuilder;->append(Ljava/lang/String;)Ljava/lang/StringBuilder;
    invoke-virtual {v7, p0}, Ljava/lang/StringBuilder;->append(I)Ljava/lang/StringBuilder;
    const-string v8, " g="
    invoke-virtual {v7, v8}, Ljava/lang/StringBuilder;->append(Ljava/lang/String;)Ljava/lang/StringBuilder;
    invoke-virtual {v7, p1}, Ljava/lang/StringBuilder;->append(I)Ljava/lang/StringBuilder;
    const-string v8, " b="
    invoke-virtual {v7, v8}, Ljava/lang/StringBuilder;->append(Ljava/lang/String;)Ljava/lang/StringBuilder;
    invoke-virtual {v7, p2}, Ljava/lang/StringBuilder;->append(I)Ljava/lang/StringBuilder;
    const-string v8, " alpha="
    invoke-virtual {v7, v8}, Ljava/lang/StringBuilder;->append(Ljava/lang/String;)Ljava/lang/StringBuilder;
    invoke-virtual {v7, v5}, Ljava/lang/StringBuilder;->append(I)Ljava/lang/StringBuilder;
    const-string v8, " bottom="
    invoke-virtual {v7, v8}, Ljava/lang/StringBuilder;->append(Ljava/lang/String;)Ljava/lang/StringBuilder;
    invoke-virtual {v7, v4}, Ljava/lang/StringBuilder;->append(I)Ljava/lang/StringBuilder;
    invoke-virtual {v7}, Ljava/lang/StringBuilder;->toString()Ljava/lang/String;
    move-result-object v7
    invoke-static {v6, v7}, Landroid/util/Log;->i(Ljava/lang/String;Ljava/lang/String;)I

    # Left stick:  "1-r:g:b:alpha"  →  /sys/class/sn3112l/led/brightness
    const/4 v6, 0x1
    invoke-static {v6, p0, p1, p2, v5}, Lio/pipboy/thor/LEDBridge;->buildPayload(IIIII)Ljava/lang/String;
    move-result-object v7
    const-string v6, "/sys/class/sn3112l/led/brightness"
    invoke-static {v6, v7}, Lio/pipboy/thor/LEDBridge;->writeFile(Ljava/lang/String;Ljava/lang/String;)V

    # Right stick: "2-r:g:b:alpha"  →  /sys/class/sn3112r/led/brightness
    const/4 v6, 0x2
    invoke-static {v6, p0, p1, p2, v5}, Lio/pipboy/thor/LEDBridge;->buildPayload(IIIII)Ljava/lang/String;
    move-result-object v7
    const-string v6, "/sys/class/sn3112r/led/brightness"
    invoke-static {v6, v7}, Lio/pipboy/thor/LEDBridge;->writeFile(Ljava/lang/String;Ljava/lang/String;)V

    :done
    :try_end
    .catch Ljava/lang/Throwable; {:try_start .. :try_end} :catch
    return-void

    :catch
    move-exception v0
    const-string v1, "strip-boy"
    const-string v2, "apply threw"
    invoke-static {v1, v2, v0}, Landroid/util/Log;->w(Ljava/lang/String;Ljava/lang/String;Ljava/lang/Throwable;)I
    return-void
.end method

# Build "<idx>-<r>:<g>:<b>:<a>" string (Bifrost's wire format).
.method private static buildPayload(IIIII)Ljava/lang/String;
    .registers 8
    .param p0, "idx"
    .param p1, "r"
    .param p2, "g"
    .param p3, "b"
    .param p4, "a"

    new-instance v0, Ljava/lang/StringBuilder;
    invoke-direct {v0}, Ljava/lang/StringBuilder;-><init>()V
    invoke-virtual {v0, p0}, Ljava/lang/StringBuilder;->append(I)Ljava/lang/StringBuilder;
    const-string v1, "-"
    invoke-virtual {v0, v1}, Ljava/lang/StringBuilder;->append(Ljava/lang/String;)Ljava/lang/StringBuilder;
    invoke-virtual {v0, p1}, Ljava/lang/StringBuilder;->append(I)Ljava/lang/StringBuilder;
    const-string v1, ":"
    invoke-virtual {v0, v1}, Ljava/lang/StringBuilder;->append(Ljava/lang/String;)Ljava/lang/StringBuilder;
    invoke-virtual {v0, p2}, Ljava/lang/StringBuilder;->append(I)Ljava/lang/StringBuilder;
    invoke-virtual {v0, v1}, Ljava/lang/StringBuilder;->append(Ljava/lang/String;)Ljava/lang/StringBuilder;
    invoke-virtual {v0, p3}, Ljava/lang/StringBuilder;->append(I)Ljava/lang/StringBuilder;
    invoke-virtual {v0, v1}, Ljava/lang/StringBuilder;->append(Ljava/lang/String;)Ljava/lang/StringBuilder;
    invoke-virtual {v0, p4}, Ljava/lang/StringBuilder;->append(I)Ljava/lang/StringBuilder;
    invoke-virtual {v0}, Ljava/lang/StringBuilder;->toString()Ljava/lang/String;
    move-result-object v0
    return-object v0
.end method

# Open /sys path, write the bytes, close. Catches Throwable so a
# permission denial or SELinux block doesn't kill the caller.
.method private static writeFile(Ljava/lang/String;Ljava/lang/String;)V
    .registers 6
    .param p0, "path"
    .param p1, "content"

    :try_start
    new-instance v0, Ljava/io/FileOutputStream;
    invoke-direct {v0, p0}, Ljava/io/FileOutputStream;-><init>(Ljava/lang/String;)V
    invoke-virtual {p1}, Ljava/lang/String;->getBytes()[B
    move-result-object v1
    invoke-virtual {v0, v1}, Ljava/io/FileOutputStream;->write([B)V
    invoke-virtual {v0}, Ljava/io/FileOutputStream;->close()V

    const-string v1, "strip-boy"
    new-instance v2, Ljava/lang/StringBuilder;
    invoke-direct {v2}, Ljava/lang/StringBuilder;-><init>()V
    const-string v3, "write OK "
    invoke-virtual {v2, v3}, Ljava/lang/StringBuilder;->append(Ljava/lang/String;)Ljava/lang/StringBuilder;
    invoke-virtual {v2, p0}, Ljava/lang/StringBuilder;->append(Ljava/lang/String;)Ljava/lang/StringBuilder;
    const-string v3, " = "
    invoke-virtual {v2, v3}, Ljava/lang/StringBuilder;->append(Ljava/lang/String;)Ljava/lang/StringBuilder;
    invoke-virtual {v2, p1}, Ljava/lang/StringBuilder;->append(Ljava/lang/String;)Ljava/lang/StringBuilder;
    invoke-virtual {v2}, Ljava/lang/StringBuilder;->toString()Ljava/lang/String;
    move-result-object v2
    invoke-static {v1, v2}, Landroid/util/Log;->i(Ljava/lang/String;Ljava/lang/String;)I
    return-void
    :try_end
    .catch Ljava/lang/Throwable; {:try_start .. :try_end} :catch
    :catch
    move-exception v0
    const-string v1, "strip-boy"
    new-instance v2, Ljava/lang/StringBuilder;
    invoke-direct {v2}, Ljava/lang/StringBuilder;-><init>()V
    const-string v3, "write FAIL "
    invoke-virtual {v2, v3}, Ljava/lang/StringBuilder;->append(Ljava/lang/String;)Ljava/lang/StringBuilder;
    invoke-virtual {v2, p0}, Ljava/lang/StringBuilder;->append(Ljava/lang/String;)Ljava/lang/StringBuilder;
    const-string v3, ": "
    invoke-virtual {v2, v3}, Ljava/lang/StringBuilder;->append(Ljava/lang/String;)Ljava/lang/StringBuilder;
    invoke-virtual {v0}, Ljava/lang/Throwable;->getMessage()Ljava/lang/String;
    move-result-object v3
    invoke-virtual {v2, v3}, Ljava/lang/StringBuilder;->append(Ljava/lang/String;)Ljava/lang/StringBuilder;
    invoke-virtual {v2}, Ljava/lang/StringBuilder;->toString()Ljava/lang/String;
    move-result-object v2
    invoke-static {v1, v2}, Landroid/util/Log;->w(Ljava/lang/String;Ljava/lang/String;)I
    return-void
.end method
