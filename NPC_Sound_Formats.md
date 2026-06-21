# Rust NPC Sound File Formats (Reverse-Engineered)

> By **RazoR**, 2026. Russian version: **[NPC_Sound_Formats.ru.md](NPC_Sound_Formats.ru.md)**.
> Discord: <https://discordapp.com/users/1056019567589216336>
>
> Complete specification of the `.json` sound files used by NPC voice plugins on
> Rust/Oxide/Carbon servers:
> - **HumanNPC** (by *Razor*) — Steam Voice packets
> - **XDQuest** (by *DezLife*) — Ogg Vorbis in base64
>
> This document exists because there was **no public spec** for these formats and every
> "tutorial" online just says *"record it in-game with a microphone"*. Below is the full
> byte-level breakdown so anyone can build a file-based converter.

---

## Quick comparison

| | **HumanNPC** (Razor) | **XDQuest** (DezLife) |
|---|---|---|
| JSON key | `"Data"` | `"voiceData"` + `"audioType"` + `"durationSeconds"` |
| Content | GZip → Steam Voice packets (Opus) | Raw **Ogg Vorbis** file (base64) |
| Audio params | Opus SILK, mono, 24 kHz | Ogg Vorbis, mono, 48 kHz |
| How it plays | Server sends raw packets → client `DecompressVoice` | Plugin decodes Ogg → sends as voice |
| Complexity | Very complex (CRC32, seq, framing) | Simple (just an ogg file in base64) |
| Folder | `data/HumanNPC/Sounds/` | `data/XDQuest/Sounds/` |

---

# Part 1 — XDQuest Format (Simple)

XDQuest stores voice as a standard Ogg Vorbis audio file, base64-encoded in JSON:

```json
{
  "voiceData": "<base64 of Ogg Vorbis file>",
  "audioType": 0,
  "durationSeconds": 4.53
}
```

### Parameters

| Field | Value |
|-------|-------|
| `voiceData` | Base64-encoded Ogg Vorbis file |
| `audioType` | Always `0` |
| `durationSeconds` | Duration in seconds (float) |

The Ogg file inside: **mono, 48000 Hz, Vorbis codec** (quality ~5, ~128 kbps VBR).

### How to generate

```bash
ffmpeg -i input.mp3 -vn -ac 1 -ar 48000 -c:a libvorbis -q:a 5 output.ogg
```

Then: `base64(output.ogg)` → `voiceData`, get duration from ffprobe, write JSON.

That's it — no special framing, no CRC, no Opus. Dead simple.

---

# Part 2 — HumanNPC Format (Complex)

---

## TL;DR — HumanNPC

A HumanNPC sound file is **NOT an audio file** (not Ogg, not raw Opus). It is a
serialized stream of **Steam Voice** network packets — the exact bytes the Rust client
produces with `SteamUser.GetVoice(...)` and consumes with `SteamUser.DecompressVoice(...)`.

```
sound.json  =  { "Data": "<base64>" }
<base64>    =  GZip( voiceChunkBlob )
voiceChunkBlob = [int32 LE len0][int32 LE len1]...[int32 LE lenN]  (one per chunk)
                 ++ chunk0 ++ chunk1 ++ ... ++ chunkN              (the chunk bytes)
each chunk  =  one Steam Voice packet (see below)
```

The single most important and most easily-missed detail: inside each chunk, every Opus
frame is wrapped in a `[u16 length][u16 sequence]` header, and frames must be **single
20 ms Opus frames (TOC code 0)** — not one large multi-frame (code 3) packet.

---

## Layer 1 — The JSON file

The plugin serializes a class with a single property using a custom `JsonConverter`:

```csharp
public class NpcSound
{
    [JsonConverter(typeof(SoundFileConverter))]
    public List<byte[]> Data = new List<byte[]>();
}
```

On disk it looks like:

```json
{ "Data": "H4sIAAAAAAA...base64..." }
```

