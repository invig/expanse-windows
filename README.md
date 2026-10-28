# Expanse for Windows

A Windows port of [Expanse](https://github.com/invig/expanse), the infinite-canvas window
manager: every window has its own place on one endless desktop; zoom out to see them all, click
one to glide straight to it.

<p align="center"><img src="docs/images/milestone-zoomed-out.jpg" alt="Zoomed out: every window live on the canvas" width="720"></p>

Early days. What works so far:

- **Zoom out** with <kbd>Ctrl</kbd> <kbd>Alt</kbd> <kbd>`</kbd> (the key below <kbd>Esc</kbd>, on any
  layout): every window, live, at its spot on the canvas, with its title above it.
- **Click a window** to glide to it at 100%, centred in the work area and focused. The other windows
  move with the canvas, off-screen if that's where the camera puts them.
- <kbd>Esc</kbd> or the hotkey again goes back to where you were; the mouse wheel zooms around the
  pointer.
- **Quitting** puts every window back where it was before Expanse moved it, as does the next start
  after a crash (`Expanse.exe --restore` does it by hand).

C# / .NET 10, built for ARM64 for now (the dev VM). Live pictures are DWM's shared thumbnail
visuals, with plain DWM thumbnails as the fallback. See [docs/spikes.md](docs/spikes.md) for why,
and for what's next.

## How it works

Same model as the Mac app ([how it works](https://github.com/invig/expanse/blob/main/docs/how-it-works.md)):
a camera (origin and scale) over one canvas, each window at a fixed spot.

- **At 100% windows are real** and sit exactly where the camera puts them. Unlike macOS, Windows lets
  a window sit wholly off-screen, so there's no corner parking.
- **Zoomed out** a full-screen, topmost overlay rises with a live picture of every window, first
  exactly over the real windows, then animated out (ease-out cubic, 0.22 s; scale interpolated in log
  space, as on the Mac).
- **The pictures are DWM shared thumbnail visuals:** DirectComposition visuals that DWM fills with
  each window, in a visual tree over the overlay. Each sits at its canvas spot inside one canvas
  visual, so the camera is a single transform per frame. The API is undocumented (dwmapi #147). If
  it's missing or fails, Expanse falls back to documented DWM thumbnails, one call per picture per
  frame. `Expanse.exe --pictures dwm` forces the fallback.
- **Off-screen windows come on-screen underneath the overlay** while it's up. GDI-drawn apps
  (classic Win32, Explorer's file list) only paint the parts of a window that are on a screen, so
  parked ones would otherwise show stale or empty pictures.
- **Clicking one** animates the camera to it (0.18 s), then moves every real window once
  (`SetWindowPos`, asynchronous so a hung app can't stall it), waits for the ones in view to land,
  focuses the window and drops the overlay.
- Animation runs a frame at a time on the UI thread, paced by `DwmFlush`.

## Layout

| Path | What |
|---|---|
| `src/Expanse` | The app |
| `src/Ctl` | `expansectl`: sends commands to the running app over a named pipe |
| `src/Guest` | Test helper on the VM's desktop: screenshots, clicks, keys |
| `src/Spikes` | Week-one experiments (pictures, parking, focus) |
| `src/Native` | Hand-written Win32 declarations shared by all of them |
| `scripts/vm.sh` | Drives the dev VM from the Mac |

## Developing (from the Mac, against the UTM VM)

The dev machine is a Windows 11 on Arm VM in UTM on the Mac, reached over SSH. Programs started
over SSH can't show windows, so anything that needs the desktop runs through a scheduled task in
the signed-in session (`scripts/guest/desk.ps1`).

```bash
scripts/vm.sh setup         # once: OpenSSH with this Mac's key, the .NET 10 SDK
scripts/vm.sh build         # sync the source over and build everything
scripts/vm.sh start         # (re)start Expanse on the VM's desktop
scripts/vm.sh ctl zoomout   # or: pick <title>, back, state, log, bench 5, pictures dwm|shared, quit
scripts/vm.sh shot          # out/shot.png: the VM's screen
```

Expanse logs to `%LOCALAPPDATA%\Expanse\expanse.log` in the VM (`scripts/vm.sh ctl log` shows the
end of it).
