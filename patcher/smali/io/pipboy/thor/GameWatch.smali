.class public Lio/pipboy/thor/GameWatch;
.super Ljava/lang/Object;
.implements Ljava/lang/Runnable;
.source "GameWatch.smali"

# Closes the companion after Fallout 4 has gone away.
#
# While the Pip-Boy is on screen, every 5 s this sends the protocol's own
# discovery request ({"cmd":"autodiscover"}) to 127.0.0.1:28000 and waits up
# to 1 s for the game's reply ({"IsBusy":...,"MachineType":...}). The game
# answers that request whether or not a companion is attached, so the probe
# does not touch the TCP session on port 27000.
#
# The watch only arms after the game has answered at least once. If it then
# misses MAX_MISSES probes in a row (about two minutes), CompanionExit
# finishes the Unity task. A game on another machine, a launcher without
# loopback networking, or a Pip-Boy opened before any game never arms it.
#
# All probing runs on its own HandlerThread, never on the Unity or UI thread.
#
# P10 RELAXATIONS: rule 2 — the probe is a self-reposting Handler timer that
# lives as long as the game answers; each run does one bounded probe, and the
# miss counter bounds the no-answer path.

# Constants (inline below): probe interval 5000 ms, receive timeout 1000 ms,
# MAX_MISSES 24, discovery port 28000 (Fallout 4 default, UDP).

.field private static handler:Landroid/os/Handler;
.field private static instance:Lio/pipboy/thor/GameWatch;

# Written on the UI thread, read on the watch thread.
.field private static volatile shown:Z
.field private static volatile misses:I

# Watch thread only (and step(), which runs there).
.field private static seenGame:Z


.method static constructor <clinit>()V
    .locals 3

    new-instance v0, Landroid/os/HandlerThread;
    const-string v1, "strip-boy-gamewatch"
    invoke-direct {v0, v1}, Landroid/os/HandlerThread;-><init>(Ljava/lang/String;)V
    invoke-virtual {v0}, Landroid/os/HandlerThread;->start()V

    new-instance v1, Landroid/os/Handler;
    invoke-virtual {v0}, Landroid/os/HandlerThread;->getLooper()Landroid/os/Looper;
    move-result-object v2
    invoke-direct {v1, v2}, Landroid/os/Handler;-><init>(Landroid/os/Looper;)V
    sput-object v1, Lio/pipboy/thor/GameWatch;->handler:Landroid/os/Handler;

    new-instance v1, Lio/pipboy/thor/GameWatch;
    invoke-direct {v1}, Lio/pipboy/thor/GameWatch;-><init>()V
    sput-object v1, Lio/pipboy/thor/GameWatch;->instance:Lio/pipboy/thor/GameWatch;

    return-void
.end method


.method public constructor <init>()V
    .locals 0
    invoke-direct {p0}, Ljava/lang/Object;-><init>()V
    return-void
.end method


# Pip-Boy came on screen: (re)start probing with a fresh miss count.
.method public static onShown()V
    .locals 2

    const/4 v0, 0x1
    sput-boolean v0, Lio/pipboy/thor/GameWatch;->shown:Z
    const/4 v0, 0x0
    sput v0, Lio/pipboy/thor/GameWatch;->misses:I

    sget-object v0, Lio/pipboy/thor/GameWatch;->handler:Landroid/os/Handler;
    sget-object v1, Lio/pipboy/thor/GameWatch;->instance:Lio/pipboy/thor/GameWatch;
    invoke-virtual {v0, v1}, Landroid/os/Handler;->removeCallbacks(Ljava/lang/Runnable;)V
    invoke-virtual {v0, v1}, Landroid/os/Handler;->post(Ljava/lang/Runnable;)Z
    return-void
.end method


# Pip-Boy left the screen: stop probing. A paused companion is never closed.
.method public static onHidden()V
    .locals 2

    const/4 v0, 0x0
    sput-boolean v0, Lio/pipboy/thor/GameWatch;->shown:Z

    sget-object v0, Lio/pipboy/thor/GameWatch;->handler:Landroid/os/Handler;
    sget-object v1, Lio/pipboy/thor/GameWatch;->instance:Lio/pipboy/thor/GameWatch;
    invoke-virtual {v0, v1}, Landroid/os/Handler;->removeCallbacks(Ljava/lang/Runnable;)V
    return-void
.end method


