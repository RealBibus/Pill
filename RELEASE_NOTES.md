Pill 1.0.1 beta — performance and cleanup release.

This build keeps the same capsule, weather, tray menu, installer and signed OTA flow, but removes the background work that could reduce a game from 120 fps to 60 fps.

What changed:

- Fullscreen game detection now uses a foreground-window hook, so the capsule hides and suspends immediately instead of waiting for a poll. A slow safety poll remains for windows that become fullscreen without changing focus.
- While hidden or in a fullscreen game, WASAPI capture, FFT, waveform rendering, media timeline polling, battery/network refresh and hover polling all stop. The process stays almost idle.
- WASAPI loopback capture starts only when the capsule is expanded and a track is actually playing. It is released on collapse, on manual hide and on game detection.
- CompositionTarget.Rendering hooks are attached only while the relevant visual is visible; idle waveform and equalizer updates are capped at 20 fps instead of following the display refresh rate.
- Hover detection now uses cached DPI and a cheap screen-position rejection before any transform or layout query is made.
- Restored the dark acrylic blur on the tray menu and installer with the same safe, guarded composition calls. Removed the unused key-generation helper, unused theme resources and throwaway keyword array allocations from the status refresh path.
- Simplified the expanded WEATHER card: a quiet translucent panel with just the icon, temperature and condition. The temperature and condition now stay fully inside the panel instead of clipping at its edge, and the card does not overlap the player or dropshelf.
- Added more breathing room between the weather card and the waveform/progress panel.
- Seeking in a video or track no longer resets the progress bar and position: transient zero timestamps from the media session are ignored while playback continues, while a real seek is applied immediately.
- Moved the small level meter out of the weather row and next to the source label, where the music status belongs.
- Fixed button and outline overflow in the header, player row and dropshelf row.
- Listening time is now saved to settings every 30 seconds and on exit, survives app restarts and updates, and keeps counting through brief SMTC session switches common with browser videos.
- Kept the capsule fixed-size: 380x66 collapsed, 480x330 expanded. The compact capsule always shows the clock and weather — a larger time, weather icon and temperature with an explicit °C/°F unit — next to the current cover, title and artist.
- Kept click-through in the collapsed capsule and full interactivity in the expanded capsule.
- Kept automatic hiding in borderless fullscreen games. Fullscreen windows with close, minimize and maximize controls stay visible, and the capsule returns when the game exits.
- Kept battery reporting limited to real connected batteries with DEVPKEY_Device_BatteryLevel, excluded phone-like devices and the network route lookup through GetBestInterface(8.8.8.8).
- Kept the separate hand-written installer and the signed manifest updater with beta/stable channels, rollout, deferred installation and automatic restart.

Install with Pill.Installer.exe or Pill.exe --install. Updates remain available only in installed builds.
