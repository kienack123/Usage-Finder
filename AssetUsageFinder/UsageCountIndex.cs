#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using UnityEditor;
using UnityEngine;

namespace AssetUsageFinder
{
    /// <summary>
    /// Project wide REVERSE-DEPENDENCY index, limited to targets of type script (.cs) and prefab (.prefab).
    ///
    /// Idea: for every "container" asset (prefab / scene / .asset / controller...), take its DIRECT dependencies
    /// (AssetDatabase.GetDependencies(path, recursive:false)); every dependency that is a .cs/.prefab is recorded in reverse:
    ///   refsTo[targetGuid] += containerGuid.
    /// => The usage count of a script/prefab is the NUMBER OF ASSETS referencing it directly (closed scenes and .asset files included,
    /// not only prefabs and open scenes like the old version). Fast: GetDependencies only, never LoadPrefabContents.
    ///
    /// Feeds the Project window badge (only when count &gt; 0) and the "Show Usages" window.
    /// Builds asynchronously on a per frame budget, patches incrementally on import, or builds synchronously when the window needs it now.
    /// </summary>
    [InitializeOnLoad]
    internal static class UsageCountIndex
    {
        // targetGuid (.cs/.prefab) -> set of guids of the assets referencing it directly
        private static readonly Dictionary<string, HashSet<string>> _refsTo = new(1 << 14);
        // containerGuid -> set of targetGuids (.cs/.prefab) it references, used for incremental patching
        private static readonly Dictionary<string, HashSet<string>> _depsFrom = new(1 << 14);

        // Build and patch queue
        private static readonly Queue<string> _queue = new(1 << 13);
        private static readonly HashSet<string> _queued = new(1 << 13);

        private static bool _building;
        private static bool _indexReady;
        private static int _total, _done;
        private static bool _showProgress;

        private const double SCAN_BUDGET_SECONDS = 0.006; // ~6ms/frame

        // Extensions of assets that CAN reference a script or prefab, scanned for outgoing links. Leaves (texture/audio/mat/anim/model...)
        // never point at a .cs/.prefab, so skipping them makes the scan much faster.
        private static readonly HashSet<string> _containerExts = new(StringComparer.OrdinalIgnoreCase)
        {
            ".prefab", ".unity", ".asset", ".controller", ".overridecontroller", ".playable", ".preset", ".mixer"
        };

        // Leaf: an asset that references nothing, skipped during the ON-DEMAND referencer scan to speed it up.
        private static readonly HashSet<string> _leafExts = new(StringComparer.OrdinalIgnoreCase)
        {
            ".png", ".jpg", ".jpeg", ".tga", ".psd", ".gif", ".bmp", ".exr", ".hdr", ".tif", ".tiff", ".webp",
            ".wav", ".mp3", ".ogg", ".aiff", ".aif", ".mod", ".xm", ".it",
            ".ttf", ".otf", ".fon", ".fnt",
            ".cs", ".dll", ".txt", ".json", ".xml", ".csv", ".bytes", ".md", ".yaml", ".yml",
            ".shader", ".cginc", ".hlsl", ".glslinc", ".compute", ".raytrace",
            ".asmdef", ".asmref", ".rsp"
        };

        // Project-window cache: guid -> kind (bit1=folder, bit2=script, bit4=prefab)
        private const byte K_FOLDER = 1, K_SCRIPT = 2, K_PREFAB = 4;
        private static readonly Dictionary<string, byte> _kindCache = new(1 << 14);
        private static GUIStyle _badgeStyle;

        public static bool HasData => _indexReady;
        public static bool IsBuilding => _building;

        static UsageCountIndex()
        {
            EditorApplication.projectWindowItemOnGUI += OnProjectWindowItemGUI;
            EditorApplication.delayCall += KickInitialBuild;
            EditorApplication.projectChanged += () => _kindCache.Clear();
            AssetChangeHook.Install();
        }

        // ========================= PUBLIC QUERY =========================
        public static int GetReferenceCount(string guid) => _refsTo.TryGetValue(guid, out var s) ? s.Count : 0;

        public static IEnumerable<string> GetReferencerGuids(string guid)
            => _refsTo.TryGetValue(guid, out var s) ? (IEnumerable<string>)s : Array.Empty<string>();

        // Back-compat for older call sites.
        public static int GetScriptCountByGuid(string guid) => GetReferenceCount(guid);
        public static int GetPrefabCountByGuid(string guid) => GetReferenceCount(guid);

