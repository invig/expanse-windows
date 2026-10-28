# Week-one spikes: results (2026-10-28)

Run in the UTM 5 VM:
- **System:** Windows 11 on Arm build 26300, on UTM's VirtIO GPU 3D (DirectX) driver.
- **Display:** 1600×1200 at 75 Hz.
- **Direct3D:** D3D11 feature level 11_0 on the virtual GPU.

Absolute frame numbers from a VM don't carry over to real hardware; the comparisons do. Code is
in `src/Spikes` (plus `bench` in Expanse itself); raw results are in `out/spike-*.json` in the VM.

## Recommendation

- **Pictures:** use **shared thumbnail visuals** (DirectComposition), with DWM thumbnails as the
  automatic fallback behind the same `IPictures` interface. Done since; see the follow-up
  below. Don't use Windows.Graphics.Capture for the canvas.
- **Parking:** park windows at their exact camera spots, fully off-screen. While the overlay is
  up, Expanse moves every off-screen window onto the screen underneath it, so GDI apps paint
  (already in the milestone). No 2 px strip.
- **Focus:** start every glide from input (the hotkey or a click on the overlay); those get focus
  directly. Keep the fallback chain (Alt tap, then `AttachThreadInput`) for glides Expanse starts
  on its own.
- **VM:** UTM 5 is good enough; VMware Fusion isn't needed. The DirectX driver works after one
  restart.

## 1. Window pictures

The camera swung between the overview and 2.5× closer for 8 seconds, with 27 windows and a panel
of our own UI over the pictures.

