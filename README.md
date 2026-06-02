<img width="1415" height="1161" alt="image" src="https://github.com/user-attachments/assets/b6211808-2b12-4b2d-bceb-ce6374d4d06a" />


# WLED SimHub Plugin

A [SimHub](https://www.simhubdash.com/) plugin that drives [WLED](https://kno.wled.ge/)-powered LED strips using real-time simracing telemetry. Define formula-based rules that trigger animations on your LED hardware when in-game conditions are met — pit lane speed limiter active, RPM in the redline, tyre temp critical, and so on.

---

## Features

- **Multi-device support** — control any number of WLED devices simultaneously, each with its own independent rule set
- **Network (UDP) and Serial** connection modes per device
- **Profile system** — create profiles scoped to a specific game and/or car; SimHub activates the best-matching profile automatically
- **Formula-based rules** — use any SimHub telemetry property inside `[Brackets]` to build trigger conditions (e.g. `[SpeedLimiterActive] == 1`)
- **Six animation types**
  - **Solid** — static fill colour
  - **Flash** — strobe between primary and idle colour
  - **Alternate** — oscillate between two colours
  - **Bar Fill** — fill LEDs proportionally to a value (throttle, brake, fuel level…)
  - **RPM Sweep** — three-zone colour sweep driven by RPM
  - **Chase** — a moving window of lit pixels driven by a value
- **Multi-segment pixel mapping** — each rule can target multiple independent LED ranges on the strip, each with its own idle colour, primary colour, and animation colours
- **Clone rule to another device** — copy any rule (including its segments and colours) to a different device in one click
- **WLED preset passthrough** — optionally trigger a WLED preset ID instead of a custom animation
- **Live firing indicator** — rule cards glow green while their condition is active
- **Drag-to-reorder rules** — first match wins; ordering matters
- **Undo rule deletion** — removing a rule shows a snackbar with an Undo button; the save is deferred until it times out
- **Test button** — flash all pixels on the selected device to confirm connectivity
- **Material Design dark UI** built directly into SimHub's settings panel

---

## Requirements

| Requirement | Version |
|---|---|
| [SimHub](https://www.simhubdash.com/) | 9.x or later |
| .NET Framework | 4.8 (ships with Windows 10/11) |
| WLED firmware | Any version with UDP (WARLS/DRGB) or Serial support |

> The plugin references `SimHub.Plugins.dll` and `GameReaderCommon.dll` from your local SimHub installation. These are **not** included in this repository.

---

## Building from Source

1. Install [Visual Studio 2022](https://visualstudio.microsoft.com/) with the **.NET desktop development** workload.
2. Install SimHub to its default location (`C:\Program Files (x86)\SimHub\`), or update the two `<HintPath>` entries in `WledSimhubPlugin\WledSimhubPlugin.csproj` to point to your install:
   ```xml
   <HintPath>YOUR_SIMHUB_PATH\GameReaderCommon.dll</HintPath>
   <HintPath>YOUR_SIMHUB_PATH\SimHub.Plugins.dll</HintPath>
   ```
3. Open `WledSimhubPlugin.sln` and build (`Ctrl+Shift+B`). The output DLL is placed in `bin\Debug\` or `bin\Release\`.

---

## Installation

1. Build the plugin (see above) or download a release DLL.
2. Copy `WledSimhubPlugin.dll` into your SimHub installation folder (the same folder as `SimHub.exe`).
3. Start SimHub. Accept the plugin when prompted.
4. Open **SimHub → Additional Plugins → WLED SimHub Plugin**.

---

## Quick Start

### 1 — Add a device
Click **Add New Device** in the left panel. Set the device name, choose **Network** or **Serial**, and enter the IP address / COM port. Set **Pixels** to match your LED strip length.

Use the **Test** button to flash the strip and confirm the connection is working.

### 2 — Create a profile
Click **New Profile**. Give it a name, and optionally set a **Target Game** (e.g. `Assetto Corsa Competizione`) and **Target Car**. SimHub will auto-activate the best-matching profile when you start a session.

### 3 — Write a rule
Rules are evaluated **top-to-bottom**; the first rule whose formula evaluates to true wins for that frame.

1. Search for a SimHub property in the **Telemetry Properties** box (e.g. `SpeedLimiterActive`).
2. Click **➔ Insert Selected** to drop it into the Formula box as `[SpeedLimiterActive]`.
3. Complete the expression: `[SpeedLimiterActive] == 1`
4. Give the rule a name and click **Add Rule To Selected Device**.

### 4 — Configure the animation
Expand the rule card and choose:
- **Target Device Mode** — toggle on to trigger a WLED preset by ID, or leave off for a custom animation
- **LED Segments** — add one or more pixel ranges (Start / Stop). Each segment has its own colour swatches: click the idle dot to set the off-state colour, the primary dot for the active colour, and the extra dots for Colour 2 / Colour 3 when the animation type needs them
- **Animation** — pick Solid, Flash, Alternate, Bar Fill, RPM, or Chase and set the relevant parameters

---

## Rule Formula Reference

Formulas follow SimHub's standard expression syntax. Properties are wrapped in `[Brackets]`.

| Example formula | What it does |
|---|---|
| `[SpeedLimiterActive] == 1` | Matches when the pit limiter is on |
| `[Rpms] > 7500` | Matches above 7500 RPM |
| `[BrakeBias] < 50` | Matches when brake bias is rearward |
| `[TyreTempFrontLeft] > 100` | Matches when FL tyre is overheating |
| `[IsInPit] == 1` | Matches while in the pit lane |

Use the autocomplete popup (type `[` in any formula field) to browse available properties.

---

## Animation Types

| Type | Parameters | Typical use |
|---|---|---|
| **Solid** | Primary colour | Status indicators |
| **Flash** | Flash speed (ms) | Alerts (blue flag, DRS available) |
| **Alternate** | Alternate speed (ms), Colour 2 | Warning states |
| **Bar Fill** | Value property, Min, Max | Throttle, brake, fuel |
| **RPM Sweep** | Value property, Zone 2 %, Zone 3 % | Shift lights |
| **Chase** | Value property, Min, Max, Window size | Turbo boost, ERS charge |

---

## Project Structure

```
WledSimhubPlugin/
├── WLEDPlugin.cs               # SimHub plugin entry point, render loop, engine management
├── WledSettings.cs             # Data model (WledDevice, WledProfile, PixelRule, DeviceRuleSet, LedSegment)
├── SettingsControl.xaml/.cs    # Main settings UI (Material Design, WPF)
├── ProfileManagerWindow.xaml/.cs       # Profile list/delete dialog
├── ProfilePropertiesWindow.xaml/.cs    # New/edit/clone profile dialog
├── CloneRuleToDeviceWindow.xaml/.cs    # Clone rule to device dialog
├── VisualExtensions.cs         # WPF helper extensions (FindAncestor)
└── WledSimhubPlugin.csproj
```

---

## Contributing

Pull requests are welcome. For larger changes, open an issue first to discuss the approach.

When building locally, make sure the two SimHub `<HintPath>` references in the `.csproj` resolve correctly — they are not included in this repository since SimHub DLLs are proprietary.

---

## License

MIT — see [LICENSE](LICENSE) for details.
