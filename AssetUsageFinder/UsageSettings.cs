#if UNITY_EDITOR
using UnityEditor;

namespace AssetUsageFinder
{
    /// <summary>
    /// EditorPrefs-backed settings for Asset Usage Finder.
    /// Backed by a project wide reverse-dependency index whose targets are scripts and prefabs.
    /// </summary>
    internal static class UsageSettings
    {
        private const string PREF_AUTO_REBUILD = "UsageFinder.AutoRebuild";
        private const string PREF_SHOW_BADGES = "UsageFinder.ShowBadges";
        private const string PREF_BADGE_RIGHT_IN_LIST = "UsageFinder.BadgeRightInList";

        /// <summary>Keep the index up to date when assets are imported, moved or deleted.</summary>
        public static bool AutoRebuild
        {
            get => EditorPrefs.GetBool(PREF_AUTO_REBUILD, true);
            set => EditorPrefs.SetBool(PREF_AUTO_REBUILD, value);
        }

        /// <summary>Draw the reference count badge in the Project window, only when the count is above zero.</summary>
        public static bool ShowBadges
        {
            get => EditorPrefs.GetBool(PREF_SHOW_BADGES, true);
            set => EditorPrefs.SetBool(PREF_SHOW_BADGES, value);
        }

        /// <summary>Put the badge on the right edge in List View instead of over the icon.</summary>
        public static bool BadgeRightInList
        {
            get => EditorPrefs.GetBool(PREF_BADGE_RIGHT_IN_LIST, true);
            set => EditorPrefs.SetBool(PREF_BADGE_RIGHT_IN_LIST, value);
        }
    }
}
#endif
