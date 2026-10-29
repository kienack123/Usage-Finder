# Asset Usage Finder

Answers "who is actually using this asset?" — and shows a usage count badge directly in the Project
window, so dead scripts and orphan prefabs are visible without searching for them.

Namespace: `AssetUsageFinder` — Editor only.

## Installation

Add this line to `Packages/manifest.json` under `dependencies`:

```json
"com.kienack123.asset-usage-finder": "https://github.com/kienack123/Usage-Finder.git#1.0.0"
```

or **Window ▸ Package Manager ▸ + ▸ Install package from git URL…** and paste
`https://github.com/kienack123/Usage-Finder.git#1.0.0`.

Drop the `#1.0.0` suffix to always track the latest commit on `main`.

## Entry points

| Where | What it does |
|---|---|
| Right click an asset in Project ▸ **Show Usages** | opens the window listing every asset that references the selection (multi-selection is supported) |
| Project window badge | number of assets referencing that script or prefab, drawn only when the count is greater than zero |
| **Tools ▸ Script Usage ▸ Force Rebuild Now** | rebuilds the whole index with a progress bar |
| **Tools ▸ Script Usage ▸ Auto Rebuild On Import** | keep the index in sync when assets are imported, moved or deleted (on by default) |
| **Tools ▸ Script Usage ▸ Show Badges** | turn the Project window badges on or off (on by default) |
| **Tools ▸ Script Usage ▸ Badge: Right side in List View** | badge on the right edge in list view instead of over the icon (on by default) |

All four menu options are remembered in `EditorPrefs`, so they are per user, not per project.

## The window

Each selected asset becomes a section listing its referencers:

- **Ping** / **Copy path** on the target, **Open** / **Copy** on every referencer, and the path itself
  is a link that selects the file.
- Filter buttons narrow the list to **Prefab**, **Scene**, **.asset** or everything else, and the
  search box matches on path.
- **From Selection** re-runs on whatever is selected now, **Refresh** re-reads the index, and
  **Export CSV** writes the whole result out.

## How the count is produced

Two different paths, depending on what the target is:

**Scripts (`.cs`) and prefabs** are served from a reverse-dependency index kept in memory. Every
container asset — `.prefab`, `.unity`, `.asset`, `.controller`, `.overridecontroller`, `.playable`,
`.preset`, `.mixer` — is asked for its *direct* dependencies (`AssetDatabase.GetDependencies` with
`recursive: false`); each dependency that is a script or a prefab records the container as a
referencer. So the count is the number of assets that reference the target **directly**, and it
includes closed scenes and `.asset` files, not only what happens to be open.

The index builds asynchronously with a budget of roughly 6 ms per frame, patches itself incrementally
on import, and is built synchronously (with a progress bar) if the window needs it before it is ready.

**Every other asset type** (material, texture, audio, font…) has no index: opening the window runs an
on-demand scan of the project with a progress bar. Leaf files that can never reference anything —
textures, audio, fonts, shaders, text, `.cs`, `.dll` — are skipped, which is what keeps that scan
usable.

## Notes and limits

- Only **direct** references are counted. If a scene uses prefab A and prefab A uses script S, the
  scene does not appear in the usage list of S — prefab A does.
- The index does not build or rebuild while the Editor is in Play Mode.
- Anything Unity does not express as an asset dependency is invisible here: scripts loaded by name,
  `Resources.Load` by string, addressables by key, code-only references.
- Counts and badges cover scripts and prefabs only; other asset types are answered by the on-demand
  scan when the window opens.