        public static bool IsScriptPath(string path) =>
            !string.IsNullOrEmpty(path) && path.EndsWith(".cs", StringComparison.OrdinalIgnoreCase);

        public static bool IsPrefabPath(string path) =>
            !string.IsNullOrEmpty(path) && path.EndsWith(".prefab", StringComparison.OrdinalIgnoreCase);

        /// <summary>
        /// Finds the referencers of ANY asset (material/texture/audio/...) ON DEMAND, by reading
        /// GetDependencies of every non-leaf asset in the project. Returns a map targetPath -> paths that
        /// reference it DIRECTLY. Slower than the prebuilt index, so it only runs when Show Usages is pressed.
        /// </summary>
        public static Dictionary<string, List<string>> FindReferencersOnDemand(IEnumerable<string> targetPaths, bool showProgress)
        {
            var targets = new HashSet<string>(targetPaths ?? Array.Empty<string>(), StringComparer.OrdinalIgnoreCase);
            var result = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
            foreach (var t in targets) result[t] = new List<string>();
            if (targets.Count == 0) return result;

            var all = AssetDatabase.GetAllAssetPaths();
            int n = all.Length;
            try
            {
                for (int i = 0; i < n; i++)
                {
                    var p = all[i];
                    if (string.IsNullOrEmpty(p)) continue;
                    if (!p.StartsWith("Assets/", StringComparison.OrdinalIgnoreCase)) continue;
                    if (targets.Contains(p)) continue;   // itself
                    if (IsLeafPath(p)) continue;         // leaf: points at nothing
                    if (AssetDatabase.IsValidFolder(p)) continue;

                    if (showProgress && (i & 31) == 0 &&
                        EditorUtility.DisplayCancelableProgressBar("Usage Finder",
                            $"Scanning usages... {i}/{n}", n <= 0 ? 1f : i / (float)n))
                        break;

                    var deps = AssetDatabase.GetDependencies(p, recursive: false);
                    for (int d = 0; d < deps.Length; d++)
                    {
                        var dep = deps[d];
                        if (!string.IsNullOrEmpty(dep) && targets.Contains(dep) &&
                            !string.Equals(dep, p, StringComparison.OrdinalIgnoreCase))
                            result[dep].Add(p);
                    }
                }
            }
            finally { EditorUtility.ClearProgressBar(); }

            return result;
        }

        private static bool IsLeafPath(string path)
        {
            int dot = path.LastIndexOf('.');
            if (dot < 0) return false;
            return _leafExts.Contains(path.Substring(dot));
        }

        // ========================= REBUILD / INIT =========================
        public static void ForceRebuild(bool showProgress)
        {
            if (EditorApplication.isPlayingOrWillChangePlaymode) return;
            StopScanner();
            _refsTo.Clear();
            _depsFrom.Clear();
            _kindCache.Clear();
            _indexReady = false;
            _done = 0;

            EnqueueAllContainers();
            StartScanner(showProgress);
        }

        /// <summary>Make sure the index is ready: when there is no data and no build is running, build SYNCHRONOUSLY with a progress bar.</summary>
        public static void EnsureBuiltBlocking()
        {
            if (_indexReady) return;
            if (EditorApplication.isPlayingOrWillChangePlaymode) return;

            StopScanner();
            _refsTo.Clear();
            _depsFrom.Clear();
            EnqueueAllContainers();
            _total = _queue.Count;
            _done = 0;
            try
            {
                while (_queue.Count > 0)
                {
                    var path = _queue.Dequeue();
                    _queued.Remove(path);
                    ScanContainer(path);
                    _done++;
                    if ((_done & 255) == 0)
                        EditorUtility.DisplayProgressBar("Usage Finder",
                            $"Indexing... {_done}/{_total}", _total <= 0 ? 1f : _done / (float)_total);
                }
            }
            finally { EditorUtility.ClearProgressBar(); }

            _indexReady = true;
            _building = false;
            _kindCache.Clear();
            EditorApplication.RepaintProjectWindow();
        }

        private static void KickInitialBuild()
        {
            if (_indexReady || _building) return;
            if (EditorApplication.isPlayingOrWillChangePlaymode) return;
            EnqueueAllContainers();
            StartScanner(showProgress: false);
        }

