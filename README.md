# FModel-Vibe

Fork of [FModel](https://github.com/4sval/FModel) with extra tooling for bulk export, search indexing, profiles, and Arc Raiders Theia decryption.

This README documents **only FModel-Vibe additions**. For upstream usage, see the [FModel repository](https://github.com/4sval/FModel).

All additions are AI generated, use at your own risk. PRs/issues on this fork are disabled. Discord: https://discord.silarious.online/

---

## Branding

- Separate executable and AppData folder (`FModel_Vibe`) so settings do not clash with upstream FModel
- Upstream release/update checker disabled

---


## Export behavior

Settings under **General → EXPORT BEHAVIOR** (and related toggles):

- **Skip Already Exported Files** — Disabled / Skip by file name / Skip by file name & size (filesystem checks only; cheap and early)
- **Overwrite Protection** — When skip is off, refuse to overwrite a destination that exists with a different size (wrong folder/build safety)
- **Export Smallest Files First** — Process smaller packages before larger ones in bulk exports
- **Group Export Loose Assets** — Loose images go with Texture export; text/config/fonts with Properties (when off, fonts only via Raw Data)
- **Auto Export Textures with Models** — Folder model exports also pull textures in that folder
- **Auto Load All Files on Startup** — After archives are recognized, populate the explorer (same as Load → All)
- **Convert uint64 to Float** — Bit-cast doubles stored as uint64 → readable floats in properties/JSON (Arc Raiders–oriented; leave off if unused)
- **Metadata Export** — Disabled / Auto Export (sidecar) / Append to Json
![Export Behaviour Settings](https://i.imgur.com/NJR3sZq.jpeg)
---

## Export Queue

Queue folder or global exports and run them back-to-back.

- **Queuing Mode** — Right-click folder Save* actions enqueue instead of running immediately
- Queue global exports across all loaded assets (Properties, Textures, Models, Animations, Audio, Decompiled Code, Raw, Metadata)
- Review / remove items, then **Run Queue**



### Queue filters & modes

| Option | What it does |
| --- | --- |
| **Flat Archive Export** | One flat list of all loaded archive files, smallest→largest; fewer per-folder console messages (on by default) |
| **Exclude .umap** | Skip `.umap` packages during queue runs |
| **Exclude Directories** | Skip path prefixes listed in the ignore box (saved on the active profile) |
| **Exclude Classes** | Skip packages whose **FMDex** class tags match listed names/fragments/aliases. Unindexed packages are not skipped. Requires FMDex data (use **Index Before Export** or index beforehand) |
| **Index Before Export** | FMDex-index only the folders selected for this queue run before export (not the whole archive) |

![Export Queue](https://i.imgur.com/4CtUaXZ.jpeg)

---

## FMDex

Package → UE class tag index for Search and Export Queue class exclusion.

- Stored as `*_FMDex.json.br` (Brotli) under `{install}/FMDex/{Profile}` (plain/legacy formats still load and migrate)
- Per-profile directory; share one index per game/build; new packages append as you open them
- **Auto-index unindexed on load**, optional **Index Folder / Index Selected** with **FMDex Max Threads** (`0` = auto ~4× cores, min 32 — I/O/Theia-bound)
- Search tag aliases (`model`, `tex`, `anim`, `bp`, …) expand to related UE class fragments without changing stored tags
- Game/build fields in the index; Detect from mounted project / BuildInfo when available

![FMDex Settings](https://i.imgur.com/OOR8X4M.jpeg)
![FMDex Class Search](https://i.imgur.com/zEg8FId.jpeg)

---

## Arc Raiders / Theia

C# Theia decryptor under `CUE4Parse/GameTypes/ArcRaiders/Encryption/Theia/`.

- **On-read decrypt** when UE version is Arc Raiders, or when the directory has Theia sibling `.meta` (`metadat0` magic) — mount Steam/game packs as-is; no decrypt-all cache required
- Sibling `.meta` → Theia on-read; no `.meta`/`.sig` → treat as already plaintext/decrypted
- **Directory → Export Decrypted Paks** — write plaintext `.pak`/`.ucas` (+ `.utoc`); does not copy `.meta`/`.sig`
- Soft-fail bad ContainerHeader chunks (warn and continue)
- Schedules cover Arc Raiders and related Theia titles (e.g. MTFS)

---

## Profiles

Named settings snapshots for switching games/builds without re-entering paths each time.

- Save / Load / Rename / Delete profiles (export directories, mapping, AES, UE version, pak archive, multithreading/queue options, etc.)
- **Save Current Settings as New Profile** (Directory dropdown + top menu)
- Settings → **Profiles** tab; top-level **Profiles** menu for quick switch
- Settings changes **autosave** to the active profile (with suspend scopes during bulk profile operations)
![Profiles Settings](https://i.imgur.com/omx0sER.jpeg)

---

## Diff Checker

Top-level window to compare two profiles or manual pak mounts.

- Shared AES / mapping / engine version; separate old vs new pak folders
- Inventory diff by path (added / modified / removed) with size deltas
- Export delta packages (Properties / Raw / Textures / Models / Audio)
- Optional Populate Explorer with the delta set
- Optional Write Diff Log (`## ADDED` / `## MODIFIED` / `## REMOVED`)
- Optional Export Removed Files into `Removed/`
- Live progress + Stop Diff
![Diff Checker](https://i.imgur.com/oy9yWqs.jpeg)

---

## Metadata

- Right-click **Export Metadata** (single file and folder bulk)
- **Append to Json** merges metadata into the properties JSON instead of a separate file

---

## Settings UI

- Collapsible **SeparatorExpander** sections on General (Advanced, Export Behavior, Fortnite LIVE) with expanded state persisted per profile
- Safe integer converters on numeric settings fields to avoid binding crashes on empty/partial input

---

## License

FModel is licensed under [GPL-3](LICENSE). Third-party notices: [NOTICE](NOTICE). Upstream: [4sval/FModel](https://github.com/4sval/FModel).
