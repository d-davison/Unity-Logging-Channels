#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using UnityEditor;
using UnityEngine;
using UnityEngine.UIElements;

namespace Logging.Editor
{
    public class LogSettingsProvider : SettingsProvider
    {
        private SerializedObject _so;
        private SerializedProperty _channels;

        private const float LabelWidth = 400f;
        private const float FoldArrowWidth = 16f;
        private const float ColorSwatchWidth = 36f;
        private const float ColorSwatchHeight = 12f;
        private const float StatusWidth = 90f;

        private static readonly Dictionary<int, bool> _foldoutById = new Dictionary<int, bool>();

        // Styles
        private static GUIStyle _statusEnabledStyle;
        private static GUIStyle _statusDisabledStyle;
        private static GUIStyle _nameRowStyle;
        private static GUIStyle _warnStyle;
        private static GUIContent _warnDuplicateContent;
        private static GUIStyle _statusInfoStyle;
        private static GUIStyle _usageMiniStyle;

        // Manual save baseline (what’s currently in the asset -> last saved)
        private static string _lastEnumSignatureSaved; // based on asset values, sorted

        // Usage scan cache: per-channel id
        private class UsageInfo { public int files; public int matches; }
        private readonly Dictionary<int, UsageInfo> _usageById = new Dictionary<int, UsageInfo>();
        private bool _usageReady;
        private bool _usageScanning;

        public LogSettingsProvider(string path, SettingsScope scope) : base(path, scope) { }
        public static bool IsSettingsAvailable() => LogConfig.Instance != null;

        [SettingsProvider]
        public static SettingsProvider CreateMyCustomSettingsProvider()
        {
            if (!IsSettingsAvailable()) return null;
            var provider = new LogSettingsProvider("Project/Logging", SettingsScope.Project)
            {
                keywords = new HashSet<string>(new[] { "Log", "Logging", "Channels", "Console" })
            };
            return provider;
        }

        public override void OnActivate(string searchContext, VisualElement rootElement)
        {
            _so = new SerializedObject(LogConfig.Instance);
            _channels = _so.FindProperty("channels");
            _lastEnumSignatureSaved = BuildEnumSignatureFromAsset(LogConfig.Instance); // baseline
            RecomputeUsages(); // initial scan
        }

