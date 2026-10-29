#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.Text;
using UnityEditor;
using UnityEngine;

namespace AssetUsageFinder
{
    /// <summary>
    /// Lists WHO IS USING the selected asset.
    /// - Scripts and prefabs: served from the prebuilt reverse-dependency index (fast).
    /// - Any OTHER asset type (material/texture/audio/...): scanned ON DEMAND across the project when the window opens, with a progress bar.
    /// A referencer can be a prefab / scene (including closed ones) / .asset / animator / material...
    /// </summary>
    internal sealed class UsageWindow : EditorWindow
    {
        private enum RefKind { Prefab, Scene, ScriptableObject, Animator, Material, Other }

        private sealed class Ref
        {
            public string path;
            public RefKind kind;
        }

        private sealed class Target
        {
            public string guid;
            public string path;
            public string name;
            public bool indexed;      // script/prefab -> read the index; anything else -> on-demand scan
            public string kindLabel;  // "Script"/"Prefab"/"Material"/"Texture2D"…
            public readonly List<Ref> refs = new();
        }

        private readonly List<Target> _targets = new();
        private Vector2 _scroll;
        private string _filter = "";
        private bool _showPrefabs = true, _showScenes = true, _showAssets = true, _showOther = true;

        // ---- Entry points ----
        public static void ShowForGuids(List<string> targetGuids)
        {
            var w = GetWindow<UsageWindow>("Usage Finder");
            w._initGuids = targetGuids ?? new List<string>();
            w.EnsureIndexAndBuild();
            w.Show();
            w.Focus();
        }

        // Back-compat with the old signature.
        public static void ShowUnified(HashSet<string> scriptGuids, List<GameObject> prefabAssets)
        {
            var list = new List<string>();
            if (scriptGuids != null) list.AddRange(scriptGuids);
            if (prefabAssets != null)
                foreach (var go in prefabAssets)
                {
                    if (go == null) continue;
                    var p = AssetDatabase.GetAssetPath(go);
                    var g = AssetDatabase.AssetPathToGUID(p);
                    if (!string.IsNullOrEmpty(g)) list.Add(g);
                }
            ShowForGuids(list);
        }

        private List<string> _initGuids = new();

        private void EnsureIndexAndBuild()
        {
            // Only build the script/prefab index when the selection contains one; other assets use the on-demand scan.
            bool needIndex = false;
            foreach (var g in _initGuids)
            {
                var p = AssetDatabase.GUIDToAssetPath(g);
                if (UsageCountIndex.IsScriptPath(p) || UsageCountIndex.IsPrefabPath(p)) { needIndex = true; break; }
            }
            if (needIndex && !UsageCountIndex.HasData)
                UsageCountIndex.EnsureBuiltBlocking();

            Build();
        }

        private void Build()
        {
            _targets.Clear();
            var seen = new HashSet<string>();
            var needScan = new List<Target>();

            foreach (var guid in _initGuids)
            {
                if (string.IsNullOrEmpty(guid) || !seen.Add(guid)) continue;

                string path = AssetDatabase.GUIDToAssetPath(guid);
                if (string.IsNullOrEmpty(path)) continue;
                if (AssetDatabase.IsValidFolder(path)) continue;

                bool isScript = UsageCountIndex.IsScriptPath(path);
                bool isPrefab = UsageCountIndex.IsPrefabPath(path);

                var t = new Target
                {
                    guid = guid,
                    path = path,
                    name = System.IO.Path.GetFileNameWithoutExtension(path),
                    indexed = isScript || isPrefab,
                    kindLabel = KindOfTarget(path, isScript, isPrefab)
                };

                if (t.indexed)
                {
                    // Fast path: read from the prebuilt reverse index.
                    foreach (var rg in UsageCountIndex.GetReferencerGuids(guid))
                    {
                        var rp = AssetDatabase.GUIDToAssetPath(rg);
                        if (string.IsNullOrEmpty(rp)) continue;
                        t.refs.Add(new Ref { path = rp, kind = ClassifyRef(rp) });
                    }
                    SortRefs(t);
                }
                else
                {
                    needScan.Add(t); // other asset types -> scanned on demand in one pass below
                }

                _targets.Add(t);
            }

            // One on-demand project scan covering every target that is not indexed (material/texture/audio/...).
            if (needScan.Count > 0)
            {
                var byPath = new Dictionary<string, Target>(StringComparer.OrdinalIgnoreCase);
                foreach (var t in needScan) byPath[t.path] = t;

                var map = UsageCountIndex.FindReferencersOnDemand(byPath.Keys, showProgress: true);
                foreach (var kv in map)
                {
                    if (!byPath.TryGetValue(kv.Key, out var t)) continue;
                    foreach (var rp in kv.Value)
                        t.refs.Add(new Ref { path = rp, kind = ClassifyRef(rp) });
                    SortRefs(t);
                }
            }

            _targets.Sort((a, b) =>
            {
                int k = string.Compare(a.kindLabel, b.kindLabel, StringComparison.OrdinalIgnoreCase);
                if (k != 0) return k;
                return string.Compare(a.name, b.name, StringComparison.OrdinalIgnoreCase);
            });
        }

