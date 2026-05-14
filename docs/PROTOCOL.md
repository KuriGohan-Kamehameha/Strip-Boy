# Fallout 4 Gameplay Companion Protocol

Reference doc for understanding what the patch is doing. This project
does **not** reimplement the protocol — Bethesda's APK speaks it natively
and we only adjust *who it tries to talk to*. Kept here so future
maintenance work has the wire format in one place.

Sources:

- Bethesda's `Fallout 4 Companion` Android APK, v1.2 (`com.bethsoft.pipboy`),
  decompiled via `ilspycmd` against `Assembly-CSharp.dll`.
- Community documentation, originally published mid-2015 by `matt0xFF` /
  `rschlaikjer` and refined by the `OpenPipBoy` and `Pip4Mob` projects.

The protocol has two transports: **UDP for discovery** and **TCP for the
gameplay channel**. Default ports are `28000` (UDP) and `27000` (TCP).
Both are configurable via `Fallout4Custom.ini`.

---

## 1. Discovery (UDP, port 28000)

Client broadcasts on UDP to `<lan-broadcast>:28000`:

```json
{"cmd":"autodiscover"}
```

Followed by `\n` (single LF, no null terminator). Payload is 24 bytes.

Server (Fallout 4) replies with UDP from `:28000`:

```json
{"IsBusy":false,"MachineType":"PC"}
```

Possible values:

| Field         | Type | Values                       | Meaning                         |
|---------------|------|------------------------------|---------------------------------|
| `IsBusy`      | bool | `true` / `false`             | Another companion already attached |
| `MachineType` | str  | `"PC"`, `"XB1"`, `"PS4"`     | Game platform                   |

**Patched behavior**: in addition to the original broadcast Send, the
patched `SocketDiscoveryChannel.CoreInitialize` also unicast-sends the
same `{"cmd":"autodiscover"}` payload to `127.0.0.1:28000`. The game,
listening on `0.0.0.0:28000` inside GameNative's Wine prefix, receives via
the kernel's loopback path and replies — so a `127.0.0.1` entry surfaces
in the existing device-list UI alongside any LAN responders.

---

## 2. Session lifecycle (TCP, port 27000)

```
Client                                  Server (Fallout 4)
  │                                        │
  │  TCP SYN ─────────────────────────────►│
  │◄──────────────────────────────── SYN/ACK
  │                                        │
  │  Connect (type=0x01) ─────────────────►│
  │◄───────────────────────────── Connect ack (type=0x01)
  │                                        │
  │◄──────────────── Full data dump (type=0x03, large)
  │◄──────────────── Map data (type=0x05)
  │                                        │
  │  KeepAlive (type=0x00) ───────────────►│   every 1s
  │◄─────────────────── Update (type=0x04) every ~250ms
  │                                        │
  │  Command (type=0x06) ─────────────────►│   on user action
  │◄─────────────────── Command ack (type=0x06)
  │                                        │
  │  TCP FIN ─────────────────────────────►│
```

Either side may drop without a clean close; client retries every 5s in
`LOOPBACK` mode, every 30s in `LAN` mode.

---

## 3. Frame format

All TCP frames share a 5-byte header:

```
 0               1               2               3
 0 1 2 3 4 5 6 7 0 1 2 3 4 5 6 7 0 1 2 3 4 5 6 7 0 1 2 3 4 5 6 7
+---------------+---------------+---------------+---------------+
|                       length (uint32 LE)                      |
+---------------+---------------+---------------+---------------+
|     type      |                payload (length bytes)         |
+---------------+ ... ─────────────────────────────────────►
```

- `length` is **payload size only**, not including the 5-byte header. Range
  is 0..2^32-1 but realistic frames cap around 256 KB (the initial full
  data dump).
- `type` selects the frame kind.

### Frame types

| ID   | Name        | Direction | Payload                                |
|------|-------------|-----------|----------------------------------------|
| 0x00 | KeepAlive   | C→S       | empty (length=0)                       |
| 0x01 | Connect     | both      | empty (length=0); server echoes back   |
| 0x03 | DataFull    | S→C       | full PipBoy state, as a `node` tree    |
| 0x04 | DataUpdate  | S→C       | sequence of (id, value) delta updates  |
| 0x05 | MapUpdate   | S→C       | map metadata + extent                  |
| 0x06 | Command     | both      | UTF-8 JSON envelope `{"type":..,"args":..,"id":..}` |

Anything other than these is an error. Bethesda's parser closes the socket
on unknown frame types.

---

## 4. The binary node tree (`DataFull` / `DataUpdate` payloads)

Pip-Boy data is a tree of **typed nodes**. Each node has:

```
+--------+-----------------------+--------+
|  type  |    value (variable)   |   id   |
| uint8  |                       | uint32 |
+--------+-----------------------+--------+
```

Order is `type, value, id`. The `id` is a stable handle the server uses
in `DataUpdate` frames; a `DataFull` registers every id, and `DataUpdate`
replaces values by id.

### Node types

| ID   | Name     | Value encoding                                                 |
|------|----------|----------------------------------------------------------------|
| 0x00 | Bool     | uint8 (`0`/`1`)                                                |
| 0x01 | Int8     | int8                                                           |
| 0x02 | UInt8    | uint8                                                          |
| 0x03 | Int32    | int32 LE                                                       |
| 0x04 | UInt32   | uint32 LE                                                      |
| 0x05 | Float    | float32 LE (IEEE-754)                                          |
| 0x06 | String   | null-terminated UTF-8                                          |
| 0x07 | List     | uint16 LE count, then `count` × uint32 LE child-id refs        |
| 0x08 | Dict     | uint16 LE count, then `count` × { uint32 LE child-id, cstring name } |

