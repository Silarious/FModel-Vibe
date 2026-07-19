# Vibe coded additions to FModel

All additions to the program were made by Claude, I am not claiming anything else, nor that I am knowlegable with coding.

I needed some features which have been requested many times and argubly should already be in the main branch, use at your own risk.

There are also features added specifically for Arc Raiders such as the Uint64 conversion, just dont enable it if you dont need it.

PRs, issues and discussion disabled, I will never try to request this branch be put into the main build.

Join my discord server if you absolutely need a place to discuss this. https://discord.silarious.online/

# FModel Vibe — Added Features

## Branding
- Separate executable / AppData folder (`FModel_Vibe`)
- Upstream update checker disabled

## Export Behavior
- Skip Already Exported Files (3 stage)
	- Disabled/Skip Based on File Name/Skip Based on File Name & Size
- Overwrite protection
	When skip files is off this prevents overwriting files that are different, that would suggest you are exporting to the wrong folder/version of game
- Export Smallest Files First (toggle)
- Auto Export Textures with Models (toggle)
- Auto Load All Files on Startup (toggle)
- Convert uint64 to Float (toggle, for Properties/json export)
  - Added specifically for Arc Raiders json files, may also apply to other games to make jsons human readable.
- Metadata Export dropdown: Disabled / Auto Export / Append to Json
![Export Features](https://i.imgur.com/XFmBgZR.jpeg)

## Metadata
- Export Metadata button (right-click menu, single file and folder bulk export)
- Append to Json mode (merges metadata into the bottom of the properties json instead of a separate file)

## Profiles
Allows for easy switching between different games or versions without having to manually change per file directories each time.
- Save/Load/Rename/Delete named profiles (full settings snapshot, including per-file-type export directories, UE Version, and Pak Archive)
- Save Current Settings as New Profile (Directory dropdown + top menu)
- Profiles tab in Settings (list + editable preview of directories, mapping file, AES key, UE Version, Pak Archive)
- Top-level Profiles menu for quick switching between profiles
![Profiles-Screenshot](https://i.imgur.com/6eXVdad.jpeg)

## Diff Checker
- Top-level Diff Checker window (compare two profiles or manual pak mounts)
- Shared AES / mapping / engine version; separate old vs new pak folders
- Inventory diff by path (added / modified / removed), including size deltas
- Export delta packages (Properties / Raw / Textures / Models / Audio)
- Optional Populate Explorer with the delta set
- Optional Write Diff Log File (`## ADDED` / `## MODIFIED` / `## REMOVED`)
- Optional Export Removed Files (from the old side into `Removed/`)
- Live progress + Stop Diff cancellation
![DiffChecker](https://i.imgur.com/k9FIecS.jpeg)

## Export Queue
The ability to queue/schedule exports and run the job after selecting folders or global exports.
- Queuing Mode toggle
- Queue folder exports from the right-click menu (any mix of folders/export types, any order)
- Queue global exports (e.g. all properties, all models) across everything currently loaded
- Review, remove, and run queued exports back-to-back from the Export Queue window
![Export Queue](https://i.imgur.com/1Qmq7Fu.jpeg)

## Arc Raiders / Theia
- On-read Theia decrypt when UE version is Arc Raiders (mount Steam Paks as-is; no decrypt-all cache)
- Sibling `.meta` → Theia on-read; no `.meta`/`.sig` → treat as already Theia-decrypted
- Directory → Export Decrypted Paks (plaintext `.pak`/`.ucas` + `.utoc`; does not copy `.meta`/`.sig`)
- Soft-fail bad ContainerHeader chunks (warn and continue instead of hard error)
- Convert uint64 → float toggle for Properties/JSON (Arc Raiders–oriented)

### License:
FModel is licensed under [GPL-3](https://github.com/4sval/FModel/blob/dev/LICENSE), and licenses of third-party libraries used are listed [here](https://github.com/4sval/FModel/blob/dev/NOTICE).