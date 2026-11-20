# Rival 3 RGB

A small, portable Windows app for the **wired SteelSeries Rival 3 Gen 2** (USB ID `1038:1870`). It controls lighting directly over USB and runs without SteelSeries GG. This is an independent project.

## Run the app

Extract the Windows release ZIP to a folder where you can save files, then open `Rival 3 RGB.exe`. The release executable is unsigned. Close SteelSeries GG and Prism if they override the mouse lighting.

- Choose a color or toggle power for each of the three zones. **Set all zones** applies one color to all three, and **Lights off** turns them off.
- **Save preset** adds a numbered setup. Pick its number from **Presets** to apply it. Presets include all three colors and on/off settings.
- The app saves your last setup beside the executable and restores it when it opens or detects a reconnect. Keep the app open, or minimized, for reconnect detection.
- **Mouse startup lighting** saves the mouse's built-in power-up mode. Choose a mode and click **Save to mouse**. Close the app before unplugging and reconnecting to test the onboard mode. Custom colors are stored on the PC.

## Build from source

Run `build.ps1` in PowerShell on Windows. The script uses the Windows .NET Framework C# compiler and writes `dist\Rival 3 RGB.exe`. The build requires no third-party packages.

## Credits and protocol references

- [Rivalcfg’s Rival 3 Gen 2 device profile](https://github.com/flozz/rivalcfg/blob/v4.17.0/rivalcfg/devices/rival3_gen2.py) and [device documentation](https://flozz.github.io/rivalcfg/devices/rival3_gen2.html) were references for the device ID, RGB zone commands, and startup lighting modes.
- [Alloyctl’s Rival 3 Gen 2 protocol notes](https://alloy.szymon-wilczek.me/protocol/steelseries-rival3-gen2.html) were a reference for the USB report layout, command acknowledgments, and how the rainbow and manual color modes interact.
- [Microsoft’s device notification documentation](https://learn.microsoft.com/en-us/windows/win32/api/winuser/nf-winuser-registerdevicenotificationw) was a reference for detecting USB reconnects.

This app implements the protocol in C# and bundles no third-party code or libraries.
