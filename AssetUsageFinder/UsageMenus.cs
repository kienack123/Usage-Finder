#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using UnityEditor;
using UnityEngine;

namespace AssetUsageFinder
{
    internal static class UsageMenus
    {
        private const string ROOT = "Tools/Script Usage/";

        [MenuItem(ROOT + "Force Rebuild Now", priority = 0)]
        private static void ForceRebuild()
        {
            UsageCountIndex.ForceRebuild(showProgress: true);
        }

        // ── Auto rebuild on import ──
        [MenuItem(ROOT + "Auto Rebuild On Import", true)]
        private static bool AutoValidate()
        {
            Menu.SetChecked(ROOT + "Auto Rebuild On Import", UsageSettings.AutoRebuild);
            return true;
        }
        [MenuItem(ROOT + "Auto Rebuild On Import", priority = 20)]
        private static void AutoToggle() => UsageSettings.AutoRebuild = !UsageSettings.AutoRebuild;

        // ── Show badges ──
        [MenuItem(ROOT + "Show Badges", true)]
        private static bool ShowBadgesValidate()
        {
            Menu.SetChecked(ROOT + "Show Badges", UsageSettings.ShowBadges);
            return true;
        }
        [MenuItem(ROOT + "Show Badges", priority = 21)]
        private static void ShowBadgesToggle()
        {
            UsageSettings.ShowBadges = !UsageSettings.ShowBadges;
            EditorApplication.RepaintProjectWindow();
        }

        // ── Badge side ──
        [MenuItem(ROOT + "Badge: Right side in List View", true)]
        private static bool BadgeSideValidate()
        {
            Menu.SetChecked(ROOT + "Badge: Right side in List View", UsageSettings.BadgeRightInList);
            return true;
        }
        [MenuItem(ROOT + "Badge: Right side in List View", priority = 24)]
        private static void BadgeSideToggle()
        {
            UsageSettings.BadgeRightInList = !UsageSettings.BadgeRightInList;
            EditorApplication.RepaintProjectWindow();
        }

        // ── Context menu: Show Usages ──
        [MenuItem("Assets/Show Usages", false, 2050)]
        private static void ShowUsages()
        {
            var guids = SelectionUtil.GetSelectedTargetGuids();
            if (guids.Count == 0)
            {
                EditorUtility.DisplayDialog("Usage Finder",
                    "Select at least one asset in the Project window.", "OK");
                return;
            }
            UsageWindow.ShowForGuids(guids);
        }

        [MenuItem("Assets/Show Usages", true, 2050)]
        private static bool ShowUsagesValidate() => SelectionUtil.GetSelectedTargetGuids().Count > 0;
    }

    internal static class SelectionUtil
    {
        /// <summary>Guids of every asset selected in the Project window, folders excluded. Show Usages handles
        /// every type: scripts and prefabs read the index, anything else is scanned on demand.</summary>
        public static List<string> GetSelectedTargetGuids()
        {
            var list = new List<string>();
            var seen = new HashSet<string>();
            foreach (var o in Selection.objects)
            {
                var p = AssetDatabase.GetAssetPath(o);
                if (string.IsNullOrEmpty(p)) continue;
                if (!p.StartsWith("Assets/", StringComparison.OrdinalIgnoreCase)) continue;
                if (AssetDatabase.IsValidFolder(p)) continue;

                var g = AssetDatabase.AssetPathToGUID(p);
                if (!string.IsNullOrEmpty(g) && seen.Add(g)) list.Add(g);
            }
            return list;
        }

        // ── Back-compat helpers ──
        public static HashSet<string> GetSelectedScriptGuids()
        {
            var set = new HashSet<string>();
            foreach (var o in Selection.objects)
            {
                if (o is not MonoScript) continue;
                var p = AssetDatabase.GetAssetPath(o);
                if (string.IsNullOrEmpty(p) || !p.EndsWith(".cs", StringComparison.OrdinalIgnoreCase)) continue;
                var g = AssetDatabase.AssetPathToGUID(p);
                if (!string.IsNullOrEmpty(g)) set.Add(g);
            }
            return set;
        }

        public static List<GameObject> GetSelectedPrefabAssets()
        {
            var list = new List<GameObject>();
            foreach (var o in Selection.objects)
            {
                if (o is not GameObject go) continue;
                var p = AssetDatabase.GetAssetPath(go);
                if (string.IsNullOrEmpty(p) || !p.EndsWith(".prefab", StringComparison.OrdinalIgnoreCase)) continue;
                if (PrefabUtility.GetPrefabAssetType(go) == PrefabAssetType.NotAPrefab) continue;
                var root = AssetDatabase.LoadAssetAtPath<GameObject>(p);
                if (root != null && !list.Contains(root)) list.Add(root);
            }
            return list;
        }
    }
}
#endif