        private static void SortRefs(Target t)
        {
            t.refs.Sort((a, b) =>
            {
                int k = a.kind.CompareTo(b.kind);
                return k != 0 ? k : string.Compare(a.path, b.path, StringComparison.OrdinalIgnoreCase);
            });
        }

        private static string KindOfTarget(string path, bool isScript, bool isPrefab)
        {
            if (isScript) return "Script";
            if (isPrefab) return "Prefab";
            var tp = AssetDatabase.GetMainAssetTypeAtPath(path);
            return tp != null ? tp.Name : "Asset";
        }

        private static RefKind ClassifyRef(string path)
        {
            if (path.EndsWith(".prefab", StringComparison.OrdinalIgnoreCase)) return RefKind.Prefab;
            if (path.EndsWith(".unity", StringComparison.OrdinalIgnoreCase)) return RefKind.Scene;
            if (path.EndsWith(".asset", StringComparison.OrdinalIgnoreCase)) return RefKind.ScriptableObject;
            if (path.EndsWith(".controller", StringComparison.OrdinalIgnoreCase) ||
                path.EndsWith(".overrideController", StringComparison.OrdinalIgnoreCase)) return RefKind.Animator;
            if (path.EndsWith(".mat", StringComparison.OrdinalIgnoreCase)) return RefKind.Material;
            return RefKind.Other;
        }

        // ========================= UI =========================
        private void OnGUI()
        {
            DrawToolbar();

            if (_targets.Count == 0)
            {
                EditorGUILayout.HelpBox(
                    "Nothing here yet. Select an asset in the Project window and press \"From Selection\", or right click it and choose Show Usages.",
                    MessageType.Info);
                return;
            }

            _scroll = EditorGUILayout.BeginScrollView(_scroll);

            foreach (var t in _targets)
            {
                int shown = CountShown(t);
                EditorGUILayout.Space(6);
                using (new EditorGUILayout.HorizontalScope(EditorStyles.helpBox))
                {
                    GUILayout.Label(t.kindLabel, EditorStyles.miniBoldLabel, GUILayout.Width(80));
                    GUILayout.Label($"{t.name}  ({t.refs.Count} ref)", EditorStyles.boldLabel);
                    GUILayout.FlexibleSpace();
                    if (GUILayout.Button("Ping", EditorStyles.miniButton, GUILayout.Width(46)))
                        PingPath(t.path);
                    if (GUILayout.Button("Copy path", EditorStyles.miniButton, GUILayout.Width(72)))
                        EditorGUIUtility.systemCopyBuffer = t.path;
                }

                if (t.refs.Count == 0)
                {
                    EditorGUILayout.LabelField("   (no references)", EditorStyles.miniLabel);
                    continue;
                }

                foreach (var r in t.refs)
                {
                    if (!PassTypeFilter(r.kind)) continue;
                    if (!PassTextFilter(r.path)) continue;

                    using (new EditorGUILayout.HorizontalScope())
                    {
                        GUILayout.Space(12);
                        GUILayout.Label(KindLabel(r.kind), EditorStyles.miniLabel, GUILayout.Width(60));

                        var icon = AssetDatabase.GetCachedIcon(r.path);
                        if (icon != null) GUILayout.Label(icon, GUILayout.Width(16), GUILayout.Height(16));

                        if (GUILayout.Button(r.path, EditorStyles.linkLabel ?? EditorStyles.label))
                            PingPath(r.path);

                        GUILayout.FlexibleSpace();
                        if (GUILayout.Button("Open", EditorStyles.miniButton, GUILayout.Width(48)))
                            AssetDatabase.OpenAsset(AssetDatabase.LoadMainAssetAtPath(r.path));
                        if (GUILayout.Button("Copy", EditorStyles.miniButton, GUILayout.Width(48)))
                            EditorGUIUtility.systemCopyBuffer = r.path;
                    }
                }

                _ = shown;
            }

            EditorGUILayout.EndScrollView();
        }