        public override void OnGUI(string searchContext)
        {
            if (_so == null)
            {
                EditorGUILayout.HelpBox("LogConfig not found.", MessageType.Warning);
                return;
            }

            InitStyles();
            _so.Update();

            // Per-frame arrays for identifiers/dropdowns
            BuildChannelArraysFromSerialized(_channels, out var chanNames, out var chanIds, out var chanIdents);

            // Header row with refresh usages
            EditorGUILayout.BeginHorizontal();
            EditorGUILayout.LabelField("Log Channels", EditorStyles.boldLabel);
            GUILayout.FlexibleSpace();
            EditorGUI.BeginDisabledGroup(_usageScanning);
            if (GUILayout.Button("Refresh Usages", GUILayout.Width(120)))
            {
                RecomputeUsages();
            }
            EditorGUI.EndDisabledGroup();
            EditorGUILayout.EndHorizontal();

            EditorGUILayout.Space(4);

            CleanupFoldouts();

            // Draw Default (no delete)
            DrawDefaultCategory(0, chanNames, chanIds, chanIdents);
            EditorGUILayout.Space(6);

            // Draw remaining channels
            for (int i = 0; i < _channels.arraySize; i++)
            {
                if (i == 0) continue;

                var elem = _channels.GetArrayElementAtIndex(i);
                var nameProp = elem.FindPropertyRelative("name");
                var enabledProp = elem.FindPropertyRelative("enabled");
                var colorProp = elem.FindPropertyRelative("color");
                var idProp = elem.FindPropertyRelative("id");
                int id = idProp.intValue;

                if (!_foldoutById.TryGetValue(id, out var _))
                    _foldoutById[id] = false;

                bool isOpen = _foldoutById[id];
                bool isEnabled = enabledProp.boolValue;
                float rowH = EditorGUIUtility.singleLineHeight;

                bool isDuplicate = IsDuplicateName(i, nameProp.stringValue);

                EditorGUILayout.BeginVertical("box");

                // HEADER
                EditorGUILayout.BeginHorizontal();

                // Fold arrow
                var foldRect = GUILayoutUtility.GetRect(FoldArrowWidth, rowH, GUILayout.Width(FoldArrowWidth), GUILayout.Height(rowH));
                _foldoutById[id] = EditorGUI.Foldout(foldRect, isOpen, GUIContent.none, true);

                // Name
                var displayName = string.IsNullOrEmpty(nameProp.stringValue) ? "<unnamed>" : nameProp.stringValue;
                GUILayout.Label(displayName, _nameRowStyle, GUILayout.Height(rowH));

                // Color swatch
                GUILayout.Space(6);
                var swatchRect = GUILayoutUtility.GetRect(ColorSwatchWidth, rowH, GUILayout.Width(ColorSwatchWidth), GUILayout.Height(rowH));
                var r = new Rect(swatchRect.x, swatchRect.y + (swatchRect.height - ColorSwatchHeight) * 0.5f, ColorSwatchWidth, ColorSwatchHeight);
                EditorGUI.DrawRect(r, colorProp.colorValue);
                // Border
                EditorGUI.DrawRect(new Rect(r.x, r.y, r.width, 1), Color.black);
                EditorGUI.DrawRect(new Rect(r.x, r.yMax - 1, r.width, 1), Color.black);
                EditorGUI.DrawRect(new Rect(r.x, r.y, 1, r.height), Color.black);
                EditorGUI.DrawRect(new Rect(r.xMax - 1, r.y, 1, r.height), Color.black);

                // Status toggle
                GUILayout.Space(6);
                var statusRect = GUILayoutUtility.GetRect(StatusWidth, rowH, GUILayout.Width(StatusWidth), GUILayout.Height(rowH));
                var rowStatusStyle = isEnabled ? _statusEnabledStyle : _statusDisabledStyle;
                EditorGUI.LabelField(statusRect, isEnabled ? "Enabled" : "Disabled", rowStatusStyle);
                EditorGUIUtility.AddCursorRect(statusRect, MouseCursor.Link);
                if (GUI.Button(statusRect, GUIContent.none, GUIStyle.none))
                {
                    enabledProp.boolValue = !enabledProp.boolValue;
                    _so.ApplyModifiedProperties();
                    GUI.FocusControl(null);
                    Repaint();
                }

                // Duplicate warning
                if (isDuplicate)
                {
                    GUILayout.Space(6);
                    GUILayout.Label(_warnDuplicateContent, _warnStyle, GUILayout.Height(rowH));
                }

                // Usage counter
                GUILayout.FlexibleSpace();
                var usage = GetUsageForId(id);
                string usageText = _usageReady
                    ? $"Usages: {(usage != null ? usage.matches : 0)} in {(usage != null ? usage.files : 0)} file(s)"
                    : "Usages: scanning...";
                GUILayout.Label(usageText, _usageMiniStyle);

                // Show Usages… button (read-only list)
                GUILayout.Space(6);
                if (GUILayout.Button("Show Usages…", GUILayout.Width(110)))
                {
                    OpenUsagesWindow(displayName, chanIdents, chanIds, i);
                }

                // Delete… button (opens ChannelDeleteWindow with per-usage actions)
                GUILayout.Space(6);
                if (GUILayout.Button("Delete…", GUILayout.Width(80)))
                {
                    string sourceIdent = GetIdentifierForIndex(i, chanIdents, chanIds);
                    ChannelDeleteWindow.ShowForChannel(
                        displayName,
                        id,
                        sourceIdent,
                        chanNames,
                        chanIds,
                        chanIdents,
                        onForceDelete: () =>
                        {
                            _channels.DeleteArrayElementAtIndex(i);
                            _so.ApplyModifiedProperties();
                            RecomputeUsages();
                            Repaint();
                        },
                        onPostModify: () =>
                        {
                            RecomputeUsages();
                            Repaint();
                        });
                }

                EditorGUILayout.EndHorizontal();

                // Expanded
                if (_foldoutById[id])
                {
                    EditorGUILayout.Space(4);

                    // Channel Name (disabled) + Rename…
                    EditorGUILayout.BeginHorizontal();
                    GUILayout.Label("Channel Name", GUILayout.Width(LabelWidth));
                    EditorGUI.BeginDisabledGroup(true);
                    EditorGUILayout.TextField(displayName);
                    EditorGUI.EndDisabledGroup();

                    GUILayout.Space(6);
                    if (GUILayout.Button("Rename…", GUILayout.Width(80)))
                    {
                        ChannelRenameWindow.Show(_so, _channels, i, onFinished: (didRegenerate) =>
                        {
                            if (didRegenerate) SyncSavedBaseline();
                            RecomputeUsages(); // if your provider tracks usages
                            Repaint();
                        });
                    }
                    EditorGUILayout.EndHorizontal();

                    // Color (disabled when channel disabled)
                    EditorGUI.BeginDisabledGroup(!isEnabled);
                    EditorGUILayout.BeginHorizontal();
                    GUILayout.Label("Color", GUILayout.Width(LabelWidth));
                    colorProp.colorValue = EditorGUILayout.ColorField(GUIContent.none, colorProp.colorValue, true, true, false);
                    EditorGUILayout.EndHorizontal();
                    EditorGUI.EndDisabledGroup();

                    // Enabled
                    EditorGUILayout.BeginHorizontal();
                    GUILayout.Label("Enabled", GUILayout.Width(LabelWidth));
                    bool beforeEnabled = enabledProp.boolValue;
                    enabledProp.boolValue = EditorGUILayout.Toggle(enabledProp.boolValue);
                    if (enabledProp.boolValue != beforeEnabled)
                    {
                        _so.ApplyModifiedProperties();
                        GUI.FocusControl(null);
                        Repaint();
                    }
                    EditorGUILayout.EndHorizontal();
                }

                EditorGUILayout.EndVertical();
                EditorGUILayout.Space(4);
            }

            // Change detection + bottom bar
            string currentSigSerialized = BuildEnumSignatureFromSerialized(_channels);
            bool hasEnumChanges = !string.Equals(currentSigSerialized, _lastEnumSignatureSaved, StringComparison.Ordinal);

            EditorGUILayout.BeginHorizontal();

            if (GUILayout.Button("Find Debug.Log Usages", GUILayout.Width(170)))
            {
                DebugLogFinderWindow.ShowWindow();
            }

            GUILayout.Space(8);

            if (GUILayout.Button("Create New", GUILayout.Width(110)))
            {
                _channels.InsertArrayElementAtIndex(_channels.arraySize);
                var elem = _channels.GetArrayElementAtIndex(_channels.arraySize - 1);
                elem.FindPropertyRelative("name").stringValue  = "NewChannel";
                elem.FindPropertyRelative("enabled").boolValue = true;
                elem.FindPropertyRelative("color").colorValue  = Color.white;
                elem.FindPropertyRelative("id").intValue       = 0;
                _so.ApplyModifiedProperties();
                GUI.FocusControl(null);
                Repaint();
                RecomputeUsages();
            }

            GUILayout.FlexibleSpace();

            var statusText = hasEnumChanges ? "Changes detected" : "No changes detected";
            GUILayout.Label(statusText, _statusInfoStyle, GUILayout.Height(EditorGUIUtility.singleLineHeight));

            GUILayout.Space(8);
            EditorGUI.BeginDisabledGroup(!hasEnumChanges);
            if (GUILayout.Button("Save LogChannels Enums", GUILayout.Width(200)))
            {
                _so.ApplyModifiedProperties();
                var cfg = LogConfig.Instance;

                bool namesFixed = EnsureUniqueNamesOnAssetUnderscore();
                if (namesFixed) _so.Update();

                EnsureUniqueIdsOnce(cfg);
                LogEnumGenerator.TryGenerate(true);
                _lastEnumSignatureSaved = BuildEnumSignatureFromAsset(cfg);
                EditorUtility.SetDirty(cfg);

                RecomputeUsages();
            }
            EditorGUI.EndDisabledGroup();

            EditorGUILayout.EndHorizontal();
        }

