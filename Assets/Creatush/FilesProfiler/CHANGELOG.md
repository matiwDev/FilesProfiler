# Changelog

All notable changes to this package are documented here.
Format loosely follows [Keep a Changelog](https://keepachangelog.com/).

## [1.0.0] — Initial release

### Added
- Category-based scanning across Textures, Audio, Models, Materials, Prefabs,
  Animation, Shaders, Fonts, Data Assets, Scenes, Scripts, and Other.
- **Unused detection** via `AssetDatabase.GetDependencies()` against every
  reference-bearing file in scope (materials, prefabs, scenes, controllers,
  ScriptableObjects, shaders).
- **Duplicate detection** by exact content hash (MD5) or filename match,
  with per-category grouping and wasted-space reporting.
- **GUID Swap** — redirect every reference from one asset's GUID to another's,
  across the whole project, with dry-run preview and `.bak` backups.
- Drag-and-drop picker in the Swap tab — accepts any asset type or whole
  folders, with an inline Role column (From / To) per row.
- **Multi-duplicate merge** — select any subset of a duplicate group and
  redirect them all to a chosen "keep" file in a single confirmed action,
  with an optional auto-delete of the now-redundant files afterward.
- "Remove Selected" action on the Unused panel with a live selected-size
  summary.
- Folded category dropdown with per-category unused-count badges, and a
  single Unused / Duplicates / GUID Swap toolbar shared across all categories.
- Scan scope control: scan only the active category, or the whole project
  in one pass — results merge in per category without discarding the rest.
