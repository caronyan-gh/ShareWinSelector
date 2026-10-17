<p align="center">
  <img src="src/ShareWinSelector/Assets/logo.png" alt="ShareWinSelector" width="560">
</p>

<p align="center">
  <b>Share <i>Win</i>dows — and <i>win</i> your meetings.</b><br>
  Show only the windows you choose, exactly where they are on your screen.
</p>

<p align="center">
  English | <a href="README.ja.md">日本語</a>
</p>

---

Screen sharing in online meetings such as Teams usually gives you two choices: share **one window**, or share **the whole screen**.
One window means re-sharing every time you switch between your IDE and your browser.
The whole screen means everyone sees your chat notifications, mail previews and desktop file names.

**ShareWinSelector** sits in between. Pick any number of windows and it composes them into a single
"shared output" window — at their real positions and in their real stacking order. You share that one window
and keep working with your apps exactly as usual.

## Features

- **Only the windows you pick are visible** — at their real positions, sizes and z-order. Everything else is replaced by a background.
- **Keep working as usual** — no virtual camera, no special mode. You operate the original windows directly.
- **Notifications and chats never show up** unless you picked them. Windows you did not pick do not leak into the share, even when they overlap.
- **Menus and dialogs of the picked apps are included** — context menus, owned dialogs and tooltips appear automatically.
- **Crop the top** — hide browser tab titles and the address bar. The crop is saved per app (PWAs are told apart from the browser).
- **Confirm before sharing** — a picked window is not shown until you confirm it on screen. No more sharing the wrong window by accident.
- **Overlay only you can see** — highlight borders around shared windows, a floating toolbar and a *click to select* mode, all excluded from capture.
- **Live preview** of what others see, thumbnail or list view, compact mode, always on top.
- **Background** for the empty area: black, the standard wallpaper, or your own image.
- **Robust** — survives GPU resets, display changes, closed windows and reused window handles.
- Dark theme, English / Japanese UI.

## Requirements

- Windows 10 version 2004 or later, or Windows 11 (64-bit)
- For the standard download: [.NET 10 Desktop Runtime](https://dotnet.microsoft.com/download/dotnet/10.0) (Windows points you to it if it is missing)

## Download

Get the latest version from [Releases](../../releases/latest):

| File | Size | Notes |
|---|---|---|
| `ShareWinSelector.exe` | ~30 MB | Standard. A single file; nothing is extracted. Needs the .NET 10 Desktop Runtime. |
| `ShareWinSelector-SelfContained.exe` | ~70 MB | No runtime needed. A few native WPF files are extracted to a temp folder on first start. |
| `SHA256SUMS.txt` | | Checksums of the files above. |

There is no installer — just run the exe. Settings are stored in `%LOCALAPPDATA%\ShareWinSelector\`.

The executables are not code-signed, so Windows SmartScreen may show *"Windows protected your PC"* the first time.
Click **More info** → **Run anyway**. Release builds are made by [GitHub Actions](.github/workflows/release.yml) from the tagged source.

## How to use

1. Start **ShareWinSelector** and choose the **monitor** to share.
2. Check the windows you want to show — in the thumbnails / list, or with **Click to select** on the toolbar.
3. In Teams (or any other tool), **share the window** named **`🖥️ ShareWinSelector - Shared output`**.
4. Work normally. Others see only the checked windows, in place.

**Where to put the output window** (Settings → *Output window placement*)

- **Behind all windows** (default) — it stays at the bottom; move your windows away to peek at what others see.
- **Normal** — a regular window. Drag anywhere to move, double-click to maximize to the whole monitor.
- **Off-screen** — placed outside all monitors at the monitor's native resolution. You never see it; the preview in the main window shows what others see.
  - Teams keeps sharing it while it is off-screen.
  - **Zoom pauses the share while the window is off-screen** (it resumes when the window is back on screen). This is how Zoom works, so use *Behind all windows* or *Normal* with Zoom.
  - Other apps (Google Meet, Webex, Discord, etc.) have not been tested. If the share pauses or shows nothing, switch to *Behind all windows* or *Normal* in the same way.

**Crop the top** (e.g. hide browser tabs)

- Turn on **Crop top** above the list to crop windows when you pick them, or check **Crop** on a single window.
- If the app has no saved crop yet, the window stays hidden while you drag its top edge down on the overlay. Press **Apply** (Enter) to start sharing it.
- The crop amount is saved per app, and whether each app is cropped is remembered across restarts.

**Confirm before sharing**

- With **Confirm before sharing** on, a picked window stays hidden until you press **Share** on the overlay. If you pick several windows, they are confirmed one by one.

## Build from source

```
dotnet build src/ShareWinSelector
```

Publish single-file executables (written to `src/ShareWinSelector/bin/publish/`):

```
dotnet publish src/ShareWinSelector -p:PublishProfile=FrameworkDependent
dotnet publish src/ShareWinSelector -p:PublishProfile=SelfContained
```

Built with .NET 10 / WPF, `Windows.Graphics.Capture`, and Direct3D 11 + Direct2D via [Vortice.Windows](https://github.com/amerkoleci/Vortice.Windows).

> [!NOTE]
> In **Debug** builds the main window, overlay and toolbar are **not** excluded from capture, so you can take screenshots of them
> (the title shows `(Debug - capturable)`). Always use a Release build in real meetings.

## License

[MIT](LICENSE)