* The base64 string decodes to **GZip-compressed** data (magic bytes `1F 8B`).
* `List<byte[]>` is the list of *voice chunks* (each `byte[]` is one chunk).

---

## Layer 2 — GZip + chunk container

The plugin's serialization (`ToSaveData`) lays the chunks out like this:

```
[int32 LE: chunk0.Length]
[int32 LE: chunk1.Length]
...
[int32 LE: chunkN.Length]
chunk0 bytes
chunk1 bytes
...
chunkN bytes
```

i.e. **all the lengths first**, then **all the chunk bytes concatenated**. The whole blob
is GZip-compressed, then base64-encoded into the `"Data"` field.

Decoder reference (how the plugin reads it back):

```csharp
// pseudo
byte[] blob = GZipDecompress(Convert.FromBase64String(json.Data));
var sizes = new List<int>();
int offset = 0;
while (true) {
    sizes.Add(BitConverter.ToInt32(blob, offset));
    offset += 4;
    int sum = sizes.Sum();
    if (sum == blob.Length - offset) break;   // header ended, rest is chunk bytes
    if (sum >  blob.Length - offset) throw;   // corrupt
}
foreach (int s in sizes) { chunks.Add(blob[offset .. offset+s]); offset += s; }
```

How the plugin **plays** a chunk: it sends each chunk straight to nearby clients as a raw
Rust network voice message:

```csharp
NetWrite w = Network.Net.sv.StartWrite();
w.PacketID(Network.Message.Type.VoiceData);
w.EntityID(npc.net.ID);
w.BytesWithSize(chunkBytes);     // <-- the chunk = a Steam Voice packet
w.Send(new SendInfo(connection) { priority = Priority.Immediate });
// one chunk every ~0.07s (WaitForSeconds(0.07f))
```

The client feeds those bytes to the Steam voice pipeline → `SteamUser.DecompressVoice`.

---

## Layer 3 — A chunk = a Steam Voice packet

Rust uses Valve's **"steam" voice codec** (`SteamUser.GetVoice` / `DecompressVoice`).
Each chunk is one Steam Voice packet:

```
+----------------------------------------------------------+
| 8 bytes  | SteamID64 of the "speaker" (uint64, LE)        |
+----------------------------------------------------------+
| payloads (one or more, see below)                         |
+----------------------------------------------------------+
| 4 bytes  | CRC32 (IEEE) of everything above (LE)           |
+----------------------------------------------------------+
```

### Payloads

Each payload starts with a 1-byte type, followed by a `u16 LE` value:

| Type | Name        | `u16` meaning            | Followed by                                   |
|------|-------------|--------------------------|-----------------------------------------------|
| `0`  | Silence     | number of silent samples | (nothing)                                     |
| `6`  | OPUS PLC    | byte length of the data  | that many bytes of **Opus PLC data** (Layer 4)|
| `11` | Sample Rate | sample rate (Hz)         | (nothing)                                     |

A typical chunk = `SampleRate(11)` payload, then one or more `OpusPlc(6)` payloads.
The sample rate is **24000 Hz** (Steam Voice "optimal" rate), encoded as `0B C0 5D`
(`0x0B` = type, `0x5DC0` = 24000).

### CRC32

* Standard **CRC32 / IEEE** (polynomial `0xEDB88320`, init `0xFFFFFFFF`, final XOR `0xFFFFFFFF`).
* Computed over the entire packet **except** the last 4 bytes.
* Stored little-endian as the last 4 bytes.
* **The client rejects packets with a wrong CRC** — this is why a "valid Opus" file with no
  CRC plays as total silence.

```csharp
static readonly uint[] T = BuildCrcTable();           // poly 0xEDB88320
static uint Crc32(byte[] d, int len) {
    uint c = 0xFFFFFFFF;
    for (int i = 0; i < len; i++) c = T[(c ^ d[i]) & 0xFF] ^ (c >> 8);
    return c ^ 0xFFFFFFFF;
}
```

