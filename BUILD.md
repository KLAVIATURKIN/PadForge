# PadForge -- Build & Project Reference

## Overview

PadForge is a controller mapping utility (fork of [x360ce](https://github.com/x360ce/x360ce)) rebuilt with:
- **[SDL3](https://github.com/libsdl-org/SDL)** (custom fork under `SDL3-build/SDL/` with HIDMaestro filtering) for all device input
- **[HIDMaestro](https://github.com/hifihedgehog/HIDMaestro)** as the single virtual-controller backend (Xbox / PlayStation / Extended types)
- **[OpenXInput](https://github.com/hifihedgehog/OpenXinput)** XInput shim, embedded in the single-file build
- **[HelixToolkit](https://github.com/helix-toolkit/helix-toolkit)** for interactive 3D controller visualization
- **DSU/Cemuhook** motion server for gyro/accelerometer passthrough
- **.NET 10 WPF** with [WPF-UI](https://github.com/lepoco/wpfui) Fluent Design
- **MVVM** architecture with [CommunityToolkit.Mvvm](https://github.com/CommunityToolkit/dotnet)

3D controller models adapted from **[Handheld Companion](https://github.com/Valkirie/HandheldCompanion)** (CC BY-NC-SA 4.0).
2D controller schematics from **[Gamepad-Asset-Pack](https://github.com/AL2009man/Gamepad-Asset-Pack)** by AL2009man (MIT).
Steam Controller and Steam Deck hardware art derives from **Valve's own published CAD** (CC BY-NC-SA 4.0): the 2015 controller from its March 2016 separate-parts STL release, the 2026 controller from the solid model and reference drawing shipped with the hardware. `tools/steam_controller_2015_mesh.py` and `tools/steam_controller_2026_mesh.py` do the conversion, and `tools/overlay_positions.py` builds the 2026 two-dimensional art from the same drawing.

## Solution Structure

```
PadForge.sln
├── PadForge.Engine/          (Class library -- net10.0-windows)
│   ├── Common/
│   │   ├── SDL3Minimal.cs         SDL3 P/Invoke declarations
│   │   ├── InputTypes.cs          Enums: MapType, ObjectGuid, InputDeviceType, etc.
│   │   ├── SdlDeviceWrapper.cs    SDL joystick/gamepad wrapper (open, read, rumble, GUID)
│   │   ├── SdlKeyboardWrapper.cs  SDL keyboard input wrapper
│   │   ├── SdlMouseWrapper.cs     SDL mouse input wrapper
│   │   ├── ISdlInputDevice.cs     Interface for SDL input devices
│   │   ├── CustomInputState.cs    Unified input state (axes, buttons, POVs, sliders)
│   │   ├── CustomInputHelper.cs   State comparison and update helpers
│   │   ├── CustomInputUpdate.cs   Buffered input change records
│   │   ├── DeviceObjectItem.cs    Device axis/button/POV capability metadata
│   │   ├── DeviceEffectItem.cs    Force feedback effect metadata
│   │   ├── ForceFeedbackState.cs  Rumble + SDL haptic state management
│   │   ├── GamepadTypes.cs        Gamepad/OutputState/ExtendedRawState types
│   │   ├── VirtualControllerTypes.cs  IVirtualController + VirtualControllerType enum
│   │   ├── RawInputListener.cs    Windows Raw Input listener
│   │   └── InputHookManager.cs    WH_KEYBOARD_LL / WH_MOUSE_LL input suppression hooks
│   ├── Data/
│   │   ├── UserDevice.cs          Physical device record (serializable + runtime)
│   │   ├── UserSetting.cs         Device-to-slot link (serializable)
│   │   └── PadSetting.cs          Mapping configuration (mappings, deadzones, FF)
│   └── Properties/
│       └── AssemblyInfo.cs
│
├── PadForge.App/             (WPF Application -- net10.0-windows10.0.26100.0)
│   ├── App.xaml / .cs             Entry point, ModernWpf resources, converter registration
│   ├── MainWindow.xaml / .cs      Shell: NavigationView + status bar + page switching
│   │
│   ├── Common/
│   │   ├── SettingsManager.cs     Static: device/setting collections, assignment, defaults
│   │   ├── ControllerIcons.cs     SVG path data for controller type icons
│   │   ├── DriverInstaller.cs     HIDMaestro, HidHide driver install/uninstall
│   │   ├── HidHideController.cs   HidHide IOCTL API (blacklist, whitelist, cloaking)
│   │   ├── StartupHelper.cs       Windows startup registry management
│   │   ├── VirtualKey.cs          Virtual key code definitions
│   │   └── Input/
│   │       ├── InputManager.cs                          Main partial: background thread, pipeline
│   │       ├── InputManager.Step1.UpdateDevices.cs      SDL enumeration, HIDMaestro filtering
│   │       ├── InputManager.Step2.UpdateInputStates.cs  State reading + force feedback
│   │       ├── InputManager.Step3.UpdateOutputStates.cs CustomInputState -> OutputState mapping
│   │       ├── InputManager.Step4.CombineOutputStates.cs  Multi-device merge per slot
│   │       ├── InputManager.Step4b.EvaluateMacros.cs    Macro evaluation (gamepad + extended)
│   │       ├── InputManager.Step5.VirtualDevices.cs     Virtual controller output (HIDMaestro / KBM / MIDI)
│   │       ├── InputManager.Step6.RetrieveOutputStates.cs  Copy combined output for UI
│   │       ├── HMaestroVirtualController.cs   HIDMaestro VC for Xbox / PlayStation / Extended types
│   │       ├── KeyboardMouseVirtualController.cs  Virtual keyboard + mouse output
│   │       └── MidiVirtualController.cs       Virtual MIDI device output
│   │
│   ├── Views/
│   │   ├── DashboardPage.xaml / .cs         Slot cards, engine stats, driver status
│   │   ├── PadPage.xaml / .cs               Mapping grid, deadzones, force feedback, macros
│   │   ├── DevicesPage.xaml / .cs           Card-based device list + visual raw input state
│   │   ├── ProfilesPage.xaml / .cs          Per-app profile management and auto-switching
│   │   ├── SettingsPage.xaml / .cs          Theme, engine, drivers, diagnostics
│   │   ├── AboutPage.xaml / .cs             App info, technology list, license
│   │   ├── ControllerModelView.xaml / .cs   3D interactive HelixToolkit viewport
│   │   ├── ControllerModel2DView.xaml / .cs 2D Canvas-based schematic with PNG overlays
│   │   ├── ControllerSchematicView.xaml / .cs  Alternative 2D schematic layout
│   │   ├── ProfileDialog.xaml / .cs         Save/edit profile dialog
│   │   └── CopyFromDialog.xaml / .cs        Copy mappings from another slot
│   │
│   ├── Models3D/
│   │   ├── ControllerModelBase.cs       Abstract base: OBJ loading, button map, materials
│   │   ├── ControllerModelXbox360.cs    Xbox 360 mesh loading (25 OBJ files)
│   │   ├── ControllerModelDS4.cs        DualShock 4 mesh loading (36 OBJ files)
│   │   └── 3DModels/
│   │       ├── DS4/                     DualShock 4 OBJ meshes
│   │       └── XBOX360/                 Xbox 360 OBJ meshes
│   │
│   ├── Models2D/
│   │   ├── ControllerOverlayLayout.cs   Layout data for 2D overlays
│   │   └── (generated position data)
│   │
│   ├── 2DModels/
│   │   ├── DS4/                         DualShock 4 PNG overlays (16 images)
│   │   └── XBOX360/                     Xbox 360 PNG overlays (21 images)
│   │
│   ├── ViewModels/
│   │   ├── ViewModelBase.cs            INotifyPropertyChanged base
│   │   ├── MainViewModel.cs            Root: navigation, pads, engine status, commands
│   │   ├── DashboardViewModel.cs       Overview: slot summaries, engine stats, driver info
│   │   ├── PadViewModel.cs             Per-slot: visualizer, mappings, deadzones, macros
│   │   ├── MappingItem.cs              Single mapping row: target, source, recording, options
│   │   ├── MacroItem.cs                Macro: trigger, actions, timing, button style, extended targets
│   │   ├── DevicesViewModel.cs         Device list, raw state display, slot assignment
│   │   ├── DeviceRowViewModel.cs       Single device: identity, status, capabilities
│   │   └── SettingsViewModel.cs        App settings: theme, engine, drivers, diagnostics
│   │
│   ├── Services/
│   │   ├── InputService.cs             Engine <-> UI bridge: 30Hz DispatcherTimer, state sync
│   │   ├── SettingsService.cs          XML persistence: load/save/reset/reload
│   │   ├── RecorderService.cs          Input recording: baseline -> detection -> descriptor
│   │   ├── DeviceService.cs            Device assignment and hiding
│   │   ├── DsuMotionServer.cs          DSU/Cemuhook UDP motion server (port 26760)
│   │   ├── ForegroundMonitorService.cs Per-app profile switching via foreground detection
│   │   └── WebControllerServer.cs     Embedded HTTP+WebSocket server for browser virtual controllers
│   │
│   ├── WebAssets/
│   │   ├── index.html                Landing page (Xbox 360 / DS4 layout selection)
│   │   ├── controller.html           Controller UI shell (dynamic PNG overlay layout)
│   │   ├── css/controller.css        Dark responsive touch-optimized styles
│   │   ├── js/controller_client.js   WebSocket client + touch input handling
│   │   └── js/nipplejs.min.js        Virtual joystick library for analog sticks
│   │
│   ├── Converter/                      WPF value converters (bool, axis, visibility, etc.)
│   ├── Controls/
│   │   └── RangeSlider.cs              Custom deadzone range slider control
│   │
│   ├── Resources/
│   │   ├── ControllerIcons.xaml        XAML icon resource dictionary
│   │   ├── PadForge.ico               Application icon
│   │   ├── SDL3/x64/SDL3.dll          Custom SDL3 fork (HIDMaestro filter, Switch 2 Pro)
│   │   ├── SDL3/x64/libusb-1.0.dll    libusb for WinUSB device access
│   │   ├── OpenXInput/x64/xinput1_4.dll  Custom XInput shim (filters HIDMaestro virtuals from PadForge's own view)
│   │   ├── */arm64/                   ARM64 copies of the native DLLs, beside each x64 folder
│   │   ├── HIDMaestro/HIDMaestro.Core.dll  HIDMaestro managed client
│   │   ├── HidHide_1.5.230_x64.exe    Embedded HidHide installer
│   │   └── Xbox Series Controller - *.png  Dashboard controller images
│   │
│   ├── Themes/
│   │   └── Generic.xaml               RangeSlider control template
│   └── Properties/
│       └── AssemblyInfo.cs
│
└── tools/
    ├── DsuDiag/                  DSU/Cemuhook diagnostic client
    │   ├── DsuDiag.csproj
    │   └── Program.cs            Real-time DSU slot data viewer
    ├── Ds4InputDump/             Raw DualShock 4 input dump for the PlayStation VC path
    │   ├── Ds4InputDump.csproj
    │   └── Program.cs
    └── overlay_positions.py      Extract 2D overlay positions from SVG assets
```

## Prerequisites

- .NET 10 SDK
- Windows 10 (build 26100+) or Windows 11 (x64)

All native DLLs, driver installers, and model assets are included in the repository under `PadForge.App/Resources/`, `PadForge.App/Models3D/`, and `PadForge.App/2DModels/`.

## NuGet Dependencies

**PadForge.Engine.csproj:**
```
(none -- pure P/Invoke, no third-party packages)
```

**PadForge.App.csproj:**
```
WPF-UI (>= 4.2.0)                       Fluent Design theme
HelixToolkit.Core.Wpf (>= 2.27.3)       3D viewport rendering
CommunityToolkit.Mvvm (>= 8.2.2)        MVVM data binding
Microsoft.Windows.Devices.Midi2 (>= 1.0.16-rc.3.7)  Virtual MIDI device output
NAudio.Wasapi (>= 2.2.1)                WASAPI loopback for audio bass rumble
```

## Build

```bash
dotnet publish -c Release PadForge.App/PadForge.App.csproj
```

Output: `PadForge.App/bin/Release/net10.0-windows10.0.26100.0/win-x64/publish/PadForge.exe` (single-file, self-contained)

> **Note:** Always use `dotnet publish`, not `dotnet build`. The project is configured for single-file publish with self-contained runtime.

> **Build from the .NET SDK, not Visual Studio's MSBuild.** The 3D art is packed at
> build time by an inline task that needs Brotli, which exists on the .NET SDK's
> runtime and not on the .NET Framework build of MSBuild that Visual Studio uses.
> Building from the IDE fails before any compile with a message naming this. Use the
> `dotnet` CLI, or point Visual Studio at it.

### ARM64 (preliminary, 4.5.1)

```bash
dotnet publish -c Release -r win-arm64 PadForge.App/PadForge.App.csproj
```

Output: `PadForge.App/bin/Release/net10.0-windows10.0.26100.0/win-arm64/publish/PadForge.exe`. With no `-r` the build is x64, so existing scripts produce what they always have.

Every bundled native binary sits in a folder named for its architecture: `Resources/SDL3/x64` and `Resources/SDL3/arm64`, and the same pair for `OpenXInput` and `VisualCpp`. `Interhaptics` has an `x64` folder only. The `$(NativeArch)` property picks the folder. `NativeBinaryArchitectureTests` reads the PE header of every file in those folders and fails when a file's machine type differs from its folder name.

A publish for either architecture is refused if `SDL3.dll` or `libusb-1.0.dll` is missing from `Resources/SDL3/<arch>`, or `xinput1_4.dll` from `Resources/OpenXInput/<arch>`. The three `Content` items are conditioned on `Exists`, so without the `RequireBundledNatives` target a missing file would publish anyway, and the auto build runs no tests that would notice. Without `SDL3.dll` the input engine cannot start. Without `libusb-1.0.dll` wired Switch 2 controllers and the GameCube adapter never open. Without the fork's `xinput1_4.dll` SDL loads the system one, and PadForge reads its own virtual controllers back as input. A publish for any runtime other than `win-x64` and `win-arm64` is refused too, since it would be handed the x64 libraries. `SDL3.dll` and `xinput1_4.dll` come from builds of the two forks, the ARM64 pair cross-compiled on an x64 machine with `cmake -A ARM64`. A plain `dotnet build` compiles without any of them.

Three features have a native half, and `PadForge.Engine/Common/PlatformSupport.cs` decides each one by the architecture that half follows:

| Feature | Decided by | On ARM64 |
|---|---|---|
| HidHide | Machine architecture | A kernel driver cannot run emulated, so either build installs HidHide's Microsoft-signed ARM64 driver (`Resources/HidHideArm64/HidHide_ARM64.zip`, driver 1.6.280.0) with HidHide's own tool (`nefconc.exe`, nefcon 1.20.0, ARM64). `HidHideArm64Installer` follows the order of HidHide's setup. An x64 machine gets the x64 MSI |
| Vosk voice engine | Process architecture | The ARM64 build bundles `Resources/Vosk/arm64/libvosk.dll`. The Vosk package carries the x64 one |
| Razer Sensa HD haptics | Process architecture | Not available. The Interhaptics SDK ships for Win32 and x64 only, and Razer lists Synapse for x86-64 Windows only |

Each rule names the architectures that have the native half and answers false for every other.

`libvosk.dll` for ARM64 is not published by anyone. Vosk's maintainer wrote a Windows ARM64 recipe (vosk-api 1b308a30, `travis/Dockerfile.winaarch64`) and never shipped its output. `tools/build-libvosk-arm64.sh` is that recipe with the same flags and every source pinned to a commit, run from Git Bash with a portable llvm-mingw toolchain, and its header lists each difference from upstream with the reason. It links the C++ runtime statically, so the DLL imports `KERNEL32` and the Universal C Runtime alone, where the x64 one needs three MinGW DLLs beside it. The same recipe aimed at x64 produced recognition output identical to the official x64 library, word timings included. The ARM64 DLL has not run, because the bench is x64. `BundledVoskArm64Tests` checks that it exports every function the managed binding calls and needs no companion DLL.

A HidHide class filter entry that names a driver which is not running can stop every keyboard and mouse from starting. HidHide's x64 setup ships a watchdog service for that. An ARM64 install has none, so `HidHideArm64Installer` adds the filters only after the driver's control device opens, removes them before it removes the driver, and at startup on an ARM64 machine takes out any filter entry whose driver is gone. `HidHideArm64InstallerTests` pins that order and the hashes of both bundled files.

Four more features load a library the vendor's own software installs, and PadForge has no gate for them because the load itself answers. `LogitechGkey.dll` comes in x64 and x86 only (`LogitechGKeyCatalog`), and the SteamVR input service loads `bin\win64\openvr_api.dll` (`OpenVrConsumerService`), so an ARM64 process gets neither. The LIGHTSYNC engine and the OpenXR runtime are whatever the registry names, so they work in an ARM64 process only if the vendor registers an ARM64 one. None of these crashes the app. G-keys, LIGHTSYNC and OpenXR each show a status line, and the SteamVR input service logs the failed load and keeps retrying.

One more gap is decided in the SDL fork, not in PadForge. The fork's Xbox Elite paddle reader (`SDL_XINPUT_PADDLES`) builds for x64 and, since fork commit a1416320e2, for ARM64. It has two routes. The Bluetooth route uses public WinRT calls and runs on both. The USB and Xbox Wireless Adapter route reads an undocumented format from the Windows GameInput service, so the fork switches it on only where five Windows files match a profile it has checked, by size and SHA-256. That profile is of x64 Windows, and no ARM64 installation matches it, so an ARM64 `SDL3.dll` reads Elite paddles over Bluetooth only. An ARM64 profile needs an ARM64 PC with an Elite controller (hifihedgehog/SDL#31). Whether the x64 build reads paddles under emulation is untested: there the five files it hashes are the ARM64 ones, so its USB route refuses as well.

Drivers that install into Windows follow the machine. HIDMaestro 1.9.0 and BthPS3 3.0.0 each carry an x64 and an ARM64 payload and install the one that matches, the DualShock 3 WinUSB package is signed with the matching catalog OS, and the Windows MIDI Services download picks the `-arm64` installer on an ARM64 machine.

Of the Visual C++ runtime, both builds bundle `vcruntime140.dll` and `msvcp140.dll`. `SDL3.dll` imports `msvcp140.dll` for the Elite paddle reader, which is C++. `vcruntime140_1.dll` is x64 only. It holds an exception handler that exists for the x64 ABI alone, and the copy in Microsoft's ARM64 redist folder is an x64 image. `BundledSdlRuntimeImportsTests` reads each bundled `SDL3.dll` for the runtime DLLs it names and fails when one is not in that architecture's `Resources/VisualCpp` folder. It did exactly that when the ARM64 `SDL3.dll` first arrived with the paddle reader in it.

None of the ARM64 path has run on ARM64 hardware. The bench is x64.

## Runtime Requirements

1. **SDL3.dll** -- Included in the repo (`Resources/SDL3/x64/`). Custom fork with HIDMaestro
   filtering and WinUSB support for Switch 2 Pro Controller. Copied to the output directory
   automatically.

2. **HIDMaestro** -- Required for all gamepad-style virtual controllers (Xbox, PlayStation,
   Extended). The app embeds the HIDMaestro installer and managed client; no separate install step.

3. **OpenXInput shim** (`xinput1_4.dll`) -- Custom XInput replacement DLL embedded in the
   single-file build under `Resources/OpenXInput/x64/`. Filters HIDMaestro virtual
   controllers out of PadForge's own XInput view. Loaded via `SetDllDirectory` preload.
   Do NOT ship the fork's `devobj.dll`: it is a link-time stub, and bundling it once
   hijacked the real System32 devobj.dll process-wide and crashed setupapi.

4. **HidHide** (optional) -- For hiding physical controllers from games. Built-in installer included.

## Architecture Notes

### Threading Model
- **InputManager** runs a background thread at configurable polling rate (default ~1000Hz).
  Uses hybrid sleep/spin-wait for sub-ms precision.
- **InputService** runs a DispatcherTimer on the UI thread at ~30Hz.
- State transfer: InputManager writes to `CombinedOutputStates[]` and `CombinedExtendedRawStates[]`;
  InputService reads them and pushes to ViewModels.
- All ViewModel property sets happen on the UI thread.

### 6-Step Pipeline (per cycle)
1. **UpdateDevices** -- SDL enumeration, open new, detect disconnections, filter HIDMaestro virtuals
2. **UpdateInputStates** -- Read axes/buttons/POVs/sensors from SDL; apply force feedback + haptic
3. **UpdateOutputStates** -- Map CustomInputState -> OutputState via PadSetting descriptors
4. **CombineOutputStates** -- Merge multiple devices per slot (OR/MAX/largest-magnitude)
   - **4b. EvaluateMacros** -- Process macro triggers and actions (gamepad + extended paths)
5. **VirtualDevices** -- Submit state to HIDMaestro via `HMContext.SubmitState` / `SubmitRawReport`; KBM and MIDI VCs emit through their respective backends
6. **RetrieveOutputStates** -- Copy combined output for UI display

### Virtual Controller Types
All gamepad-style virtuals run on HIDMaestro via `HMaestroVirtualController.cs`:
- **Xbox** -- Xbox 360 layout, up to `MaxPads` (16) simultaneous (XInput visibility caps at 4)
- **PlayStation** -- DualShock 4 layout, up to 16 simultaneous
- **Extended** -- Fully custom HID descriptors, up to 16 simultaneous

Non-gamepad virtuals:
- **KeyboardMouse** -- `KeyboardMouseVirtualController.cs`, up to 16 simultaneous
- **MIDI** -- `MidiVirtualController.cs` via Windows MIDI Services 2, up to 16 simultaneous

### Mapping Descriptors
String format: `"[I][H]{Type} {Index} [{Direction}]"`
- `Button 0`, `Axis 1`, `IHAxis 2`, `POV 0 Up`, `Slider 0`
- Prefixes: `I` = inverted, `H` = half-axis, `IH` = inverted half

### Controller Visualization
- **3D View** (`ControllerModelView`): HelixToolkit.WPF viewport with OBJ meshes from Handheld Companion.
  Xbox 360 (25 parts) and DualShock 4 (36 parts). Mouse/touch rotation, zoom, pan.
- **2D View** (`ControllerModel2DView`): Canvas with PNG overlays from Gamepad-Asset-Pack.
  Button/stick/trigger state shown via opacity toggling on overlay images.

### Settings File (PadForge.xml)
```xml
<PadForgeSettings>
  <Devices><Device>...</Device></Devices>
  <UserSettings><Setting>...</Setting></UserSettings>
  <PadSettings><PadSetting>...</PadSetting></PadSettings>
  <AppSettings>...</AppSettings>
  <Macros><Macro>...</Macro></Macros>
  <Profiles><ProfileData>...</ProfileData></Profiles>
</PadForgeSettings>
```

### DSU Motion Server
- UDP server on port 26760 (Cemuhook protocol)
- Broadcasts gyro/accelerometer data from SDL sensor-capable controllers
- Compatible with Cemu, Dolphin, and other DSU clients
- Diagnostic tool: `tools/DsuDiag/`

### Diagnostic Tools
- **DsuDiag** (`tools/DsuDiag/`) -- Real-time DSU protocol client showing per-slot motion data
- **Ds4InputDump** (`tools/Ds4InputDump/`) -- Raw DualShock 4 input dump for debugging the PlayStation VC path
