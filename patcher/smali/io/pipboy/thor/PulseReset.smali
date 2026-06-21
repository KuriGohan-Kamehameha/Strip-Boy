.class public Lio/pipboy/thor/PulseReset;
.super Ljava/lang/Object;
.implements Ljava/lang/Runnable;

# Runnable posted by LEDBridge.menuPulse() to return the sticks to the
# resting PIPBOY brightness ~180ms after a menu-navigation brightness pop.
# Kept as a tiny standalone class so menuPulse can use Handler.postDelayed
# without an anonymous inner class.

.source "PulseReset.smali"


.method public constructor <init>()V
    .registers 1
    invoke-direct {p0}, Ljava/lang/Object;-><init>()V
    return-void
.end method


.method public run()V
    .registers 2
    # resting intensity ~77/255 (matches LEDBridge.sendBifrostDisplay)
    const/16 v0, 0x4d
    invoke-static {v0}, Lio/pipboy/thor/LEDBridge;->pipboyAt(I)V
    return-void
.end method
