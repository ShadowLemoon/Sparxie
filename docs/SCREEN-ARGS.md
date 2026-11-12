# Session screen arguments (Broker protocol, optional)

`ProfileSnapshot.screen` (field 7, message `ScreenArgs { width = 1, height = 2, window_mode = 3 }`) lets the client choose the size and the window mode of
one session. The Broker validates it and the session puts it on the game's command line as Unity player arguments. An older client that sends nothing
gets the previous behaviour (no arguments); an older Broker ignores the field.

| `window_mode` | Unity arguments |
| --- | --- |
| `fullscreen` | `-screen-fullscreen 1` |
| `windowed` | `-screen-fullscreen 0` |
| `borderless` | `-screen-fullscreen 0 -popupwindow` |
| (empty) | none |

`width`/`height` (both or neither, 320–16384) become `-screen-width W -screen-height H`, placed before the window arguments. Only integers and these fixed tokens are
ever built, so the field cannot carry text onto the game's command line. A bad object makes the Broker refuse the session (`InvalidArgument`).

Where it lands, per title:

| Title | Process creation | Arguments |
| --- | --- | --- |
| Genshin Impact, Honkai: Star Rail | HoyoTouchCore (`hoyo_launch`, `CREATE_SUSPENDED`) | `"<exe>" <arguments>` as the `lpCommandLine` of `CreateProcessW` (`game_arguments` of the launch request) |
| Zenless Zone Zero | SessionHost (`Process.Start`) | `ProcessStartInfo.Arguments` |

When the profile also has launch arguments of its own, Genshin and Star Rail use those (the user's own arguments win over the session's).

`hoyo_get_launch_command` returns the command line the last launch gave `CreateProcessW`, and `logs\game-launch.log` has one line per session with the arguments, so a
launch can be checked without starting the game again.

## Sources

- Unity manual, *Standalone Player command-line arguments*: `-screen-width`, `-screen-height`, `-screen-fullscreen` (0 or 1), `-popupwindow` ("Create the window as a
  dialog window, without a frame"), `-window-mode` (exclusive or borderless).
- Collapse Launcher (MIT), `GetLaunchArguments` of each title: Genshin `GameSettings/Genshin/Settings.cs`, Star Rail `GameSettings/StarRail/Settings.cs`, Zenless
  `GameSettings/Zenless/Settings.cs`, Honkai Impact 3rd `GameSettings/Honkai/Settings.cs`: `-screen-width/-screen-height` and `-popupwindow` (and `-window-mode exclusive
  -screen-fullscreen 1`). Starward (MIT), `GameLauncherService.cs`: `-popupwindow`. Only the behaviour is used; no code was copied.