        private static void EnqueueAllContainers()
        {
            _queue.Clear();
            _queued.Clear();

            var all = AssetDatabase.GetAllAssetPaths();
            for (int i = 0; i < all.Length; i++)
            {
                var p = all[i];
                if (string.IsNullOrEmpty(p)) continue;
                if (!p.StartsWith("Assets/", StringComparison.OrdinalIgnoreCase)) continue;
                if (!IsContainerPath(p)) continue;
                if (_queued.Add(p)) _queue.Enqueue(p);
            }
            _total = _queue.Count;
        }

        private static bool IsContainerPath(string path)
        {
            int dot = path.LastIndexOf('.');
            if (dot < 0) return false;
            return _containerExts.Contains(path.Substring(dot));
        }

        // ========================= ASYNC SCANNER =========================
        private static void StartScanner(bool showProgress)
        {
            if (_building) { _showProgress |= showProgress; return; }
            if (EditorApplication.isPlayingOrWillChangePlaymode) return;
            if (_queue.Count == 0) { _indexReady = true; EditorApplication.RepaintProjectWindow(); return; }

            _building = true;
            _indexReady = false;
            _showProgress = showProgress;
            EditorApplication.update += ScanUpdate;
        }

        private static void StopScanner()
        {
            if (!_building) return;
            EditorApplication.update -= ScanUpdate;
            EditorUtility.ClearProgressBar();
            _building = false;
        }

        private static void ScanUpdate()
        {
            double start = EditorApplication.timeSinceStartup;
            while (_queue.Count > 0 && (EditorApplication.timeSinceStartup - start) < SCAN_BUDGET_SECONDS)
            {
                var path = _queue.Dequeue();
                _queued.Remove(path);
                ScanContainer(path);
                _done++;
            }

            if (_showProgress && _total > 0)
            {
                if (EditorUtility.DisplayCancelableProgressBar("Usage Finder",
                        $"Indexing... {_done}/{_total}", _done / (float)_total))
                {
                    _queue.Clear();
                    _queued.Clear();
                }
            }

            if (_done % 200 == 0 || _queue.Count == 0)
            {
                _indexReady = true;
                _kindCache.Clear();
                EditorApplication.RepaintProjectWindow();
            }

            if (_queue.Count == 0)
            {
                EditorApplication.update -= ScanUpdate;
                EditorUtility.ClearProgressBar();
                _indexReady = true;
                _building = false;
                _showProgress = false;
                _kindCache.Clear();
                EditorApplication.RepaintProjectWindow();
            }
        }

        // Scan one container: refresh the reverse map for its .cs/.prefab dependencies.
        private static void ScanContainer(string containerPath)
        {
            string cGuid = AssetDatabase.AssetPathToGUID(containerPath);
            if (string.IsNullOrEmpty(cGuid)) return;

            // Drop the previous contribution
            if (_depsFrom.TryGetValue(cGuid, out var oldSet))
            {
                foreach (var tg in oldSet)
                    if (_refsTo.TryGetValue(tg, out var refs))
                    {
                        refs.Remove(cGuid);
                        if (refs.Count == 0) _refsTo.Remove(tg);
                    }
                oldSet.Clear();
            }
            else
            {
                oldSet = new HashSet<string>();
                _depsFrom[cGuid] = oldSet;
            }

            var deps = AssetDatabase.GetDependencies(containerPath, recursive: false);
            for (int i = 0; i < deps.Length; i++)
            {
                var dep = deps[i];
                if (string.IsNullOrEmpty(dep)) continue;
                if (string.Equals(dep, containerPath, StringComparison.OrdinalIgnoreCase)) continue; // itself
                if (!IsScriptPath(dep) && !IsPrefabPath(dep)) continue; // scripts and prefabs only

                var tGuid = AssetDatabase.AssetPathToGUID(dep);
                if (string.IsNullOrEmpty(tGuid)) continue;
                oldSet.Add(tGuid);
            }

            foreach (var tg in oldSet)
            {
                if (!_refsTo.TryGetValue(tg, out var refs)) { refs = new HashSet<string>(); _refsTo[tg] = refs; }
                refs.Add(cGuid);
            }
        }