# Fold one probe result into the state. Returns true when the companion
# should close: the game answered before and has now missed MAX_MISSES
# probes in a row.
.method static step(Z)Z
    .locals 2
    .param p0, "answered"

    if-eqz p0, :missed
    const/4 v0, 0x1
    sput-boolean v0, Lio/pipboy/thor/GameWatch;->seenGame:Z
    const/4 v0, 0x0
    sput v0, Lio/pipboy/thor/GameWatch;->misses:I
    return v0

    :missed
    sget-boolean v0, Lio/pipboy/thor/GameWatch;->seenGame:Z
    if-eqz v0, :not_armed

    sget v0, Lio/pipboy/thor/GameWatch;->misses:I
    add-int/lit8 v0, v0, 0x1
    sput v0, Lio/pipboy/thor/GameWatch;->misses:I
    const/16 v1, 0x18
    if-lt v0, v1, :not_armed
    const/4 v0, 0x1
    return v0

    :not_armed
    const/4 v0, 0x0
    return v0
.end method


# One discovery round trip. True only for a reply that looks like the game's.
.method static probe()Z
    .locals 6

    const/4 v0, 0x0

    :try_start
    # Construct into v1 and only then move to v0, so the catch handler never
    # sees an uninitialised reference in v0.
    new-instance v1, Ljava/net/DatagramSocket;
    invoke-direct {v1}, Ljava/net/DatagramSocket;-><init>()V
    move-object v0, v1
    const/16 v1, 0x3e8
    invoke-virtual {v0, v1}, Ljava/net/DatagramSocket;->setSoTimeout(I)V

    const-string v1, "{\"cmd\":\"autodiscover\"}\n"
    const-string v2, "UTF-8"
    invoke-virtual {v1, v2}, Ljava/lang/String;->getBytes(Ljava/lang/String;)[B
    move-result-object v1

    const-string v2, "127.0.0.1"
    invoke-static {v2}, Ljava/net/InetAddress;->getByName(Ljava/lang/String;)Ljava/net/InetAddress;
    move-result-object v2

    array-length v4, v1
    const/16 v5, 0x6d60
    new-instance v3, Ljava/net/DatagramPacket;
    invoke-direct {v3, v1, v4, v2, v5}, Ljava/net/DatagramPacket;-><init>([BILjava/net/InetAddress;I)V
    invoke-virtual {v0, v3}, Ljava/net/DatagramSocket;->send(Ljava/net/DatagramPacket;)V

    const/16 v4, 0x200
    new-array v1, v4, [B
    new-instance v3, Ljava/net/DatagramPacket;
    invoke-direct {v3, v1, v4}, Ljava/net/DatagramPacket;-><init>([BI)V
    invoke-virtual {v0, v3}, Ljava/net/DatagramSocket;->receive(Ljava/net/DatagramPacket;)V

    invoke-virtual {v3}, Ljava/net/DatagramPacket;->getLength()I
    move-result v4
    const/4 v5, 0x0
    const-string v3, "UTF-8"
    new-instance v2, Ljava/lang/String;
    invoke-direct {v2, v1, v5, v4, v3}, Ljava/lang/String;-><init>([BIILjava/lang/String;)V

    const-string v3, "MachineType"
    invoke-virtual {v2, v3}, Ljava/lang/String;->contains(Ljava/lang/CharSequence;)Z
    move-result v1

    invoke-virtual {v0}, Ljava/net/DatagramSocket;->close()V
    return v1
    :try_end
    .catch Ljava/lang/Throwable; {:try_start .. :try_end} :catch

    :catch
    move-exception v1
    if-eqz v0, :no_socket
    invoke-virtual {v0}, Ljava/net/DatagramSocket;->close()V
    :no_socket
    const/4 v0, 0x0
    return v0
.end method


.method public run()V
    .locals 4

    sget-boolean v0, Lio/pipboy/thor/GameWatch;->shown:Z
    if-eqz v0, :done

    invoke-static {}, Lio/pipboy/thor/GameWatch;->probe()Z
    move-result v0
    invoke-static {v0}, Lio/pipboy/thor/GameWatch;->step(Z)Z
    move-result v0
    if-eqz v0, :again

    const-string v0, "strip-boy"
    const-string v1, "Fallout 4 stopped answering discovery; closing the Pip-Boy"
    invoke-static {v0, v1}, Landroid/util/Log;->i(Ljava/lang/String;Ljava/lang/String;)I
    invoke-static {}, Lio/pipboy/thor/CompanionExit;->request()V
    return-void

    :again
    # Hidden while the probe was in flight: stay stopped.
    sget-boolean v0, Lio/pipboy/thor/GameWatch;->shown:Z
    if-eqz v0, :done
    sget-object v0, Lio/pipboy/thor/GameWatch;->handler:Landroid/os/Handler;
    const-wide/16 v2, 0x1388
    invoke-virtual {v0, p0, v2, v3}, Landroid/os/Handler;->postDelayed(Ljava/lang/Runnable;J)Z

    :done
    return-void
.end method
