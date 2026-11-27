# LocalBloops

A small iRacing overlay in the spirit of Bloops: a live delta bar plus speed and throttle/brake traces
compared against a reference lap. Everything is local. Reference laps come from your own driving, from
`.ibt` files on disk, or from Garage 61 CSV exports you've downloaded. It never connects to Garage 61 or any other online
service, and it has no NuGet dependencies (the irsdk reader is built in).

> **iRacing must run in borderless or windowed mode.** Exclusive fullscreen draws over every other
> window, so the overlay won't be visible. In iRacing, **Alt+Enter switches to exclusive fullscreen**, not
> borderless. For borderless, use windowed mode with the border turned off. You can do that in Options → Graphics,
> or with the sim closed set `fullScreen=0` and `border=0` in `Documents\iRacing\rendererDX11Monitor.ini`.
> In borderless mode the sim makes itself always-on-top, so LocalBloops attaches the overlay to the sim window to stay above it.

## Build and run

Requires the .NET 8 SDK on Windows.

```bash
dotnet build -c Release
```

```bash
dotnet run --project src/LocalBloops -c Release
```

You can pass an `.ibt` or Garage 61 `.csv` path as the first argument to load it as the reference at startup:

```bash
src/LocalBloops/bin/Release/net8.0-windows/LocalBloops.exe "C:\Users\me\Documents\iRacing\telemetry\lap.ibt"
```

## Using it

The app runs from the tray icon. The overlay is click-through, so it never steals input from the sim.

| Action | How |
| --- | --- |
| Move/resize the overlay | **Ctrl+Shift+L** (or tray → *Unlock overlay*) unlocks it and adds an orange border. Drag anywhere inside to move it, or drag an edge or the bottom-right corner to resize, then press it again to lock. The position is saved. If another app already owns a hotkey, a tray notification says so; use the tray menu instead. |
| Hide/show the overlay | **Ctrl+Shift+H**, or double-click the tray icon |
| Overlay a Garage 61 lap | Download the lap's telemetry CSV from Garage 61, then go to tray → *Load reference from Garage 61 CSV…* (opens in `Downloads`). The lap stays the reference across sessions and restarts until you pick another one. If the session's track length differs from the lap's by more than 3%, a tray notification warns you. |
| Load a reference from an `.ibt` | Tray → *Load reference from .ibt…* (opens in `Documents\iRacing\telemetry`). The fastest valid lap in the file is used. |
| Compare to your best | Tray → *Use my best lap* |
| Compare to your last lap | Tray → *Use last lap* |
| Speed units | Tray → *Speed in mph* (km/h by default) |
| Off-track rule | Tray → *Off-track invalidates lap* (on by default) |
| See saved laps | Tray → *Open laps folder* |

### What the overlay shows

- **Delta bar:** live elapsed time minus the reference time at the same lap-distance %. Green means
  you're ahead and red means you're behind; the bar fills completely at ±2 s. `INVALID` appears when
  the current lap won't count.
- **Reference:** the reference lap time and where it came from (`Local PB`, `Last lap`, `IBT …`, `G61 <driver>`).
- **Speed trace and throttle/brake traces:** they cover 150 m behind to 500 m ahead of the car. The
  reference is drawn dimmed and your live inputs bright, and the traces wrap across start/finish.

## Garage 61 CSV import

Garage 61 exports one lap per file, sampled at 60 Hz, with `Speed`, `LapDistPct`, `Throttle`, `Brake`, `Gear`, `SteeringWheelAngle` and GPS columns, but no time column. LocalBloops rebuilds elapsed time from the row number, using the lap time in the file name (`… - 07.48.607 - id.csv`), and measures the track length from the GPS points. If the rows don't match a steady sample rate, it falls back to integrating distance over speed. The file is only read from disk; LocalBloops never contacts Garage 61.

## Personal bests

Every valid lap that beats the stored best for the current track/car combo is saved to:

```
%LocalAppData%\LocalBloops\laps\{TrackID}_{CarPath}.json
```

When the session's combo changes, that file is loaded as the reference automatically. If you loaded
an `.ibt` reference for the same combo, it is kept. Laps loaded from `.ibt` files are never written
over your personal best.

A lap counts as valid only if all of these hold:

- it started at the start/finish line
- the car was never on pit road
- the car never went off track (`PlayerTrackSurface == OffTrack`; you can turn this rule off)
- telemetry never had a gap of more than 1 s
- there were no backward or teleport jumps in lap distance (resets and tows restart the lap as invalid)

Laps are resampled onto a 2000-point lap-distance grid (time, speed, throttle, brake, gear, steering).

Settings (overlay bounds, units, options) are stored in `%LocalAppData%\LocalBloops\settings.json`.

The overlay's position and size are saved per car (by iRacing `CarPath`) and saved automatically
half a second after you stop moving or resizing it. When you switch cars, the overlay jumps to that car's saved
bounds. A car you haven't placed yet keeps the overlay where it is until you move or resize it.

## Layout

```
src/LocalBloops/
  Telemetry/   irsdk header/var parsing, live memory-map reader, .ibt reader, session YAML
  Laps/        LapRecorder (shared by live + .ibt), LapData (2000-bin grid), LapStore
  Overlay/     click-through topmost window, OnRender-based renderer, Win32 interop
  AppController.cs, TrayIcon.cs, Settings.cs, Program.cs
```