        // Default category (no delete); includes Show Usages… and toggle
        private void DrawDefaultCategory(int defaultIndex, string[] chanNames, int[] chanIds, string[] chanIdents)
        {
            if (defaultIndex < 0 || defaultIndex >= _channels.arraySize)
                return;

            var elem = _channels.GetArrayElementAtIndex(defaultIndex);
            var enabledProp = elem.FindPropertyRelative("enabled");
            var colorProp = elem.FindPropertyRelative("color");
            var idProp = elem.FindPropertyRelative("id");
            int id = idProp.intValue;

            if (!_foldoutById.TryGetValue(id, out var _))
                _foldoutById[id] = false;

            bool isOpen = _foldoutById[id];
            float rowH = EditorGUIUtility.singleLineHeight;

            EditorGUILayout.BeginVertical("box");

            // Header row
            EditorGUILayout.BeginHorizontal();

            var foldRect = GUILayoutUtility.GetRect(FoldArrowWidth, rowH, GUILayout.Width(FoldArrowWidth), GUILayout.Height(rowH));
            _foldoutById[id] = EditorGUI.Foldout(foldRect, isOpen, GUIContent.none, true);

            var defaultLabel = new GUIStyle(EditorStyles.boldLabel)
            {
                alignment = TextAnchor.MiddleLeft,
                fixedHeight = rowH
            };
            GUILayout.Label("Default", defaultLabel, GUILayout.Height(rowH));

            GUILayout.Space(6);
            var sw = GUILayoutUtility.GetRect(ColorSwatchWidth, rowH, GUILayout.Width(ColorSwatchWidth), GUILayout.Height(rowH));
            var rr = new Rect(sw.x, sw.y + (sw.height - ColorSwatchHeight) * 0.5f, ColorSwatchWidth, ColorSwatchHeight);
            EditorGUI.DrawRect(rr, colorProp.colorValue);
            EditorGUI.DrawRect(new Rect(rr.x, rr.y, rr.width, 1), Color.black);
            EditorGUI.DrawRect(new Rect(rr.x, rr.yMax - 1, rr.width, 1), Color.black);
            EditorGUI.DrawRect(new Rect(rr.x, rr.y, 1, rr.height), Color.black);
            EditorGUI.DrawRect(new Rect(rr.xMax - 1, rr.y, 1, rr.height), Color.black);

            GUILayout.Space(6);
            var statusRect = GUILayoutUtility.GetRect(StatusWidth, rowH, GUILayout.Width(StatusWidth), GUILayout.Height(rowH));
            var rowStatusStyle = enabledProp.boolValue ? _statusEnabledStyle : _statusDisabledStyle;
            EditorGUI.LabelField(statusRect, enabledProp.boolValue ? "Enabled" : "Disabled", rowStatusStyle);
            EditorGUIUtility.AddCursorRect(statusRect, MouseCursor.Link);
            if (GUI.Button(statusRect, GUIContent.none, GUIStyle.none))
            {
                enabledProp.boolValue = !enabledProp.boolValue;
                _so.ApplyModifiedProperties();
                GUI.FocusControl(null);
                Repaint();
            }

            // Usage counter
            GUILayout.FlexibleSpace();
            var usage = GetUsageForId(id);
            string usageText = _usageReady
                ? $"Usages: {(usage != null ? usage.matches : 0)} in {(usage != null ? usage.files : 0)} file(s)"
                : "Usages: scanning...";
            GUILayout.Label(usageText, _usageMiniStyle);

            // Show Usages… button
            GUILayout.Space(6);
            if (GUILayout.Button("Show Usages…", GUILayout.Width(110)))
            {
                OpenUsagesWindow("Default", chanIdents, chanIds, defaultIndex);
            }

            EditorGUILayout.EndHorizontal();

            // Body (read-only)
            if (_foldoutById[id])
            {
                EditorGUILayout.Space(2);
                EditorGUI.BeginDisabledGroup(true);
                EditorGUILayout.BeginHorizontal();
                GUILayout.Label("Channel Name", GUILayout.Width(LabelWidth));
                EditorGUILayout.TextField("Default");
                EditorGUILayout.EndHorizontal();

                EditorGUILayout.BeginHorizontal();
                GUILayout.Label("Color", GUILayout.Width(LabelWidth));
                EditorGUILayout.ColorField(GUIContent.none, colorProp.colorValue, true, true, false);
                EditorGUILayout.EndHorizontal();
                EditorGUI.EndDisabledGroup();

                // Toggle (explicit checkbox)
                EditorGUILayout.Space(4);
                EditorGUILayout.BeginHorizontal();
                GUILayout.Label("Enabled", GUILayout.Width(LabelWidth));
                bool beforeEnabled = enabledProp.boolValue;
                enabledProp.boolValue = EditorGUILayout.Toggle(enabledProp.boolValue);
                if (enabledProp.boolValue != beforeEnabled)
                {
                    _so.ApplyModifiedProperties();
                    GUI.FocusControl(null);
                    Repaint();
                }
                EditorGUILayout.EndHorizontal();
            }

            EditorGUILayout.EndVertical();
        }

