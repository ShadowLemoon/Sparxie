# The game's cloud-gaming mode and how the Runtime uses its design (cloud_mode)

Source: static reading of the game's `global-metadata.dat` (Unity 2021.3, IL2CPP metadata v29), of `GameAssembly.dll` (method code located through the metadata, disassembled) and of `UnityPlayer.dll`.
What the Runtime reports at run time is in `endfield-runtime.log` (`cloud:` lines).

## 1. The engine switch
- `Beyond.CloudGame.get_enabled` is a thin wrapper around the engine call `UnityEngine.Application::get_isCloudGame()` (an engine addition in `UnityPlayer.dll`).
- The engine computes it once from its boot arguments, and the mode is gated by a key the engine checks, not just by a flag. cloud_mode does not turn `isCloudGame` on and does not touch that check.

## 2. What cloud mode changes in the managed code (callers of `CloudGame.get_enabled`)
`DeviceInfo.get_userPlatform` (see 3), `GameApp._OnSystemDisplaySizeChanged`, `GameSetting.GetCurVideoNotchPaddingId` (the notch setting id is built from the device safe area instead of the aspect ratio),
`GameSettingHelper._TryGetSettingRuleValue`, `ResourcePreferencesManager.GetResourcePreferred/_GetDefaultSet/_SetResourcePreferred`, `LoginMenuPanel.OnValueChanged`, `HGNetSession.DoReconnectLogin`,
`SDKAccountUtils.GetChannel/GetSubChannel` (the login channel comes from `CloudGame.channel`), `SDKUtils.GetPresetPropertyByKey`, `UIInputField.TryActivateInputFieldCustom` (text input goes to the cloud client's keyboard:
`CloudGame.OpenKeyboard`), `CloudGame.get_isOnlyUpdateMode/get_isWarmupMode` (login work modes).

## 3. Device platform versus user platform
Cloud design: the game runs on a PC (device platform) for a user on a phone (user platform). `Beyond.DeviceInfo.get_userPlatform()` returns `CloudGame.platform` when cloud mode is on and `DeviceInfo.platform` otherwise
(RuntimePlatform values: WindowsPlayer 2, iPhonePlayer 8, Android 11). The UI asks the USER platform:
- `UIConst.GetResolutionScale()` (used by `UICanvasScaleHelper.get_standardResolutionScale`, `UIConst.CUR_STANDARD_*`, `UIPCScaleHelper`, `UIWorldCanvasResolutionScaleFit`, `QTESafeAreaConst`): `DeviceInfo.isPCorConsoleUser` -> `PC_REFERENCE_RESOLUTION_SCALE` (1.25), otherwise the mobile value
  (the game also has `MOBILE_PAD_DEVICE_REFERENCE_RESOLUTION_SCALE` = 1.1 for pads; the phone value is read from the log at run time: `UIConst.GetResolutionScale`). This is why the menus (UIRoot reference 2400x1350 = 1920x1080 x 1.25) look small on a PC.
- `DeviceInfoManager.NotchPadding()` / `StandardNotchPadding()` / `NotchPaddingInCanvas()` (the notch padding the `NotchAdapter` applies to side UI) read `DeviceInfo.safeArea` and test the user platform.
- `DeviceInfo.isMobileUser/isIOSUser/isAndroidUser/isPCUser` -> mobile UI layers (`PlatformLayer.GetUserPlatformLayer`), gyroscope effects, foot bars, screen capture, remote factory env.
- `DeviceInfo.safeArea` -> the provider: `LocalDeviceInfoProvider.get_safeArea` = `Screen.safeArea` (an engine icall; the full window on a PC); `CloudDeviceInfoProvider.get_safeArea` = `CloudGame.safeArea`.

## 4. Command-line arguments of cloud mode (`Beyond.CloudCommandLineDefs`, `CloudGame._ParseCommandLineArgs`: arguments that start with `-`, value in the next argument)
| key | value format |
|---|---|
| `cloud` | flag (KEY_CLOUD) |
| `cloud_platform` | integer enum `CloudPlatform` (0 / 1 = iOS / Android; others: "Undefined CloudPlatform value") |
| `cloud_is_web` | bool (web client or app client) |
| `cloud_display_resolution`, `cloud_screen_resolution` | `WIDTHxHEIGHT`, both positive (regex `^(?<width>\d+)x(?<height>\d+)$`) |
| `cloud_safe_area` | `LEFTxTOPxRIGHTxBOTTOM` four integers (regex `^(?<left>\d+)x(?<top>\d+)x(?<right>\d+)x(?<bottom>\d+)$`; `CloudGame._CalculateSafeArea` scales it by `resolutionScale`) |
| `cloud_update_game`, `cloud_warmup_game` | flags (login work modes) |
`CloudGame.Initialize()` (called from Lua) reads them (`_InitializeBasicInfo/_InitializeDeviceInfo/_InitializeResolution`), registers callbacks on the cloud bridge (`_GetBridge`, `SendDataWithKey`,
`RegisterGlobalCallback`) and waits for the cloud pipeline (`_OnPipelineConnected` -> `_ParseDeviceProperties(json)`) for the rest of the client's properties.

