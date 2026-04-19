#if UNITY_EDITOR
using UnityEditor;

namespace Logging.Editor
{
    public static class LoggingMenus
    {
        [MenuItem("Tools/Logging/Open Settings")]
        public static void OpenSettings()
        {
            SettingsService.OpenProjectSettings("Project/Logging");
        }

        [MenuItem("Tools/Logging/Find Debug.Log Usages")]
        public static void FindDebugLogs()
        {
            DebugLogFinderWindow.ShowWindow();
        }
    }
}
#endif