        private void OpenUsagesWindow(string displayName, string[] chanIdents, int[] chanIds, int indexInList)
        {
            string sourceIdent = GetIdentifierForIndex(indexInList, chanIdents, chanIds);
            ChannelUsagesWindow.ShowForChannel(displayName, sourceIdent);
        }

        // Allow external helpers (e.g., rename popup) to sync the change baseline after a generate/save.
        public static void SyncSavedBaseline()
        {
            _lastEnumSignatureSaved = BuildEnumSignatureFromAsset(LogConfig.Instance);
        }

        // ===== Usage scanning =====

        private void RecomputeUsages()
        {
            if (_usageScanning) return;
            _usageScanning = true;
            _usageReady = false;
            _usageById.Clear();

            try
            {
                var map = BuildEnumMemberMapFromSerialized(_channels);

                var files = Directory.GetFiles(Application.dataPath, "*.cs", SearchOption.AllDirectories)
                                     .Where(p => !p.Replace('\\', '/')
                                                   .EndsWith("/Logging/Generated/LogChannels.generated.cs", StringComparison.OrdinalIgnoreCase))
                                     .ToArray();

                var rxById = new Dictionary<int, Regex>();
                foreach (var kv in map)
                {
                    var pattern = $@"\bLogChannels\.{Regex.Escape(kv.Value)}\b";
                    rxById[kv.Key] = new Regex(pattern, RegexOptions.CultureInvariant);
                    _usageById[kv.Key] = new UsageInfo { files = 0, matches = 0 };
                }

                foreach (var abs in files)
                {
                    string text;
                    try { text = File.ReadAllText(abs); }
                    catch { continue; }

                    foreach (var kv in rxById)
                    {
                        var matches = kv.Value.Matches(text);
                        int cnt = matches.Count;
                        if (cnt > 0)
                        {
                            var u = _usageById[kv.Key];
                            u.matches += cnt;
                            u.files += 1;
                        }
                    }
                }

                _usageReady = true;
            }
            finally
            {
                _usageScanning = false;
            }
        }

