# TailPad

Turn your iPhone into a game controller for your Windows PC — over Tailscale, so it works on the couch, in another room, or across town.

- **Shows up as a real controller.** Windows and games see an Xbox 360 or PlayStation (DualShock 4) controller, so it works in any game that supports a controller: Steam, Epic, Xbox app games, emulators.
- **Your layout, your way.** Drag, resize, recolour and reshape every control. Unlimited layouts and pages.
- **Gyro.** Aim with the right stick or the mouse, tilt-steer for racing, or pass raw motion to PlayStation-mode games and emulators (DSU/cemuhook on `127.0.0.1:26760`).
- **Smart buttons.** Drag-out (hold to aim, slide out to fire), stick-edge actions (push to the edge to sprint), turbo, toggle, and page switching.
- **Macros.** Combos played back by the PC with exact timing, either once or repeating while held.
- **Auto-switching layouts.** Tie a layout to a game's `.exe` and TailPad switches to it when that game is focused on your PC.
- **Game launcher.** Browse your Steam, Epic and custom games on the phone and start them from there.
- **Sharing.** Send a layout as a link or a `.tailpad` file.
- **Rumble.** When the game vibrates, your phone vibrates.
- **2–4 players.** Each phone that connects becomes its own controller.

## Install

### PC (Windows 10/11)
1. Install the **[ViGEmBus driver](https://github.com/nefarius/ViGEmBus/releases/latest)**, once. It's the driver that lets apps create virtual controllers.
2. Download `TailPad.exe` from the latest [release](../../releases) or [build](../../actions), and run it. It lives in the system tray.
3. Allow it through Windows Firewall when asked (Private networks).

### iPhone
1. Download `TailPad.ipa` from the latest release or build.
2. Install it with **[Sideloadly](https://sideloadly.io)** using your Apple ID. With a free Apple ID you'll need to re-sign it every 7 days.
3. Turn on **Tailscale** on the phone and the PC.
4. Point the iPhone **Camera** at the QR code in the PC app, or type in the PC's Tailscale IP and pairing code.

## How it connects

| | Port | |
|---|---|---|
| Control | TCP 47800 | pairing, macros, rumble, games, auto-switch |
| Input | UDP 47801 | sticks, buttons and gyro, about 120 times a second |
| Emulator motion | UDP 26760 (local only) | DSU server for Cemu, Dolphin, Ryujinx and others |

The PC only accepts phones on your tailnet (`100.64.0.0/10`), unless you tick "Also allow phones on the same Wi-Fi". Every phone has to pair once with a 6-digit code. Five wrong codes lock that address out for a minute.

Full protocol: [docs/PROTOCOL.md](docs/PROTOCOL.md).

## Project layout

```
pc/TailPad.Core     networking, sessions, macros, DSU server, game library (cross-platform, tested)
pc/TailPad.Host     Windows tray app: ViGEm controllers, mouse, launcher, QR pairing
pc/TailPad.Tests    end-to-end tests with a fake phone
ios/                SwiftUI iPhone app (XcodeGen project.yml)
.github/workflows   builds TailPad.exe and an unsigned TailPad.ipa; tags v* publish a release
```

Build locally: `dotnet test pc/TailPad.Tests && dotnet publish pc/TailPad.Host -c Release -o out`.
The iPhone app is built in the cloud by GitHub Actions, so no Mac is needed.

## Tips
- **Lag:** the ping is shown at the top of the pad. Run `tailscale status` on the PC. If the phone's connection shows `relay` instead of `direct`, it's slower.
- **Gyro aiming in controller games:** use "Right stick". For games that read the mouse, use "PC mouse".
- **Emulator motion feels wrong:** flip the X, Y or Z axis in Settings → Motion.
- **DualShock 4 rumble and lightbar** come through ViGEm's feedback event. On a few driver versions this event is unreliable.