---

## Layer 4 — Inside an OPUS PLC payload

This is the part with **no public documentation** and the part everyone gets wrong.

The bytes inside a type-`6` payload are **not** a single Opus packet. They are a sequence
of length-and-sequence-prefixed Opus frames:

```
repeat until payload consumed:
    [u16 LE: frameLength]
    if frameLength == 0xFFFF:   // reset marker: reset decoder state, seq = 0, continue
        continue
    [u16 LE: sequenceNumber]
    [frameLength bytes: one Opus frame]
```

* `sequenceNumber` is a **global, monotonically increasing** counter across the entire
  stream (used for packet-loss concealment). The client tracks the expected sequence:
  * `seq < expected` → reset decoder state
  * `seq > expected` → run PLC (decode `seq - expected` empty frames)
* `0xFFFF` is a special "reset" length marker (no sequence/data follows it).

### Critical: the Opus frames themselves

Real, working files (recorded in-game) use:

| Property        | Value                                   |
|-----------------|-----------------------------------------|
| Opus mode       | **Hybrid**, SuperWideBand (TOC config 13)|
| Frame duration  | **20 ms** (480 samples @ 24 kHz)         |
| TOC code        | **0** (a single frame per Opus packet)   |
| Channels        | Mono                                     |
| Frame size      | ~60–100 bytes each                       |

For a generated file, what matters for **smooth, click-free** playback on the real Rust
client is matching the in-game recorder's codec mode: **Hybrid SuperWideBand (config 13)**.
Notes from experimentation:

* ✅ **Hybrid SWB (config 13), 20 ms, code 0** — what the in-game recorder produces and the
  only mode that is smooth on the real client. Requires a full Opus encoder, e.g. **native
  `libopus` (opus.dll)** via P/Invoke. Settings: `OPUS_APPLICATION_VOIP` +
  `OPUS_SET_BANDWIDTH(SUPERWIDEBAND)` + `OPUS_SET_SIGNAL(VOICE)`.
* ⚠️ **SILK-only WideBand (config 9)** — decodes fine and looks smooth offline, but on the
  real client SILK's inter-frame prediction produces audible **clicks at packet boundaries**.
* ⚠️ **CELT-only (config 27)** — plays, but CELT's transient pre-echo causes a faint
  **periodic stutter** on speech.

What does **NOT** work at all:

* ❌ One big multi-frame packet (40/60 ms, TOC **code 3**) — many decoders/Steam reject it.
* ❌ Raw Opus with no `[len][seq]` wrapper.
* ❌ Wrong/missing CRC32.
* ❌ Plain Ogg Vorbis / Ogg Opus (for HumanNPC — that is the XDQuest format, see Part 1).

Group several frames (≈5 → ~100 ms of audio) into each chunk, matching the in-game
recorder's behavior.

### Packetization matters for REPLAY (critical, easily missed)

Even with byte-perfect audio, the **packet layout** affects in-game playback on *repeated*
plays of the same NPC. With a fresh NPC entity the first play is always clean; subsequent
plays glitch/stutter at a fixed point unless the stream is packetized like the in-game
recorder. Match this layout:

* **First VoiceData packet (chunk): one group of ~8 frames (~160 ms)** — front-loads the
  client jitter buffer.
* **Subsequent chunks: TWO `SampleRate + OpusPlc` groups**, each ~3 frames (so ~6 frames /
  120 ms per chunk). i.e. each packet re-declares the sample rate before each small group.
* **End of stream: a `0xFFFF` reset marker** at the end of the last opus group, then a final
  packet containing only a **`Silence` (type 0) payload (~1500 samples / 62 ms)**.
* Enable Opus **DTX** so pauses encode as 1-byte frames (matches the recorder).

