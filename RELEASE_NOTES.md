Pill 1.0.0 beta — first public build.

This is a Windows dynamic island: a black capsule pinned to the top of the screen with a live media player, equalizer, waveform, ambient color from the active window, a drop shelf for files, and signed OTA updates from GitHub Releases.

What changed in this build:

- Replaced the old WinForms context menu with a separate WPF tray menu in the Aerisflow style: blurred glass, rounded cards, thin scrollbar, switches and update card. It closes on an outside click, on Esc, or from the tray icon.
- Reduced the menu to what is actually needed: AMBIENT, SYSTEM and UPDATES. Removed the player and behavior controls from the menu.
- Reworked the ambient color: area-weighted dominant hue from the active window, no blue mixing and no album-art influence, small accents below 1.5% ignored, saturation raised to at least 55%, applied immediately when the window changes, with a smooth 320 ms accent transition. Menu accents now fall back to the default blue instead of turning gray when the active window is gray.
- Kept the capsule fixed-size: 380x66 collapsed, 480x330 expanded, 40x40 and 52x52 cover art, 68 px waveform, no overlapping elements. The background stays black with the #141A2B to #020305 gradient and a 16% accent layer.
- Fixed battery reporting: only real connected batteries with DEVPKEY_Device_BatteryLevel are shown, phones are excluded, and devices such as Mchose A7 Ultra / G435 show a dash.
- Fixed network reporting: the active adapter is resolved with GetBestInterface(8.8.8.8) and GetAdaptersInfo; the lower panel is DROPSHELF.
- Added a separate hand-written installer (no Inno Setup). It supports silent --install / --uninstall, a Start menu shortcut, a Run entry and an uninstall registry entry. Uninstall keeps settings.json and removes the app folders, current.json, device.json, shortcut and registry entries.
- Replaced the old zip-based updater with a signed manifest system: manifest.json + manifest.sig, RSA/SHA256 verification, per-file SHA256 checks, staged app-<version> folders, current.json, beta/stable channels, rollout percentage, release notes, deferred installation and automatic restart. Updates are available only in installed builds; portable builds show "Only in installed builds".
- Settings live in %LOCALAPPDATA%\Pill\settings.json and survive updates and uninstall.

Install with Pill.Installer.exe or Pill.exe --install.

This release was put together in a hurry. It is beta, not a final version. About half of the decisions may change in a patch: ambient color behavior, animation timing, menu layout, settings format, installer and the update flow.