## 5. What cloud mode needs that a local session cannot provide
1. The key the engine checks (see 1).
2. A cloud bridge and client: channel/sub-channel (login), text input (keyboard), kick-out and reconnect all go through it; locally the login channel would be the empty default and text fields would call a bridge that is not there.
So the Runtime does NOT switch `isCloudGame` on.

## 6. What `cloud_mode=on` does instead (endfield_cloud.inc)
It gives the game's own UI code the one input the cloud client would give it, with the same two data points: the user platform and the device safe area.
- The game's methods `DeviceInfo.get_userPlatform` and `DeviceInfo.get_safeArea` start with a check of a table of function-pointer slots (the game's hot-fix extension table). If a slot is filled, the method calls that function
  instead of its own body. The Runtime fills exactly these two slots. The slot table is found through the method's own machine code (a checked byte pattern: global, table offset, slot offsets; nothing is hard-coded),
  and the memory behind the slots is checked to be committed and writable first. No code is patched.
  - userPlatform -> iOS (8) for iPhone/iPad profiles, Android (11) for Android profiles; this is what `CloudGame.platform` would be.
  - safeArea -> `Rect(x = left inset, y = bottom inset, w = W - left - right, h = H - top - bottom)` in screen pixels from the device profile (`safe_area` in the ini; the 2868x1320 iPhone 17 Pro Max row = 186 px per side).
- Everything else (UI scale = the phone value, mobile UI layers, notch padding, canvas layout) is the game's own computation from these two inputs. `ui_scale` then only adds the extra factor above the phone size
  (ui_scale / PC multiplier: 1.40 / 1.25 = 1.12 for the 17 Pro Max class) through the standard-resolution knob.
- `cloud_mode=on` is the default. `cloud_mode=off` (live) puts both slots back to what they were (empty) and asks the UI to lay out again.
- The Runtime logs `cloud state (...)`: userPlatform, isPCUser/isMobileUser/isIOSUser, DeviceInfo.safeArea, DeviceInfoManager.NotchPadding, UIConst.GetResolutionScale, hook call counts, and `UI state` for UIRoot / CanvasRoot.
- In a session without a hot-fix the slot page does not exist: the game creates it only when a hot-fix is loaded. See 7.
- Still not verified in the game: the side effects of a mobile user platform on `GameSettingHelper._TryGetSettingRuleValue` and `RemoteFactoryManager._CreateGlobalEnv`.

## 7. The slot table (static reading of GameAssembly.dll, same build as the game install)
- The global the methods read (one global for all ~94 000 hot-fix checks of the main assembly) holds a **native page directory**, not a managed object: at metadata load the runtime allocates
  `calloc(ceil(n / 0x1000), 8)` through the IL2CPP memory callbacks (`n` = number of hot-fixable methods, read from a section of `global-metadata.dat`), stores it in the global, and keeps `n`, the page
  count and a sorted (method, slot index) map next to it. No object header, no length field, no bounds check on the read side.
- A method's check is `page = dir[index >> 12]; if (page && page[index & 0xFFF].fn) return fn(page[...].ctx, args...)`, page entries 16 bytes (function, context). `+0x78` is page 15. Across the binary the
  directory offsets read are `+0x00 .. +0x98` (20 pages) and the largest slot offset read is `+0xFFF0`, i.e. exactly one page of `0x1000 x 0x10 = 0x10000` bytes. Every reader only tests page and function
  for null; with a page whose entries are zero every method runs its own body. Nothing else keys off a non-null page.
- Only five runtime functions touch the directory: init (above, once), the slot setter (method -> index through the map; if the page is null it is created with `calloc(0x1000, 0x10)` under the runtime's
  lock, then context and function are stored), clear one slot, clear all slots, and shutdown (frees every non-null page and the directory with the IL2CPP free callback, then nulls the global).
- Consequence for the Runtime (endfield_cloud.inc): when the page is missing it creates it the way the setter does: `0x10000` zeroed bytes from `il2cpp_alloc` (the game's malloc callback, so the
  shutdown's free is correct; VirtualAlloc memory would be handed to that free), stored by compare-exchange only while the entry is still null (if the game wins the race, ours is freed unseen).
  A watch every second fills the two slots again if the game puts in its own page, a new directory, or empties the slots; if the directory disappears the Runtime stops writing. cloud_mode=off empties
  the two slots and leaves the page. If the slots cannot be filled within ~10 s of the first attempt, the session falls back to the PC path (UI size knob + notch setting, as with cloud_mode=off).
- Log lines: `cloud: PATH A active ...` or `cloud: PATH B active (fallback ...)`, `cloud: the game has no slot page here ... created one ...`, `cloud: ... slots filled again`, and at 1 s / 10 s / 30 s
  `cloud state (...)`, `UI state (...)`, `UI scale (...)` (canvas scaleFactor of UIRoot, CanvasRoot, one open menu panel) and `notch (...)` (DeviceInfoManager values and the NotchAdapter margins).