Uniform packing (e.g. a flat 5 frames/chunk in a single group) decodes to identical PCM
offline but causes a reproducible stutter on the 2nd+ play in-game. The grouped layout above
plays cleanly on every replay.

---

## Annotated real example

A chunk from a working `soundname.json` (first 14 bytes + structure):

```
F9 99 12 1D 01 00 10 01   SteamID64 LE = 76561198448024057
0B                        payload type 11 = sample rate
C0 5D                     u16 = 0x5DC0 = 24000 Hz
06                        payload type 6 = OPUS PLC
CA 02                     u16 = 0x02CA = 714 bytes of opus-plc data follow
   3F 00                  frame[0] length = 0x003F = 63
   00 00                  frame[0] sequence = 0
   68 ...(63 bytes)...    Opus frame, TOC 0x68 -> config 13 (Hybrid SWB), code 0, 20ms
   ...                    next [u16 len][u16 seq][frame] ...
<...more frames...>
<4 bytes>                 CRC32 (IEEE, LE) over the whole packet minus these 4 bytes
```

---

## How to build a file from arbitrary audio

End-to-end pipeline that produces a working file:

1. **Decode** input (mp3/wav/flac/…) to PCM: **mono, 24000 Hz, signed 16-bit LE**
   (e.g. `ffmpeg -i in.mp3 -vn -ac 1 -ar 24000 -f s16le -acodec pcm_s16le out.pcm`).
2. **Encode** the PCM to Opus in **20 ms frames** (480 samples), mono, **Hybrid
   SuperWideBand** via native libopus (`OPUS_APPLICATION_VOIP` + `SET_BANDWIDTH(SUPERWIDEBAND)`
   + `SET_SIGNAL(VOICE)`), ~32 kbps VBR. Each `opus_encode` with `frame_size=480` yields a
   single-frame (code 0) packet with TOC config 13.
3. **Wrap** each Opus frame as `[u16 len][u16 seq]` + frame bytes; `seq` increments globally.
4. **Group** ~5 frames, prefix `[0x0B][u16 24000]` (sample rate) and
   `[0x06][u16 innerLen]`, prepend the 8-byte SteamID64, append CRC32 → one chunk.
5. **Serialize** all chunks: lengths-first blob → GZip → base64 → `{"Data":"..."}`.
6. Save as `…/oxide|carbon/data/HumanNPC/Sounds/<name>.json`.

