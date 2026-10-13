SPECTARI
==========

Stream a window, monitor, or capture device to friends' browsers over
Tailscale or LAN. Choose one application's audio, desktop audio, a capture
device's audio input, or no sound. Desktop audio can include voice chat.

REQUIREMENTS
------------
Hosting needs Windows 10/11, x64. Per-application audio requires Windows
build 20348 or later. On older Windows 10 builds, use desktop audio.
The built-in Watch window needs the Microsoft Edge WebView2 Runtime.
Browser playback needs Media Source Extensions and H.264/AAC support.
This package includes FFmpeg and does not need a separate .NET installation.

SETUP (once, ~2 minutes)
------------------------
1. For internet viewing, install Tailscale (tailscale.com/download) and sign
   in. Share the hosting PC with viewers from the Tailscale admin console,
   or invite fellow streamers to your network. For LAN or a local test,
   Tailscale is not required. Full setup instructions:
   https://github.com/Skeptic043/spectari#readme
2. Double-click Spectari.exe and Start a stream. If a friend can't
   connect, click "Open port" in the app: it asks Windows for
   administrator approval once and configures the stream port for you
   (the port is what Spectari serves the stream on and what viewers'
   browsers connect to). You normally do this once for each port you use.
   If you'd rather do it up front, or "Open port" doesn't work for
   some reason, right-click setup.bat -> Run as administrator instead.
   It does the same thing: reserves the stream port and adds a firewall
   rule scoped to Tailscale addresses.

   The firewall rule allows only Tailscale by default. To also let devices
   on your local network (LAN) reach the stream, tick "Allow LAN viewers"
   in the app before clicking "Open port". setup.bat always sets up
   Tailscale-only. Use the app for LAN access.

STREAMING
---------
1. Double-click Spectari.exe.
2. Pick your game from the list, pick a quality preset, Start streaming.
   The encoder dropdown shows Auto, CPU, and the hardware encoders detected
   on this PC. Leave it on Auto unless you have a reason to force one.
   Live status shows which encoder is actually running, including if it
   falls back to CPU mid-stream.
3. Copy link -> paste it to your friends. They open it in a compatible browser.
   For a viewer who is only on your LAN, use the arrow next to Copy link
   and choose Copy LAN link.
   Hover the video for sound and volume controls, and fullscreen.
   The link includes a per-stream key, a new one is generated every time
   you start a stream. Eligible Tailscale viewers and browsers on the host
   itself can recover the new key in an open tab or by reopening an old link.
   LAN-only viewers need a fresh link. Discovery and key recovery for viewers
   using a shared-machine invite have not yet been verified.
   After upgrading the host, reload older browser pages and update older
   Watch installations to restore automatic key recovery.
   While the app is open but not streaming, the link shows "not streaming
   yet" and connects on its own once you start.

Minimizing keeps the app on the taskbar and keeps streaming. Closing the
window stops your stream. If Spectari is already running, launching it
again just brings that window back instead of opening a second copy. The
app waits for the first captured frame before reporting the stream as
live. If it stops with a capture error instead, use "Copy log" (it
includes version, system, GPU, and encoder info along with the recent
log). Review and redact it before sharing the relevant excerpt.

WATCHING MULTIPLE STREAMS, THE GRID
------------------------------------
Anyone streaming also serves a grid page at http://<their address>:<port>/grid
(a stream link with /grid in place of everything after the port).
Open it, paste each friend's stream link once, every live stream tiles
into one tab. Easier: the app's "Watch streams" window is the same grid
with a stream finder built in. Reorder tiles with
the arrow buttons, and switch between side-by-side and stacked layout.
the list is remembered by your browser.

TIPS
----
- FULLSCREEN GAMES: share the whole monitor. Monitor shares use the
  desktop-duplication path by default and capture the cursor. If an
  exclusive-fullscreen game freezes, switch the game to borderless mode.
  Pick the game in the Audio list so the monitor share has sound.
- WINDOW SHARES AT 60 FPS: Windows window capture can deliver fewer fresh
  frames than the target rate on some systems. Try sharing the monitor or
  lowering the frame rate or resolution. A 60 fps setting does not guarantee
  60 distinct captured frames each second.
- The Audio dropdown chooses whose sound the stream carries: the
  captured window, another running app, desktop audio, or nothing. Capture
  shares offer the card's matching audio input when available, otherwise
  they offer active inputs that can include microphones. Check your selection.
- "Watch streams" in the app opens the built-in viewer (same grid as
  the browser version, identical playback for everyone), including a
  "Find streams" panel that lists live streams from other Spectari
  hosts among Tailscale peers visible to your PC for one-click adding.
- Add &stats=1 to the end of a stream link for a diagnostics overlay
  (the link already ends in ?k=..., so it's & not ?).
- The app remembers your source/preset/audio/port between runs.
- CUSTOM PORT (optional): setup.bat covers port 8093. For another port,
  open Command Prompt in this folder and run:  setup.bat 8094
  (that's the same setup.bat, given the port as an argument, not a
  separate file). Then set the same port in the app. If you use
  start-stream.bat, edit the PORT line there to match.
- Streaming at the same time as a friend is fine, whatever the ports.
  ports only matter per PC.

IF SOMETHING BREAKS
-------------------
Every run writes a log file (its path is printed at the top of the app's
log panel). "Copy log" puts the session log plus version, system, GPU,
and encoder info on the clipboard. Review and remove viewer links, keys,
addresses, names, or other private information before posting a relevant
excerpt with steps to reproduce the problem.

- Stream page never loads: the port isn't open yet, or Tailscale isn't
  connected on one end. Click "Open port" in the app (or run setup.bat
  as admin) and try again.
- Status shows "THIS PC ONLY": click "Open port" for that port. The app
  restarts an active stream automatically. If you run setup.bat instead,
  restart the stream yourself.
- Page loads but video never starts: same as above, the port that
  viewers' browsers connect to isn't reachable yet. Click "Open port" again
  or run setup.bat.
- "Port ... is already in use": another program (often another copy of
  Spectari still running) has that port. Close it, or set a different
  port in the app.
- Everything looks fine on your end but nobody can connect: firewall
  state can drift (a Windows reset, another program, a network profile
  change). Click "Open port" again. It configures one port at a time, so
  after changing the port, run it again for the new one.
- Stream stops with "video timing overload": lower the frame rate or
  resolution and try again.

AI USE
------
AI was used to write all of the code in this project. The original concept,
design direction, testing, debugging, and release decisions are my own. If
you prefer not to use software developed with AI assistance, I understand
and respect that choice.

License: free for personal and other noncommercial use under PolyForm
Noncommercial 1.0.0 (see LICENSE.txt). Commercial use is not included.
Contact the maintainer through GitHub regarding commercial licensing.

This package includes an unmodified FFmpeg executable as separate software
under GPLv3 or later. See FFMPEG-LICENSE.txt and THIRD-PARTY-NOTICES.txt for
the license, exact source link, binary hash, and build-provider provenance.
