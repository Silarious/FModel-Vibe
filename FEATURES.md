# FModel Vibe — Feature List

Fork additions on top of base FModel. Use at your own risk.

App identity: `FModel_Vibe` (separate AppData; does not conflict with stock FModel).

## Branding
- Separate executable / AppData folder (`FModel_Vibe`)
- Upstream update checker disabled

## Arc Raiders / Theia
- On-read Theia decrypt when UE version is Arc Raiders (mount Steam Paks as-is; no decrypt-all cache)
- Sibling `.meta` → Theia on-read; no `.meta`/`.sig` → treat as already Theia-decrypted
- Directory → Export Decrypted Paks (plaintext `.pak`/`.ucas` + `.utoc`; does not copy `.meta`/`.sig`)
- Soft-fail bad ContainerHeader chunks (warn and continue instead of hard error)
- Convert uint64 → float toggle for Properties/JSON (Arc Raiders–oriented)

## Profiles
- Save / Load / Rename / Delete named profiles (full settings snapshot)
- Settings → Profiles tab with editable preview before Load
- Selecting another profile previews saved values; Load applies and restarts
- Top-level Profiles menu for quick switching
- Save Current Settings as New Profile (Directory selector + Profiles menu)

## Diff Checker
- Top-level Diff Checker window (compare two profiles or manual pak mounts)
- Shared AES / mapping / engine version; separate old vs new pak folders
- Inventory diff by path (added / modified / removed), including size deltas
- Export delta packages (Properties / Raw / Textures / Models / Audio)
- Optional Populate Explorer with the delta set
- Optional Write Diff Log File (`## ADDED` / `## MODIFIED` / `## REMOVED`)
- Optional Export Removed Files (from the old side into `Removed/`)
- Live progress + Stop Diff cancellation

## Export Queue
- Queuing Mode toggle
- Queue folder exports from the right-click menu
- Queue global exports across everything currently loaded
- Review, remove, and run queued exports back-to-back

## Export Behavior
- Skip Already Exported Files (by name, or name + non-empty size)
- Overwrite Protection when skip is off (refuse overwrite if existing size differs)
- Export Smallest Files First
- Auto Export Textures with Models
- Auto Load All Files on Startup (blocked if AES is missing)
- Metadata Export: Disabled / Auto Export / Append to Json
- Export Metadata (right-click, single file and folder bulk)

## License
FModel is licensed under [GPL-3](https://github.com/4sval/FModel/blob/dev/LICENSE). Third-party licenses: [NOTICE](https://github.com/4sval/FModel/blob/dev/NOTICE).