In game: `/npc_edit` → `/npc sound <name>` → `/npc soundonuse true` → `/npc_end`.
(`o.reload HumanNPC` / `carbon.reload HumanNPC` to clear the plugin's sound cache.)

### C# reference (native libopus via P/Invoke)

```csharp
[DllImport("opus")] static extern IntPtr opus_encoder_create(int Fs, int ch, int app, out int err);
[DllImport("opus")] static extern int opus_encode(IntPtr st, short[] pcm, int frameSize, byte[] data, int max);
[DllImport("opus")] static extern int opus_encoder_ctl(IntPtr st, int request, int value);

const int SR = 24000, FRAME = 480, FRAMES_PER_CHUNK = 5;
const ulong STEAM_ID = 76561198448024057UL;            // any valid-looking SteamID64

int err;
IntPtr enc = opus_encoder_create(SR, 1, 2048 /*VOIP*/, out err);
opus_encoder_ctl(enc, 4002, 32000);   // SET_BITRATE
opus_encoder_ctl(enc, 4006, 1);       // SET_VBR
opus_encoder_ctl(enc, 4004, 1104);    // SET_MAX_BANDWIDTH = SUPERWIDEBAND  -> Hybrid SWB (cfg 13)
opus_encoder_ctl(enc, 4008, 1104);    // SET_BANDWIDTH    = SUPERWIDEBAND
opus_encoder_ctl(enc, 4024, 3001);    // SET_SIGNAL       = VOICE
opus_encoder_ctl(enc, 4010, 10);      // SET_COMPLEXITY

// encode all 20ms frames
var frames = new List<byte[]>();
var buf = new byte[4000];
for (int pos = 0; pos + FRAME <= pcm.Length; pos += FRAME) {
    int n = opus_encode(enc, /*pcm slice at pos*/ , FRAME, buf, buf.Length);
    frames.Add(buf[0..n]);
}

// group into Steam Voice packets with [u16 len][u16 seq] inner framing
ushort seq = 0; var chunks = new List<byte[]>();
for (int i = 0; i < frames.Count; i += FRAMES_PER_CHUNK) {
    var inner = new MemoryStream();
    for (int k = 0; k < FRAMES_PER_CHUNK && i + k < frames.Count; k++) {
        var f = frames[i + k];
        inner.Write(BitConverter.GetBytes((ushort)f.Length));   // [u16 len]
        inner.Write(BitConverter.GetBytes(seq++));              // [u16 seq]
        inner.Write(f);                                         // opus frame
    }
    var body = new MemoryStream();
    body.Write(BitConverter.GetBytes(STEAM_ID));                // SteamID64 LE
    body.WriteByte(0x0B); body.Write(BitConverter.GetBytes((ushort)SR));   // sample rate
    body.WriteByte(0x06); body.Write(BitConverter.GetBytes((ushort)inner.Length));
    inner.WriteTo(body);
    var b = body.ToArray();
    chunks.Add(b.Concat(BitConverter.GetBytes(Crc32(b, b.Length))).ToArray()); // + CRC32
}

// container: lengths-first, then bytes -> gzip -> base64 -> {"Data":...}
```

> ℹ️ **Codec choice matters.** A pure-C# encoder (Concentus 1.1.7) can produce CELT or
> SILK-WideBand, both of which *decode* fine but are **not smooth on the real Rust client**
> (CELT = periodic stutter; SILK = boundary clicks). Only **Hybrid SuperWideBand (config 13)**
> from full libopus matches the in-game recorder and plays cleanly. Use native `opus.dll`.

---

## Common failure modes (and why they're silent)

| Symptom                         | Cause                                                        |
|---------------------------------|--------------------------------------------------------------|
| Total silence, file "looks ok"  | Missing/incorrect CRC32 → client drops every packet          |
| Total silence                   | Raw Opus without the `[u16 len][u16 seq]` inner wrapper       |
| Total silence                   | Multi-frame Opus packets (TOC code 3) instead of code 0       |
| Nothing reads the file          | File is Ogg/`{"voiceData":...}` — wrong format entirely       |
| Encoder produced silence        | Concentus 1.1.7 `VOIP`/SILK bug → use `AUDIO` application     |
| Plays but choppy/slow           | Too little audio per chunk vs the 70 ms send interval         |
| Plays but faint periodic stutter| CELT codec on speech — use Hybrid SWB (native libopus)        |
| Plays but clicks at boundaries  | SILK-only codec — use Hybrid SWB (native libopus)            |
| 1st play clean, replays stutter | Uniform 1-group packets — use grouped layout (see above)     |

---

## Credits / sources

* Format confirmed against Rust's decompiled `Facepunch.Steamworks`
  (`SteamUser.GetVoice` / `DecompressVoice`, optimal rate 24000) and the HumanNPC plugin
  source (`SoundFileConverter`, `ToSaveData`, `FromSaveData`, `SendSound`).
* Steam Voice codec framing cross-checked with:
  * **demostf/steam-audio-codec** — a Rust parser for Steam voice packets
    (`https://codeberg.org/demostf/steam-audio-codec`).
  * **"Reversing Steam Voice Codec"** by *Zhenyang Li*
    (`https://zhenyangli.me/posts/reversing-steam-voice-codec/`).
* Opus codec: RFC 6716. Encoder used in the reference converter: **Concentus** (pure C# Opus).

*Content from external sources was paraphrased/summarized for licensing compliance.*
