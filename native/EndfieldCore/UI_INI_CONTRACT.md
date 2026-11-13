# endfield-ui.ini — contract between Sparxie and the Endfield Runtime

File: `endfield-ui.ini`, in the same folder as `EndfieldRuntime.dll`. Plain text, one `key=value` per line, keys case-insensitive, no sections, no comments. UTF-8 / ASCII, only the first 2 KB are read.
A missing file or a missing key means the default. The defaults are the values Sparxie ships.

## Keys Sparxie owns (the touch UI card)

| key | values | default | meaning |
|---|---|---|---|
| `ui_scale` | `auto` \| `off` \| a number from `1.00` to `1.80` | `auto` | Size of the game UI relative to the PC build's default size. `auto` = per client device (table keyed by stream resolution): iPhone 17 Pro Max / 16 Pro Max class (2868×1320) = `1.35`; every other device = the phone build's size (the game's PC multiplier removed, `1.25` in the shipped game). A number overrides the table, e.g. `1.3`, `1.35`, `1.4`. `off` = the game's own size. Numbers outside 1.00–1.80 are clamped; text that is not a number means `auto`. |
| `safe_area` | `auto` \| `off` \| `<left>,<right>` | `auto` | Notch / side safe area. `auto` = from the client device (table keyed by stream resolution, e.g. 2868×1320 = iPhone 17 Pro Max → 186 px per side; iPads, notch-less phones and unknown sizes = 0). `off` = the game's own setting is not touched. `<left>,<right>` = pixels in **stream** coordinates, e.g. `186,186`; `0,0` means no inset. |

## Keys of the Runtime (Sparxie keeps them as they are)

| key | values | default | meaning |
|---|---|---|---|
| `cloud_mode` | `on` \| `off` | `on` | The game's own cloud-gaming inputs without the cloud: the Runtime tells the game that the user plays on the stream's client device (iOS/Android user platform, device safe area), and the game's own UI code does the phone scale, the mobile layers and the notch padding. `ui_scale` is the size above that. Switched live. See CLOUD_MODE.md. If the inputs cannot be set within about 10 s, the session uses the PC path: the UI size through the scale helper's own fields and the notch through the game's notch setting (restored when the session ends). |
| `ui_pointers` | `all` \| `0` \| `1` \| `2` \| `off` | `all` | The `pointerBehavior` the UI input module is held at; `all` = `2` = AllPointersAsIs (every finger keeps its own pointer record). `off` = the game's own value. |
| `touch_mouse` | `drop` \| `off` | `drop` | The mouse messages Windows makes from touches are removed before the game reads them (the touch itself arrives as WM_POINTER). |
| `drag_px` | `auto` \| `<pixels 1-400>` \| `off` | `auto` | `EventSystem.pixelDragThreshold`: `auto` = 0.1 inch at the client's pixel density (table keyed by stream resolution; 1.5 % of the stream height otherwise), never below the game's own value. |
| `observe` | `1` \| `0` | `1` | Read-only log lines about new canvases and scalers. |

Lines with other keys are ignored by the Runtime and must be kept by Sparxie when it rewrites the file.

## Behaviour

- The Runtime re-reads the file about every 5 s. A changed `ui_scale` or `safe_area` is applied **once**, live, from the game's original values (never from the last written value), followed by one relayout. Unchanged values cause no writes. Every panel the game opens brings its own scale helper; the Runtime puts the held size into that helper before its first layout, so a new panel does not first appear small and then jump.
- `ui_scale=off` and `safe_area=off` put the original values back when they were changed earlier in the session.
- Write the file **before launch** and, for live changes, atomically (a temp file next to it, then rename).
- Verification: `logs\endfield-runtime.log` lines `config (...)`, `device profile: ...`, `cloud: PATH A active ...` or `cloud: PATH B active (fallback ...)`, `cloud state (...)`, `UI size: ...`, `notch (...)`.
