# config.json: what Sparxie does with a file it cannot take as it is

Why this exists: a client wrote an Endfield profile with `endfieldWidth/Height: 0` ("follow the stream"). `AppConfigValidator` rejected the size,
`AppConfigStore` moved the whole file to `config.invalid-*.json` and started a new one, and every profile was gone.

## Rules

1. **One bad profile never discards the file.** `ConfigReader` reads the profiles one at a time.
   - A field outside its range goes back to its default: fps values (10–1000) → 120 / 10 / 60 / 1000, touch UI scale (100–500) → 400, `processPriority` → normal,
     Endfield refresh rate (24–1000) → 60, Endfield input other than touch|pc → touch.
   - A fixed Endfield size outside 320×240–7680×4320 cannot be used: the profile follows the stream instead (`endfieldAutoResolution: true`, size 1920×1080).
   - A field of the wrong type is dropped (default). `hoyo` on a Zenless Zone Zero profile is removed. A missing `displayName` takes the id.
   - A profile that cannot be repaired (not an object, unknown `game`, an EXE that is not on the whitelist, no usable `id`, a duplicate `id`) is left out. The others stay.
2. **The Endfield size is only checked while it is fixed.** With `endfieldAutoResolution: true` the width and height are not looked at (0 is accepted from older hosts).
3. **The original is always kept.** When anything was repaired or left out, the original bytes are written first to `config.repaired-<utc>-<id>.json` beside `config.json`;
   only then is the repaired file saved. If the copy or the save fails, `config.json` is left alone and the load fails (`ConfigLoadState.Failed`).
   Every change is in `AppConfigLoadResult.Changes`, and `Sparxie.Desktop` writes each line to `logs\desktop.log`.
4. **A file that is not a config at all** (not JSON, root is not an object, another `schemaVersion`, no `profiles` list) is the only case that moves the whole file:
   the bytes go to `config.invalid-<utc>-<id>.json`. Then Sparxie looks for the newest readable file beside it that has at least one profile,
   among `config.backup-*.json` (left by a client before each of its writes), `config.repaired-*.json` and `config.invalid-*.json`, in that order of recency.
   If there is one, it is restored (`ConfigLoadState.RestoredFromBackup`, `RecoveredFrom` names it); if not, a blank config is created (`RestoredFromCorrupt`).
5. **A blank but valid `config.json` is never filled from a backup on its own** (the person may have removed the profiles). If a readable file with profiles is beside it,
   `RecoverableBackupPath` and a line in `Changes` say so.

## Tests

`tests/Config.Tests/ConfigSurvivalTests.cs`: the file with the 0×0 Endfield size loads with all seven profiles (`endfield-follow-stream-0x0.json`); a fixture with every game and option
(`profile-matrix.json`) passes the real validator; repair, quarantine, backup and recovery cases.