        private UsageInfo GetUsageForId(int id)
        {
            _usageById.TryGetValue(id, out var u);
            return u;
        }

        // ===== Helpers: identifiers, arrays, duplicates, unique names, ids, styling =====

        private static Dictionary<int, string> BuildEnumMemberMapFromSerialized(SerializedProperty channels)
        {
            var taken = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var map = new Dictionary<int, string>();
            for (int i = 0; i < channels.arraySize; i++)
            {
                var elem = channels.GetArrayElementAtIndex(i);
                int id = elem.FindPropertyRelative("id").intValue;
                var name = elem.FindPropertyRelative("name").stringValue ?? "";
                var baseName = Sanitize(name);
                var unique = MakeUniqueMember(baseName, taken);
                map[id] = unique;
            }
            return map;
        }

        private static void BuildChannelArraysFromSerialized(SerializedProperty channels, out string[] names, out int[] ids, out string[] idents)
        {
            var taken = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var nList = new List<string>(channels.arraySize);
            var iList = new List<int>(channels.arraySize);
            var idList = new List<string>(channels.arraySize);
            for (int i = 0; i < channels.arraySize; i++)
            {
                var elem = channels.GetArrayElementAtIndex(i);
                int id = elem.FindPropertyRelative("id").intValue;
                var name = elem.FindPropertyRelative("name").stringValue ?? "";
                var baseName = Sanitize(name);
                var unique = MakeUniqueMember(baseName, taken);
                nList.Add(name);
                iList.Add(id);
                idList.Add(unique);
            }
            names = nList.ToArray();
            ids = iList.ToArray();
            idents = idList.ToArray();
        }

