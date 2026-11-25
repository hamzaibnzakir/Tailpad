# TailPad protocol (v1)

Phone ⇄ PC over Tailscale. Two channels:

| Channel | Port | Direction | Used for |
|---|---|---|---|
| TCP control | 47800 | both | pairing, settings, macros, games, rumble, foreground game |
| UDP input | 47801 | phone → PC | sticks, buttons, triggers, gyro, mouse — sent ~120×/s |
| UDP DSU | 26760 (localhost) | PC → emulators | cemuhook motion server (Cemu, Dolphin, Ryujinx, Yuzu forks…) |

The PC only accepts connections from the Tailscale range `100.64.0.0/10`, `fd7a:115c:a1e0::/48` and loopback, plus the local network if "Allow Wi-Fi/LAN" is ticked in the tray app.

## Control channel (TCP)

Newline-delimited JSON. Every message has a `t` (type) field.

### Pairing / hello
```jsonc
// phone → PC (first message)
{"t":"hello","v":1,"device":"<stable uuid>","name":"Hamza's iPhone","token":"<saved token or empty>","code":"<6-digit pairing code, first time only>","pad":"x360"|"ds4"}
// PC → phone
{"t":"welcome","token":"<token to save>","key":12345678,"udp":47801,"pc":"DELL-5540","player":1,"pad":"x360"}
{"t":"denied","reason":"Wrong pairing code"}
```
`key` is a random uint32 that must be in every UDP packet of this session.

### Phone → PC
| `t` | Fields | Meaning |
|---|---|---|
| `pad` | `pad`: `x360`/`ds4` | Switch controller type (re-plugs the virtual pad) |
| `macro` | `id`, `steps[]`, `loop` | Run a macro (see below). `loop:true` repeats until `macroStop` |
| `macroStop` | `id` | Stop a looping macro |
| `games` | – | Ask for the game library |
| `launch` | `id` | Launch a game from the library |
| `ping` | `ts` | Latency probe, echoed as `pong` |
| `stream` | `on`, `height`, `fps`, `kbps`, `audio` | Start/stop remote play of the focused game window |
| `bg` | `action`: `launch` (`id`) · `move` · `back` · `setup` | Background play: game on the PC's hidden screen |
| `bye` | – | Clean disconnect |

Macro step: `{"b":<button bits>,"lt":0-1,"rt":0-1,"lx":-1..1,"ly":-1..1,"rx":-1..1,"ry":-1..1,"ms":<hold time>}`.
An empty step (`{"ms":80}`) is a pause. Macro output is merged on top of what your fingers are doing (buttons OR-ed, triggers max, sticks override when non-zero).

### PC → phone
| `t` | Fields | Meaning |
|---|---|---|
| `rumble` | `l`, `s` (0-255) | Large / small motor from the game |
| `light` | `r`,`g`,`b` | DS4 lightbar colour from the game |
| `player` | `n` | Player number (Xbox LED) |
| `games` | `list[]` of `{id,name,source,art}` | Game library (Steam, Epic, custom) |
| `fg` | `exe`, `title` | Game/app in focus on the PC — phone auto-switches layout |
| `pong` | `ts` | Latency reply |
| `info` | `msg` | Message to show on the phone |
| `bg` | `state`: `on` (`game`) · `off` · `waiting` · `needs-setup` · `ready` · `error`, optional `msg` | Background play status |
| `stream` | `state`: `started` (`w`,`h`,`fps`,`encoder`,`target`) · `stats` (`fps`,`kbps`) · `error` (`msg`) · `stopped` | Remote-play status |

## Input packet (UDP, little-endian, 68 bytes)

| Offset | Type | Field |
|---|---|---|
| 0 | u32 | magic `0x31445054` ("TPD1") |
| 4 | u32 | session key |
| 8 | u32 | sequence (wraps) |
| 12 | u32 | button bits |
| 16 | i16×4 | LX, LY, RX, RY (−32767…32767, up = +) |
| 24 | u8×2 | LT, RT (0-255) |
| 26 | u8 | flags: bit0 = motion valid |
| 27 | u8 | reserved |
| 28 | f32×2 | mouse dx, dy (pixels, since last packet) |
| 36 | f32×3 | accel X, Y, Z (g) |
| 48 | f32×3 | gyro pitch, yaw, roll (deg/s) |
| 60 | u64 | motion timestamp (µs) |

Packets with an older sequence number than the last one are dropped. If no packet arrives for 500 ms the pad is centred and all buttons released.

### Button bits
| Bit | Xbox | PlayStation |
|---|---|---|
| 0 | A | Cross |
| 1 | B | Circle |
| 2 | X | Square |
| 3 | Y | Triangle |
| 4 | LB | L1 |
| 5 | RB | R1 |
| 6 | Back | Share |
| 7 | Start | Options |
| 8 | Guide | PS |
| 9 | LS (L3) | L3 |
| 10 | RS (R3) | R3 |
| 11–14 | D-pad Up, Down, Left, Right | same |
| 15 | – | Touchpad click |

## Layout share links

`tailpad://layout?d=<base64url(zlib(JSON))>` — opens in the app and imports the layout. The same JSON can be shared as a `.tailpad` file.
Pairing QR: `tailpad://pair?host=<tailscale ip>&port=47800&code=<code>&pc=<name>`.

## Remote play (UDP, PC → phone)

Sent back to wherever the phone's input packets come from (so no extra port is opened on the phone).
Video is H.264 (Annex B, an Access Unit Delimiter before each frame, SPS/PPS repeated on every keyframe, keyframe every second, no B-frames),
captured with Desktop Duplication cropped to the focused game window and encoded on the GPU (NVENC → Quick Sync → AMF → Media Foundation).

| Packet | Header | Payload |
|---|---|---|
| Video `TPV1` | u32 magic · u32 frame · u16 fragment · u16 fragments · u8 flags (bit0 keyframe) · u8 · u16 · u32 time ms (20 bytes) | ≤1100 bytes of the frame |
| Audio `TPA1` | u32 magic · u32 seq · u32 time ms (12 bytes) | one Opus packet, 10 ms, 48 kHz stereo |

If any fragment of a frame is lost the phone drops frames until the next keyframe (≤1 s) instead of showing smeared video.
