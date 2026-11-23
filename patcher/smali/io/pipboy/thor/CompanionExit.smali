.class public Lio/pipboy/thor/CompanionExit;
.super Ljava/lang/Object;
.implements Ljava/lang/Runnable;
.source "CompanionExit.smali"

# Closes the companion: finishes the Unity activity and removes its task, so
# it neither stays on the bottom screen nor lingers in Recents. Posted to the
# main thread by GameWatch once Fallout 4 has stopped answering.


.method public constructor <init>()V
    .locals 0
    invoke-direct {p0}, Ljava/lang/Object;-><init>()V
    return-void
.end method


# Callable from any thread; the work runs on the main thread.
.method public static request()V
    .locals 3

    :try_start
    new-instance v0, Landroid/os/Handler;
    invoke-static {}, Landroid/os/Looper;->getMainLooper()Landroid/os/Looper;
    move-result-object v1
    invoke-direct {v0, v1}, Landroid/os/Handler;-><init>(Landroid/os/Looper;)V
    new-instance v1, Lio/pipboy/thor/CompanionExit;
    invoke-direct {v1}, Lio/pipboy/thor/CompanionExit;-><init>()V
    invoke-virtual {v0, v1}, Landroid/os/Handler;->post(Ljava/lang/Runnable;)Z
    :try_end
    .catch Ljava/lang/Throwable; {:try_start .. :try_end} :catch

    return-void

    :catch
    move-exception v0
    const-string v1, "strip-boy"
    const-string v2, "could not post companion exit"
    invoke-static {v1, v2, v0}, Landroid/util/Log;->w(Ljava/lang/String;Ljava/lang/String;Ljava/lang/Throwable;)I
    return-void
.end method


.method public run()V
    .locals 3

    :try_start
    sget-object v0, Lcom/unity3d/player/UnityPlayer;->currentActivity:Landroid/app/Activity;
    if-eqz v0, :done
    invoke-virtual {v0}, Landroid/app/Activity;->isFinishing()Z
    move-result v1
    if-nez v1, :done
    invoke-virtual {v0}, Landroid/app/Activity;->finishAndRemoveTask()V
    :done
    :try_end
    .catch Ljava/lang/Throwable; {:try_start .. :try_end} :catch

    return-void

    :catch
    move-exception v0
    const-string v1, "strip-boy"
    const-string v2, "companion exit failed"
    invoke-static {v1, v2, v0}, Landroid/util/Log;->w(Ljava/lang/String;Ljava/lang/String;Ljava/lang/Throwable;)I
    return-void
.end method
