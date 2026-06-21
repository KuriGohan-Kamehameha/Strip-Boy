# LEDBridge — AYN Thor analog-stick RGB driver.
#
# Single static method, invoked from C# (patched
# AppSettings::set_PipboyEffectColor) on every Pip-Boy effect colour
# change. Writes three Settings.System keys that the AYN HAL reads
# directly:
#
#   joystick_led_light_picker_color  = "#FFrrggbb,#FFrrggbb"  (L,R)
#   joystick_light_enabled           = "1,1"                  (L,R on)
#   led_light_brightness_percent     = max(0.05, dual_screen_brightness_level / 100)
#
# Brightness tracks the BOTTOM screen (dual_screen_brightness_level,
# 0–100) because the Pip-Boy companion lives on the bottom screen on
# the Thor — the LEDs and the screen the user is looking at change
# together. A 0.05 floor stops the LEDs from going fully dark.
#
# Requires WRITE_SETTINGS, granted out-of-band (adb appops or the
# "Modify system settings" toggle in Special access). If not granted,
# Settings.System.canWrite returns false and the method becomes a no-op
# — never throws.

.class public Lio/pipboy/thor/LEDBridge;
.super Ljava/lang/Object;

# Activity is grabbed off UnityPlayer.currentActivity inside the method
# so the C# caller only has to pass (r, g, b) ints — no AndroidJavaObject
# Activity round-trips.

.method public static apply(III)V
    .registers 11
    .param p0, "r"
    .param p1, "g"
    .param p2, "b"

    # Activity activity = UnityPlayer.currentActivity;
    # if (activity == null) return;
    sget-object v0, Lcom/unity3d/player/UnityPlayer;->currentActivity:Landroid/app/Activity;
    if-eqz v0, :done

    # ContentResolver cr = activity.getContentResolver();
    invoke-virtual {v0}, Landroid/content/Context;->getContentResolver()Landroid/content/ContentResolver;
    move-result-object v1

    # if (!Settings.System.canWrite(activity)) return;
    invoke-static {v0}, Landroid/provider/Settings$System;->canWrite(Landroid/content/Context;)Z
    move-result v2
    if-eqz v2, :done

    # hex = String.format("#FF%02X%02X%02X", r, g, b);
    const/4 v2, 0x3
    new-array v3, v2, [Ljava/lang/Object;
    invoke-static {p0}, Ljava/lang/Integer;->valueOf(I)Ljava/lang/Integer;
    move-result-object v4
    const/4 v2, 0x0
    aput-object v4, v3, v2
    invoke-static {p1}, Ljava/lang/Integer;->valueOf(I)Ljava/lang/Integer;
    move-result-object v4
    const/4 v2, 0x1
    aput-object v4, v3, v2
    invoke-static {p2}, Ljava/lang/Integer;->valueOf(I)Ljava/lang/Integer;
    move-result-object v4
    const/4 v2, 0x2
    aput-object v4, v3, v2
    const-string v2, "#FF%02X%02X%02X"
    invoke-static {v2, v3}, Ljava/lang/String;->format(Ljava/lang/String;[Ljava/lang/Object;)Ljava/lang/String;
    move-result-object v2

    # picker = hex + "," + hex;
    new-instance v3, Ljava/lang/StringBuilder;
    invoke-direct {v3}, Ljava/lang/StringBuilder;-><init>()V
    invoke-virtual {v3, v2}, Ljava/lang/StringBuilder;->append(Ljava/lang/String;)Ljava/lang/StringBuilder;
    const-string v4, ","
    invoke-virtual {v3, v4}, Ljava/lang/StringBuilder;->append(Ljava/lang/String;)Ljava/lang/StringBuilder;
    invoke-virtual {v3, v2}, Ljava/lang/StringBuilder;->append(Ljava/lang/String;)Ljava/lang/StringBuilder;
    invoke-virtual {v3}, Ljava/lang/StringBuilder;->toString()Ljava/lang/String;
    move-result-object v3

    # Settings.System.putString(cr, "joystick_led_light_picker_color", picker);
    const-string v4, "joystick_led_light_picker_color"
    invoke-static {v1, v4, v3}, Landroid/provider/Settings$System;->putString(Landroid/content/ContentResolver;Ljava/lang/String;Ljava/lang/String;)Z

    # Settings.System.putString(cr, "joystick_light_enabled", "1,1");
    const-string v4, "joystick_light_enabled"
    const-string v5, "1,1"
    invoke-static {v1, v4, v5}, Landroid/provider/Settings$System;->putString(Landroid/content/ContentResolver;Ljava/lang/String;Ljava/lang/String;)Z

    # int bottom = Settings.System.getInt(cr, "dual_screen_brightness_level", 50);
    const-string v4, "dual_screen_brightness_level"
    const/16 v5, 0x32
    invoke-static {v1, v4, v5}, Landroid/provider/Settings$System;->getInt(Landroid/content/ContentResolver;Ljava/lang/String;I)I
    move-result v6

    # float pct = max(0.05f, bottom / 100.0f);
    int-to-float v6, v6
    const/high16 v7, 0x42c80000   # 100.0f
    div-float v6, v6, v7
    const v7, 0x3d4ccccd          # 0.05f
    invoke-static {v6, v7}, Ljava/lang/Math;->max(FF)F
    move-result v6

    # Settings.System.putFloat(cr, "led_light_brightness_percent", pct);
    const-string v4, "led_light_brightness_percent"
    invoke-static {v1, v4, v6}, Landroid/provider/Settings$System;->putFloat(Landroid/content/ContentResolver;Ljava/lang/String;F)Z

    :done
    return-void
.end method