| Route | Works here | fps | p95 frame gap | Our cost per frame | DWM CPU | Our UI on top |
|---|---|---|---|---|---|---|
| DWM thumbnails | yes | 61 | 26 ms | 2.3 ms (27 calls into DWM) | 58% of a core | needs a second, layered window |
| Shared thumbnail visuals (`dwmapi` #147, undocumented) | yes, 27 of 27 | 68 | 19 ms | 0.09 ms (one transform + commit) | 20% | just another visual in our tree |
| Windows.Graphics.Capture | yes, 27 streams | n/a (we'd draw them) | | +75 MB memory | 20% | ours to draw |

Expanse's own `bench` (DWM thumbnails, 11 windows): 71 fps, p95 16 ms, 0.8 ms per frame.

**Why shared visuals next:**
- **One transform per frame.** The camera becomes a single transform that DWM applies to every
  picture at once. With thumbnails it's one cross-process call per picture, and those calls can
  land in different frames.
- **Our UI lives in the same tree.** Labels, the hover ring, the minimap and the selection box
  can sit over and between pictures without stacking extra windows.

The risk is that it's undocumented, so the ordinal and signature can change between Windows
builds. Mitigation: try it at start-up and fall back to DWM thumbnails.

**Why not WGC for the canvas:**
- It costs memory per stream, plus a texture per frame.
- We'd have to draw every picture ourselves, which DWM does for free.

In its favour, borderless capture is allowed without a prompt for an unpackaged app (no yellow
border appeared), and frames only arrive when a window changes. Keep it for what DWM can't do:
blur or glass effects, and snapshots.

![DWM thumbnails, mid-swing, with the layered UI panel](images/pictures-dwm-mid.jpg)

## 2. Parking off-screen

Frames per second from each window's capture. Each window's content changes by itself, and the
other windows were moved out of the way so the subject was never covered.

| App (what changes) | On screen | Fully off-screen | 2 px strip | Back on screen |
|---|---|---|---|---|
| Edge, Chromium (requestAnimationFrame counter) | 36 | 36 | 37 | 36 |
| Windows Terminal (prints the time 10×/s) | 1.7 | 1.7 | 2 | 2 |
| File Explorer (folder whose files churn) | 1.7 | **0** | **0** | 1 |
| Plain Win32 GDI window (30 fps timer) | 21 | **0** | 21, but only the strip | 22 |

- **DirectX- and DirectComposition-drawn apps keep painting wherever they're parked.** Edge also
  kept rendering under our full-screen overlay: its counter went from 21,569 to 21,757 in about
  2.5 s. So Chromium's occlusion tracking didn't throttle it, at least on this build.
- **GDI drawing is clipped to the screen.** Off-screen pixels are never painted, so a window
  created off-screen has an empty picture. A 2 px strip refreshes only those 2 px. That rules out
  the strip trick.
- **WinUI apps redraw late.** The new Notepad, once parked, takes a second or two to redraw after
  being lifted under the overlay, so its picture is blank for that long. This is the same with both
  picture backends. A snapshot kept from before it was parked would cover the gap.
- **Windows never pulled a parked window back.** This was without display changes; those aren't
  tested yet.
- **The fix is in the milestone.** On zoom-out (and for glides), every off-screen window is moved
  onto the screen under the overlay; the hand-back puts each where the camera says. The GDI test
  windows went from blank to live.

Not tested:
- **VS Code (Electron).** Installing it with winget means accepting its licence. It's Chromium
  underneath, like Edge.
- **Notepad.** It was static, so it gave no signal.

## 3. Focus: `SetForegroundWindow`

| Trigger | Result |
|---|---|
| No input: a process started by Task Scheduler, or a fresh Expanse given a command over the pipe | Refused; works after a dummy Alt tap |
| Hotkey (Ctrl+Alt+`) → the overlay | Direct, 4 of 4 |
| Click on the overlay → the picked window | Direct, 5 of 6 |
| Pipe command after Expanse got the last input | Direct ("the process that received the last input" rule) |

The one refused click came straight after an Alt-tap fallback. That Alt tap put the overlay into
Windows' menu mode. The overlay now ignores `SC_KEYMENU`, and the same sequence focuses directly
since (2 of 2).

Input was injected with `SendInput` inside the VM, which these rules treat like hardware input.
The Mac's keyboard and trackpad through UTM aren't tested yet, because UTM only forwards input
while it's the active app.

## Learned about the VM

- **Display driver:** the guest tools' display driver needs a Windows restart. Before it, DWM
  half-drew windows and the clock stood still.
- **OpenSSH:** Windows' own OpenSSH feature sat queued behind a cumulative update for 25+ minutes
  on the fresh install. Microsoft's Win32-OpenSSH MSI took seconds. Its SFTP subsystem fails
  here, but `scp -O` works.
- **Disk:** the VM's C: drive has about 15 GB free.

## Follow-up: Expanse on shared visuals (same day)

Expanse now draws its canvas with shared visuals, falling back to DWM thumbnails (tested by
forcing every shared visual to fail: `ctl pictures shared-fail`). The `bench` was run twice per
backend, with 10 windows, in the same session:

| Backend | fps | p95 frame gap | Worst gap | Our cost per frame | DWM CPU |
|---|---|---|---|---|---|
| DWM thumbnails | 52–59 | 26–33 ms | 117–119 ms | 1.1–1.6 ms | 48–58% |
| Shared visuals | 63 | 23–25 ms | 45–53 ms | 0.12 ms | 32–34% |

- **Zoom-out and dive timings are noisy in the VM for both backends:** 4 to 14 frames for a
  0.2 s animation, at 75 Hz.
- **A Windows toast can sit above our topmost overlay.** The OneDrive one did.

## Next

1. ~~Live window tracking (`SetWinEventHook`)~~: done, along with the glide when the taskbar or
   Alt-Tab focuses a parked window, and the tray icon.
2. ~~Hops on Ctrl+Alt+arrows; the layout maths ported with its tests~~: done. Next on the maths:
   use it: drag with snapping, Tidy Up, new windows in free space. Then the saved layout.
3. Dialogs should move with their owner. Then maximised windows, admin windows, several monitors and mixed DPI.