        private void DrawToolbar()
        {
            using (new EditorGUILayout.HorizontalScope(EditorStyles.toolbar))
            {
                GUILayout.Label($"Targets: {_targets.Count}", GUILayout.Width(90));

                _filter = GUILayout.TextField(_filter, GUI.skin.FindStyle("ToolbarSearchTextField") ?? EditorStyles.toolbarTextField);
                if (GUILayout.Button("", GUI.skin.FindStyle("ToolbarSearchCancelButton") ?? EditorStyles.toolbarButton, GUILayout.Width(18)))
                    _filter = "";

                _showPrefabs = GUILayout.Toggle(_showPrefabs, "Prefab", EditorStyles.toolbarButton, GUILayout.Width(58));
                _showScenes = GUILayout.Toggle(_showScenes, "Scene", EditorStyles.toolbarButton, GUILayout.Width(54));
                _showAssets = GUILayout.Toggle(_showAssets, ".asset", EditorStyles.toolbarButton, GUILayout.Width(54));
                _showOther = GUILayout.Toggle(_showOther, "Other", EditorStyles.toolbarButton, GUILayout.Width(48));

                GUILayout.FlexibleSpace();

                if (GUILayout.Button("From Selection", EditorStyles.toolbarButton, GUILayout.Width(96)))
                    LoadFromSelection();
                if (GUILayout.Button("Refresh", EditorStyles.toolbarButton, GUILayout.Width(60)))
                    Build();
                if (GUILayout.Button("Export CSV", EditorStyles.toolbarButton, GUILayout.Width(80)))
                    ExportCsv();
            }
        }

        private void LoadFromSelection()
        {
            var list = SelectionUtil.GetSelectedTargetGuids();
            if (list.Count == 0)
            {
                EditorUtility.DisplayDialog("Usage Finder", "Select at least one asset in the Project window.", "OK");
                return;
            }
            _initGuids = list;
            EnsureIndexAndBuild();
        }

        private int CountShown(Target t)
        {
            int n = 0;
            foreach (var r in t.refs)
                if (PassTypeFilter(r.kind) && PassTextFilter(r.path)) n++;
            return n;
        }

        private bool PassTypeFilter(RefKind k) => k switch
        {
            RefKind.Prefab => _showPrefabs,
            RefKind.Scene => _showScenes,
            RefKind.ScriptableObject => _showAssets,
            RefKind.Animator => _showOther,
            _ => _showOther,
        };

        private bool PassTextFilter(string path)
        {
            if (string.IsNullOrEmpty(_filter)) return true;
            return path.IndexOf(_filter.Trim(), StringComparison.OrdinalIgnoreCase) >= 0;
        }

        private static string KindLabel(RefKind k) => k switch
        {
            RefKind.Prefab => "Prefab",
            RefKind.Scene => "Scene",
            RefKind.ScriptableObject => ".asset",
            RefKind.Animator => "Animator",
            RefKind.Material => "Material",
            _ => "Other",
        };

        private static void PingPath(string path)
        {
            var obj = AssetDatabase.LoadMainAssetAtPath(path);
            if (obj != null) { Selection.activeObject = obj; EditorGUIUtility.PingObject(obj); }
        }

        private void ExportCsv()
        {
            var sb = new StringBuilder();
            sb.AppendLine("TargetKind,Target,TargetPath,RefKind,RefPath");
            foreach (var t in _targets)
            {
                string tk = t.kindLabel;
                if (t.refs.Count == 0)
                    sb.AppendLine($"{tk},{Csv(t.name)},{Csv(t.path)},,");
                else
                    foreach (var r in t.refs)
                        sb.AppendLine($"{tk},{Csv(t.name)},{Csv(t.path)},{r.kind},{Csv(r.path)}");
            }

            string file = EditorUtility.SaveFilePanel("Export Usage CSV", Application.dataPath, "usage_report", "csv");
            if (string.IsNullOrEmpty(file)) return;
            System.IO.File.WriteAllText(file, sb.ToString());
            EditorUtility.RevealInFinder(file);
        }

        private static string Csv(string s)
        {
            if (string.IsNullOrEmpty(s)) return "";
            if (s.IndexOf(',') >= 0 || s.IndexOf('"') >= 0)
                return "\"" + s.Replace("\"", "\"\"") + "\"";
            return s;
        }
    }
}
#endif
