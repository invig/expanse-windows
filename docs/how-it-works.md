# How Expanse for Windows works

The same model as [Expanse for Mac](https://github.com/invig/expanse/blob/main/docs/how-it-works.md):
a camera (an origin and a scale) over one canvas, every window at a fixed spot on it. C# / .NET 10,
hand-written Win32 declarations, [Vortice.Windows](https://github.com/amerkoleci/Vortice.Windows)
for DirectComposition and Direct2D. Nothing is injected into other apps.

## The canvas

- **At 100% windows are real** and sit exactly where the camera puts them. Unlike macOS, Windows
  lets a window sit wholly off-screen, so windows out of view are simply further along the canvas,
  just past an edge of the main screen with no monitor beyond it.
- **Zoomed out** a full-screen, topmost overlay rises with a live picture of every window, first
  exactly over the real windows, then animated out (ease-out cubic, 0.22 s; scale interpolated in
  log space, as on the Mac).
- **The pictures are DWM shared thumbnail visuals:** DirectComposition visuals that DWM, Windows'
  compositor, fills with each window, in a visual tree over the overlay. Each sits at its canvas
  spot inside one canvas visual, so the camera is a single transform per frame. Expanse never
  copies a window's pixels. The API is undocumented (dwmapi #147, see
  [ADeltaX](https://github.com/ADeltaX)); if it's missing or fails, Expanse falls back to
  documented DWM thumbnails, one call per picture per frame. `Expanse.exe --pictures dwm` forces
  the fallback.
- **Expanse's own UI is in the same tree, over the pictures:** titles (Direct2D/DirectWrite, drawn
  once per text), the hover and selection rings and close buttons (each in its picture's place in
  the stack, so a window in front covers them), the marquee, snapping guides, the minimap and the
  Desktops panel. Everything that moves with the camera changes in one composition commit per
  frame. With the fallback, DWM draws its thumbnails over everything, so titles and rings go on the
  backdrop underneath, and there are no close buttons (<kbd>Ctrl</kbd> + <kbd>W</kbd> still works).
- **Off-screen windows come on-screen underneath the overlay** while it's up. GDI-drawn apps
  (classic Win32, Explorer's file list) only paint the parts of a window that are on a screen, so
  windows out of view would otherwise show stale or empty pictures.
- **Clicking one** animates the camera to it (0.18 s), then moves every real window once
  (`SetWindowPos`, asynchronous so a hung app can't stall it), waits for the ones in view to land,
  focuses the window and drops the overlay. Animation runs a frame at a time on the UI thread,
  paced by `DwmFlush`.
- **When DWM restarts** (after a graphics driver reset or update) the pictures die with it, as the
  Mac's do when its capture service restarts. Expanse waits for it (no zooming out, no reading
  window frames, no hot corner meanwhile) and then makes new pictures, in place if zoomed out.

## Following the windows

- **Window events** come from `SetWinEventHook`, out of context, on the UI thread. Comings, goings
  and moves are gathered into one sync 50 ms after the last of them; focus acts where it settles,
  120 ms later. A window that gets focus while it isn't fully on screen (the taskbar, Alt-Tab, an
  app bringing it forward) is glided to: the Mac's auto-centre. Focus coming back to the window you
  were in, or handed on by Windows after you close or minimise one, doesn't count.
- **Focus** is set directly where Windows allows it; where it doesn't (another app is in front),
  a null key press, then an Alt tap, then `AttachThreadInput`.
- **The empty desktop** is watched by a low-level mouse hook on its own thread: a click there zooms
  out, a scroll starts moving around from 100%. The hook also notes when buttons go down, so a
  click can be told from the pointer moving (Windows' own "last input" counts both).
- **Dialogs** (owned windows with a title bar) keep their place by their owner and move with it;
  Windows doesn't move them. One asking something while you're zoomed out is shown over the canvas.
- **Maximised windows** can't be moved, so to move one out of view Expanse restores it to a window
  of the same size (`SetWindowPlacement`), and maximises it again at its spot.
- **Admin windows** belong to processes at a higher integrity level, which Windows won't let a
  normal program move (UIPI). Include Admin Windows restarts Expanse elevated, and back; the two
  copies hand over through the saved layout without moving anything.
- **Virtual desktops** are read from where Explorer keeps them in the registry (there's no public
  API); switching presses <kbd>Win</kbd> + <kbd>Ctrl</kbd> + arrows. Windows on other desktops are
  cloaked, so they leave the canvas and come back to their spots.
- **Monitors.** The canvas is the main monitor's. A window only on another monitor is left alone;
  one moved onto the main screen joins the canvas.

## Screens of every size

The layout maths is a line-for-line port of the Mac app's, in `src/Layout`, with no Windows APIs.

- **Home layout.** Each window has a home spot (its place at full chosen sizes) and the size you
  chose: dragging an edge, or a resize right after your click or key press in its app (Snap, a
  snap layout, FancyZones) that Expanse didn't cause.
- **Fitting a screen.** Sizes are capped at the work area (a 16 px margin only for windows bigger
  than it). Positions are derived from home: each window keeps its gap to its nearest window on the
  left or, nearer, its offset from a window above or below (a column stays lined up); the same
  vertically. Only overlaps the smaller screen caused are pushed apart.
- **Moving only as far as the screen changed.** After a display change, a window moves by how much
  its derived spot moves between the old and new screen sizes, less last fit's push apart, so a
  round trip comes back exactly, and a restart on the same screen moves nothing. The fitted size is
  saved with the layout.
- **Home spots from neighbours.** A window opened or moved on a smaller screen, or one its home spot
  no longer explains, gets a home spot measured from its neighbours here: the inverse of the
  derivation.
- **Scaling changes** scale the canvas by the DPI ratio, then fit. Layouts record the scaling they
  were saved at.

## Saving and restoring

`%LOCALAPPDATA%\Expanse\` holds `layout.json` (written a second after changes, and on pause or
quit), up to three snapshots 15 or more minutes apart in `Snapshots\` for Restore Layout,
`settings.json`, `originals.json` (where windows were before Expanse moved them, so quitting, or
the next start after a crash, puts them back), and with Detailed Logging, `expanse.log`.

Windows are matched to saved spots by the same window while its app keeps running, then by app and
title (titles are stored only as hashes), then by app; a window under a quarter of a saved one's
size (a loading box) doesn't take its spot. Saved spots are used at launch, or for an app that has
just started; a window a long-running app opens later is placed in free space beside you.

## Updates

Release builds check `api.github.com/repos/invig/expanse-windows/releases/latest` a minute after
launch and then once a day, download `Expanse-<version>-windows-<arm64|x64>.zip` for their own
processor, and check it: the SHA-256 GitHub lists for the file, the version in the exe, and when
the running copy is code-signed, the same publisher's signature (`WinVerifyTrust`). Installing
renames the running `Expanse.exe` (Windows allows that, not overwriting it), moves the new one in,
starts it and hands over as Include Admin Windows does; the new copy deletes the old file.

## The code

| Path | What |
|---|---|
| `src/Expanse` | The app |
| `src/Layout` | The canvas maths (deriving, nearest window, Tidy Up, free spots, snapping), no Windows APIs |
| `src/Layout.Tests` | Its tests: by hand, and against the Mac app's own Swift on random layouts |
| `src/Native` | Hand-written Win32 declarations |
| `src/Ctl` | `expansectl`: sends commands to the running app over a named pipe |
| `src/Guest` | Test helper on the VM's desktop: screenshots, clicks, keys, the wheel |
| `src/Spikes` | The first week's experiments (pictures, parking, focus) |
| `scripts/vm.sh` | Drives the development VM from a Mac |
| `scripts/release.sh` | Builds the release zips in the VM and publishes them |

## Testing against the Mac

`scripts/layout-oracle.py` takes the layout functions from the Mac app's source (`../CANVAS`),
runs them with Swift on thousands of random layouts, and writes `tests/layout-fixtures.json`.
`src/Layout.Tests` requires the C# to give exactly the same answers. Run the script again when the
Mac's maths changes: if a function it extracts has changed shape, the script stops rather than test
the wrong thing.

## Developing

Expanse for Windows is developed from a Mac against a Windows 11 on Arm VM in
[UTM](https://github.com/utmapp/UTM), reached over SSH. Programs started over SSH can't show
windows, so anything that needs the desktop runs through a scheduled task in the signed-in session
(`scripts/guest/desk.ps1`).

```bash
scripts/vm.sh setup         # once: OpenSSH with this Mac's key, the .NET 10 SDK
scripts/vm.sh build         # sync the source over and build everything
scripts/vm.sh start         # (re)start Expanse on the VM's desktop (`start admin`: as administrator)
scripts/vm.sh ctl zoomout   # or: pick <title>, back, state, windows, log, pause, resume, bench 5, quit
scripts/vm.sh ctl screen 640 480             # pretend the screen is that size (no size: the real one)
scripts/vm.sh ctl monitor 1440 0 1920 1080   # pretend there's another monitor there
scripts/vm.sh guest scale 125                # the main display's scaling (`scale 150 2`: display 2's)
scripts/vm.sh shot          # out/shot.png: the VM's screen (`shot FILE all`: every display)
scripts/vm.sh test          # the layout maths' tests
DRY_RUN=1 scripts/release.sh   # release builds (arm64, x64) zipped in artifacts/release
scripts/release.sh          # the same, published as a GitHub release (bump <Version> first)
```

How each part was tried, measured and decided, in order: [spikes.md](spikes.md).