        private string GetIdentifierForIndex(int index, string[] chanIdents, int[] chanIds)
        {
            var id = _channels.GetArrayElementAtIndex(index).FindPropertyRelative("id").intValue;
            int pos = Array.IndexOf(chanIds, id);
            return (pos >= 0 && pos < chanIdents.Length) ? chanIdents[pos] : "Channel";
        }

        private static string MakeUniqueMember(string baseName, HashSet<string> taken)
        {
            var name = baseName;
            int i = 1;
            while (taken.Contains(name))
            {
                name = $"{baseName}_{i}";
                i++;
            }
            taken.Add(name);
            return name;
        }

        private HashSet<string> BuildUsedSetExcluding(int excludeIndex)
        {
            var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            for (int i = 0; i < _channels.arraySize; i++)
            {
                if (i == excludeIndex) continue;
                var n = _channels.GetArrayElementAtIndex(i).FindPropertyRelative("name").stringValue?.Trim();
                if (!string.IsNullOrEmpty(n)) used.Add(n);
            }
            return used;
        }

        private bool IsDuplicateName(int index, string name)
        {
            if (string.IsNullOrWhiteSpace(name)) return false;
            string target = name.Trim();
            for (int i = 0; i < _channels.arraySize; i++)
            {
                if (i == index) continue;
                var other = _channels.GetArrayElementAtIndex(i).FindPropertyRelative("name").stringValue;
                if (!string.IsNullOrEmpty(other) && string.Equals(other.Trim(), target, StringComparison.OrdinalIgnoreCase))
                    return true;
            }
            return false;
        }

        private bool EnsureUniqueNamesOnAssetUnderscore()
        {
            var cfg = LogConfig.Instance;
            if (cfg == null) return false;

            var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            bool changed = false;

            for (int i = 0; i < cfg.channels.Count; i++)
            {
                var ch = cfg.channels[i];
                string baseName = string.IsNullOrWhiteSpace(ch.name) ? "Channel" : ch.name.Trim();

                string unique = MakeUniqueUnderscore(baseName, used);
                if (!string.Equals(unique, ch.name, StringComparison.Ordinal))
                {
                    ch.name = unique;
                    changed = true;
                }
                used.Add(ch.name);
            }

            if (changed)
            {
                // Persist only on explicit Save
            }
            return changed;
        }

        private static string MakeUniqueUnderscore(string baseName, HashSet<string> used)
        {
            if (!used.Contains(baseName))
                return baseName;

            int n = 1;
            string candidate;
            do
            {
                candidate = $"{baseName}_{n}";
                n++;
            } while (used.Contains(candidate));
            return candidate;
        }

        private static void EnsureUniqueIdsOnce(LogConfig cfg)
        {
            var seen = new HashSet<int>();
            foreach (var c in cfg.channels)
            {
                if (c == null) continue;
                if (c.Id == 0 || !seen.Add(c.Id))
                {
                    int guard = 0;
                    do
                    {
                        var g = Guid.NewGuid().ToByteArray();
                        var id = BitConverter.ToInt32(g, 0);
                        if (id == 0) id = 1;
                        typeof(Logging.LogChannelDef)
                            .GetField("id", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)
                            ?.SetValue(c, id);
                        guard++;
                    } while (!seen.Add(c.Id) && guard < 256);
                }
            }
            // Persist only on explicit Save
        }

        private static string BuildEnumSignatureFromSerialized(SerializedProperty channelsProp)
        {
            var parts = new List<string>(channelsProp.arraySize);
            for (int i = 0; i < channelsProp.arraySize; i++)
            {
                var elem = channelsProp.GetArrayElementAtIndex(i);
                var name = elem.FindPropertyRelative("name").stringValue ?? "";
                var id = elem.FindPropertyRelative("id").intValue;
                var safe = Sanitize(name);
                parts.Add($"{id}:{safe}");
            }
            parts.Sort(StringComparer.Ordinal);
            return string.Join("|", parts);
        }

