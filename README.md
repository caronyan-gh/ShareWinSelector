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

<p align="center">
  <img src="docs/demo.gif" alt="Left: your screen, with the picked windows outlined. Right: what others see — only the picked windows, at their real positions. The chat and the notification never appear on the right." width="100%">
</p>

<p align="center">
  <a href="https://github.com/caronyan-gh/ShareWinSelector/releases/latest"><b>⬇ Download the latest version</b></a><br>
  Not sure which file? Get <code>ShareWinSelector-SelfContained.exe</code> under “Assets” — it runs as is.
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
- **Switch monitors without re-sharing** — change "Monitor to share" and others see the new monitor right away; no need to stop and re-share in your meeting app. In compact mode, the numbered monitor buttons (1–4) switch it in one click (pick a 5th or later monitor in the normal view).
- **Clear per monitor** — windows on the shared monitor come first in the list; windows on other monitors are dimmed below a "── DISPLAY2 ──" separator. A picked window moved to another monitor is not visible to others, so it gets a muted blue border instead.
- **Bring all to front / Uncheck all** — raise the shared windows on the shared monitor at once, keeping their order (without flicker for others). After unchecking all, **Undo** re-shares them (with their crop) for a few seconds.
- **Remote link (experimental)** — share the screen of another PC on the same LAN (for example, one you use over Remote Desktop) as one more monitor, **R**. You can also pick that PC's windows from this PC's list.
- **Crop the top** — hide browser tab titles and the address bar. The crop is saved per app (PWAs are told apart from the browser).
- **Confirm before sharing** — a picked window is not shown until you confirm it on screen. No more sharing the wrong window by accident.
- **Overlay only you can see** — highlight borders around shared windows, a floating toolbar and a *click to select* mode, all excluded from capture. The toolbar folds itself after a while and unfolds when you hover over "Sharing n".
- **Live preview** of what others see, thumbnail or list view, compact mode, always on top.
- **Background** for the empty area: a solid color of your choice (drag to pick), the standard wallpaper, or your own image.
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
To remove it, press **Delete settings and exit** at the bottom of the settings, then delete the exe. Nothing is left behind.

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
- **Right-click** the output window to pick full screen (borderless), 75% / 50% / 33% of the monitor, or common sizes such as 1920 × 1080 (except when off-screen).
- If the output window hides your desktop icons, the **desktop** button at the top of the main window opens the desktop folder in Explorer.

**Crop the top** (e.g. hide browser tabs)

- Turn on **Crop top** above the list to crop windows when you pick them, or check **Crop** on a single window.
- If the app has no saved crop yet, the window stays hidden while you drag its top edge down on the overlay. Press **Apply** (Enter) to start sharing it.
- **Ctrl+click** a window during *Click to select* to adjust its top edge before sharing it. On a window that is already shared, it lets you redo the crop.
- The crop amount is saved per app, and whether each app is cropped is remembered across restarts. **Crop** on a row is greyed out until the app has a saved crop.

**Confirm before sharing**

- With **Confirm before sharing** on, a picked window stays hidden until you press **Share** on the overlay. If you pick several windows, they are confirmed one by one.

**Remote link (experimental)**

Share the screen of another PC (the **child**), such as one you use over Remote Desktop (RDP), as one more monitor (**R**) of the PC you share from in the meeting (the **parent**).
In your meeting app you still share only the parent's `🖥️ ShareWinSelector - Shared output`.

- It is meant for use within the same LAN (the same network segment). VPNs and the internet are not supported.
- **Getting started**: start ShareWinSelector on both PCs and turn on **Use remote link** in **Advanced** in the settings.
  - Choose **Parent** on the PC that shares in the meeting, and **Child** on the PC that sends its screen.
  - On the child, pick the parent with **Find parents**, or enter the parent's IP address and press **Connect**. Use the same port on both (47810 by default).
- **Pairing**: the first time they connect, both PCs show a 6-digit number. Check that both screens show the same number, then press **Allow** on both.
  - After that they connect without asking. **Unpair** on either side makes them check the number again on the next connection.
  - The connection is encrypted.
- **Sharing the child's screen**: once connected, **R** is added to the parent's *Monitor to share*. Pick R and others see the windows shared on the child, at their positions on the child's screen.
  - Windows of the parent and the child are never mixed into one picture. R is switched like any other monitor.
  - While a PC runs as a child, its output window is moved off-screen automatically.
- **Picking the child's windows on the parent**: the child's windows are also listed on the parent, below a "── R *child name* ──" separator. Checking one starts sharing it on the child.
  - In the thumbnail view, still images of the child's windows arrive every few seconds.
  - The parent's **Uncheck all** and **Undo** also work on the child's windows.
  - With the parent's **Confirm before sharing** on, the parent asks next to the cursor.
  - The window is cropped if **Crop top** is on on either the parent or the child. If the child has no saved crop for that app yet, set the crop area on the child's screen; the parent tells you so next to the cursor and in the status line.
- **Firewall**: the first time, Windows may ask whether to allow ShareWinSelector through the firewall. It needs to be allowed to connect.
  - That rule stays after you stop using remote link. If you no longer need it, remove ShareWinSelector under *Allow an app through Windows Firewall*.
- **Running the child inside RDP**: after the cursor enters the RDP window, it may stay hidden (and input may not reach the PC) until you click once. This also happens with other apps that capture the screen inside RDP, such as Snipping Tool; it is how Windows / RDP behaves.
- Using RDP while your meeting app is sharing can feel sluggish. Most of that load comes from the meeting app.

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