(Some community docs label 0x07 "Array" and 0x08 "Object"; same wire.)

Notes:

- **`String`** is C-string-style: bytes up to and including a `\0`. Empty
  string is the single byte `0x00`.
- **`List` / `Dict`** values are *references*, not nested encodings. The
  full tree is a flat sequence of `(type, value, id)` records; lists and
  dicts contain ids that point to other records in the same frame.
- **`DataFull`** is itself a single record at the root — usually a Dict
  with id `0`. The "id" of the root is conventionally 0.
- **`DataUpdate`** frames are a concatenation of `(type, value, id)`
  records. The client looks up `id` in its registry and replaces the
  node's value. New ids are added.
- **Removals** are signalled by `List` and `Dict` whose count differs
  from the previous full state — any id no longer referenced is reaped.

### Worked example

A status block might look like:

```
Full dump:
  id=0, Dict count=2 { 1:"Status", 2:"Inventory" }
  id=1, Dict count=2 { 10:"HP", 11:"AP" }
  id=10, Float 95.0
  id=11, Float 84.0
  id=2, List count=0
```

An update — player took damage:

```
DataUpdate:
  type=Float, value=87.0, id=10
```

The client now shows HP=87.

---

## 5. Command channel (type 0x06)

Player actions go to the game as JSON-in-binary-frame:

```
length = byte_len(payload)
type   = 0x06
payload = UTF-8 JSON
```

The payload shape:

```json
{"type": <int>, "args": <object>, "id": <int>}
```

The server replies with a `0x06` frame whose JSON has `{"id":<same>,
"allowed":<bool>}`.

### Documented command types

| `type` | Name             | `args` shape                              |
|--------|------------------|-------------------------------------------|
| 0      | UseItem          | `{"handleId":<u32>, "stackId":<u32>}`     |
| 1      | DropItem         | `{"handleId":<u32>, "stackId":<u32>, "count":<u16>}` |
| 2      | ToggleComponent  | `{"componentId":<u32>}`                   |
| 3      | SortMode         | `{"tab":<int>, "mode":<int>}`             |
| 4      | ToggleQuestActive| `{"questId":<u32>}`                       |
| 5      | SetCustomMarker  | `{"x":<float>, "y":<float>}`              |
| 6      | RemoveCustomMarker| `{}`                                     |
| 7      | CheckFastTravel  | `{"markerId":<u32>}`                      |
| 8      | FastTravel       | `{"markerId":<u32>}`                      |
| 9      | PlayRadio        | `{"radioId":<u32>, "play":<bool>}`        |
| 10     | RequestLocalMap  | `{}`                                      |
| 11     | Login            | `{"lang":<string>}`                       |

`Login` is sent immediately after the Connect handshake. The original app
always sends `{"lang":"en"}`.

---

## 6. Map data (type 0x05)

Sent once after Login and again whenever the player changes worldspace
(entering an interior, etc).

Payload:

```
+----------------+----------------+----------------+
| nw_x  float32  | nw_y  float32  | ne_x  float32  |
+----------------+----------------+----------------+
| ne_y  float32  | sw_x  float32  | sw_y  float32  |
+----------------+----------------+----------------+
| se_x  float32  | se_y  float32  |
+----------------+----------------+
| name  cstring  |                                  |
+----------------+----------------+
```

The four corners are world-space coordinates of the map tile. `name` is
the worldspace formid name (e.g. `"Commonwealth"`).

Map tile imagery itself ships in `Data\Interface\Pipboy\PipboyMap.swf` on
the game side; the protocol does not stream it. The companion app loads
its own pre-baked map asset and uses these corners to project markers.

---

## 7. Discovery + loopback

For the AYN Thor + GameNative same-device case, UDP broadcast doesn't
always reach the game's listener — Wine inherits the host's network
namespace but Android's broadcast routing depends on which interface is
"up" and whether the Wine prefix is bound to it. Loopback, on the other
hand, is always available and always reliable.

The patch keeps the original broadcast *and* adds a unicast send to
`127.0.0.1:28000`. Both go out on the same `UdpClient`, both replies are
caught by the same `BeginReceive` callback. From the UI's perspective
nothing has changed except that the loopback entry now consistently
appears in the discovered-device list.

See `apk/decompiled/SocketDiscoveryChannel.cs` (post-`build.sh`) for the
patched method's C# round-trip, and `patcher/Program.cs` for the exact
IL inserted.

---

## 8. References for protocol verification

After running `./scripts/build.sh` once, the full decompiled source tree
of the original DLL is at `apk/decompiled/`. Key files:

- `apk/decompiled/SocketDiscoveryChannel.cs` — UDP discovery (the patched method)
- `apk/decompiled/SocketNetworkChannel.cs`   — TCP connect + send/receive
- `apk/decompiled/RemoteDeviceDescription.cs` — autodiscover-reply parser
- `apk/decompiled/NetworkChannelBase.cs`     — frame loop, KeepAlive
- `apk/decompiled/DataProtocolParser.cs`     — binary node tree codec
- `apk/decompiled/PipboyDataManager.cs`      — full-dump + delta apply

If a wire detail in this doc diverges from the decompile, **the decompile
wins**; this doc gets a PR to match.