        private static string BuildEnumSignatureFromAsset(LogConfig cfg)
        {
            if (cfg == null || cfg.channels == null) return "";
            var parts = new List<string>(cfg.channels.Count);
            foreach (var c in cfg.channels)
            {
                if (c == null) continue;
                var safe = Sanitize(c.name);
                parts.Add($"{c.Id}:{safe}");
            }
            parts.Sort(StringComparer.Ordinal);
            return string.Join("|", parts);
        }

        private static string Sanitize(string name)
        {
            if (string.IsNullOrEmpty(name)) return "Channel";
            var filtered = new string(name.Select(ch => char.IsLetterOrDigit(ch) ? ch : '_').ToArray());
            if (string.IsNullOrEmpty(filtered)) filtered = "Channel";
            if (!char.IsLetter(filtered[0]) && filtered[0] != '_') filtered = "_" + filtered;
            return filtered;
        }

        private static void InitStyles()
        {
            float rowH = EditorGUIUtility.singleLineHeight;
            if (_nameRowStyle == null)
            {
                _nameRowStyle = new GUIStyle(EditorStyles.label)
                {
                    alignment = TextAnchor.MiddleLeft,
                    wordWrap = false,
                    clipping = TextClipping.Clip,
                    fixedHeight = rowH,
                    margin = new RectOffset(0, 0, 0, 0),
                    padding = new RectOffset(0, 0, 0, 0)
                };
            }
            if (_statusEnabledStyle == null)
            {
                _statusEnabledStyle = new GUIStyle(EditorStyles.miniBoldLabel)
                {
                    alignment = TextAnchor.MiddleLeft,
                    fixedHeight = rowH,
                    margin = new RectOffset(0, 0, 0, 0),
                    padding = new RectOffset(0, 0, 0, 0),
                    clipping = TextClipping.Clip
                };
                _statusEnabledStyle.normal.textColor = new Color(0.20f, 0.60f, 0.20f);
            }
            if (_statusDisabledStyle == null)
            {
                _statusDisabledStyle = new GUIStyle(EditorStyles.miniBoldLabel)
                {
                    alignment = TextAnchor.MiddleLeft,
                    fixedHeight = rowH,
                    margin = new RectOffset(0, 0, 0, 0),
                    padding = new RectOffset(0, 0, 0, 0),
                    clipping = TextClipping.Clip
                };
                _statusDisabledStyle.normal.textColor = new Color(0.75f, 0.20f, 0.20f);
            }
            if (_warnStyle == null)
            {
                _warnStyle = new GUIStyle(EditorStyles.miniLabel)
                {
                    alignment = TextAnchor.MiddleLeft,
                    fixedHeight = EditorGUIUtility.singleLineHeight,
                    margin = new RectOffset(0, 0, 0, 0),
                    padding = new RectOffset(0, 0, 0, 0),
                    clipping = TextClipping.Clip
                };
                _warnStyle.normal.textColor = new Color(1.0f, 0.75f, 0.25f);
            }
            if (_warnDuplicateContent == null)
            {
                var baseIcon = EditorGUIUtility.IconContent("console.warnicon");
                _warnDuplicateContent = new GUIContent(baseIcon.image)
                {
                    text = " Duplicate name",
                    tooltip = "Duplicate name"
                };
            }
            if (_statusInfoStyle == null)
            {
                _statusInfoStyle = new GUIStyle(EditorStyles.miniBoldLabel)
                {
                    alignment = TextAnchor.MiddleLeft
                };
                _statusInfoStyle.normal.textColor = new Color(0.85f, 0.75f, 0.25f);
            }
            if (_usageMiniStyle == null)
            {
                _usageMiniStyle = new GUIStyle(EditorStyles.miniLabel)
                {
                    alignment = TextAnchor.MiddleRight,
                    fixedHeight = rowH
                };
                _usageMiniStyle.normal.textColor = new Color(0.7f, 0.7f, 0.7f);
            }
        }

        private void CleanupFoldouts()
        {
            var currentIds = new HashSet<int>();
            for (int i = 0; i < _channels.arraySize; i++)
            {
                var idProp = _channels.GetArrayElementAtIndex(i).FindPropertyRelative("id");
                currentIds.Add(idProp.intValue);
            }
            var missingKeys = _foldoutById.Keys.Where(k => !currentIds.Contains(k)).ToList();
            foreach (var k in missingKeys) _foldoutById.Remove(k);
        }
    }
}
#endif