        private static void RemoveContainer(string cGuid)
        {
            if (string.IsNullOrEmpty(cGuid)) return;
            if (_depsFrom.TryGetValue(cGuid, out var oldSet))
            {
                foreach (var tg in oldSet)
                    if (_refsTo.TryGetValue(tg, out var refs))
                    {
                        refs.Remove(cGuid);
                        if (refs.Count == 0) _refsTo.Remove(tg);
                    }
                _depsFrom.Remove(cGuid);
            }
            // If the deleted asset is itself a target (prefab), drop its reverse row as well.
            _refsTo.Remove(cGuid);
        }

        // ========================= PROJECT WINDOW BADGES =========================
        // Draw the badge ONLY when count > 0. An asset nobody uses shows nothing.
        private static void OnProjectWindowItemGUI(string guid, Rect rect)
        {
            if (!UsageSettings.ShowBadges) return;
            if (string.IsNullOrEmpty(guid)) return;

            byte kind = GetKind(guid);
            if ((kind & K_FOLDER) != 0) return;
            if ((kind & (K_SCRIPT | K_PREFAB)) == 0) return; // badge scripts and prefabs only

            if (!_indexReady) return;

            int count = GetReferenceCount(guid);
            if (count <= 0) return; // zero usage -> draw nothing

            EnsureBadgeStyle();
            DrawBadge(rect, count.ToString());
        }

        private static byte GetKind(string guid)
        {
            if (_kindCache.TryGetValue(guid, out var k)) return k;

            string path = AssetDatabase.GUIDToAssetPath(guid);
            byte v = 0;
            if (string.IsNullOrEmpty(path)) { _kindCache[guid] = 0; return 0; }

            if (AssetDatabase.IsValidFolder(path)) v |= K_FOLDER;
            else
            {
                if (IsScriptPath(path)) v |= K_SCRIPT;
                if (IsPrefabPath(path)) v |= K_PREFAB;
            }
            _kindCache[guid] = v;
            return v;
        }

        private static void EnsureBadgeStyle()
        {
            _badgeStyle ??= new GUIStyle(EditorStyles.miniLabel)
            {
                alignment = TextAnchor.MiddleCenter,
                fontSize = 10,
                normal = { textColor = Color.white }
            };
        }

        private static void DrawBadge(Rect r, string text)
        {
            bool isList = r.height <= 20f;
            const float w = 28f, h = 14f;

            Rect badgeRect = (isList && UsageSettings.BadgeRightInList)
                ? new Rect(r.xMax - w - 6f, r.y + 2f, w, h)
                : new Rect(r.x + 4f, r.y + 4f, Mathf.Min(w, r.width - 6f), h);

            if (badgeRect.width <= 6f) return;

            GUI.Label(badgeRect, text, _badgeStyle);
        }

        // ========================= ASSET CHANGE HOOK =========================
        private sealed class AssetChangeHook : AssetPostprocessor
        {
            private static bool _installed;
            public static void Install() { _installed = true; }

            private static void OnPostprocessAllAssets(string[] imported, string[] deleted, string[] moved, string[] movedFrom)
            {
                _ = _installed;
                _kindCache.Clear();

                if (EditorApplication.isPlayingOrWillChangePlaymode) return;
                if (!UsageSettings.AutoRebuild) { EditorApplication.RepaintProjectWindow(); return; }

                bool changed = false;

                void Reindex(string p)
                {
                    if (string.IsNullOrEmpty(p)) return;
                    p = p.Replace('\\', '/');
                    if (!p.StartsWith("Assets/", StringComparison.OrdinalIgnoreCase)) return;
                    if (!IsContainerPath(p)) return;
                    if (_indexReady) { ScanContainer(p); changed = true; }
                    else if (_queued.Add(p)) { _queue.Enqueue(p); }
                }

                for (int i = 0; i < imported.Length; i++) Reindex(imported[i]);
                for (int i = 0; i < moved.Length; i++) Reindex(moved[i]);

                for (int i = 0; i < deleted.Length; i++)
                {
                    var g = AssetDatabase.AssetPathToGUID(deleted[i]);
                    if (!string.IsNullOrEmpty(g)) { RemoveContainer(g); changed = true; }
                }
                for (int i = 0; i < movedFrom.Length; i++)
                {
                    var g = AssetDatabase.AssetPathToGUID(movedFrom[i]);
                    if (!string.IsNullOrEmpty(g)) RemoveContainer(g);
                }

                if (!_indexReady && _queue.Count > 0 && !_building)
                    StartScanner(showProgress: false);

                if (changed) EditorApplication.RepaintProjectWindow();
            }
        }
    }
}
#endif
