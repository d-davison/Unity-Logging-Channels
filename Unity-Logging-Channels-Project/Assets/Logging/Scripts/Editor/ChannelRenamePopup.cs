#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using UnityEditor;
using UnityEngine;

namespace Logging.Editor
{
    public class ChannelRenameWindow : EditorWindow
    {
        private SerializedObject _settingsSO;
        private SerializedProperty _channelsProp;
        private int _index;
        private Action<bool> _onFinished; // bool = did regenerate enums?

        private string _oldName;
        private string _newName;
        private string _error;

        private string _oldIdent;
        private int _usageFiles;
        private int _usageCount;

        private Vector2 _scroll;

        public static void Show(SerializedObject settingsSO, SerializedProperty channelsProp, int index, Action<bool> onFinished = null)
        {
            var w = GetWindow<ChannelRenameWindow>(true, "Rename Channel", true);
            w.minSize = new Vector2(420, 180);
            w.position = new Rect(Screen.currentResolution.width / 2f - 220, Screen.currentResolution.height / 2f - 120, 440, 220);
            w.Init(settingsSO, channelsProp, index, onFinished);
            w.ShowUtility();
            w.Focus();
        }

        private void Init(SerializedObject settingsSO, SerializedProperty channelsProp, int index, Action<bool> onFinished)
        {
            _settingsSO = settingsSO;
            _channelsProp = channelsProp;
            _index = index;
            _onFinished = onFinished;

            var elem = _channelsProp.GetArrayElementAtIndex(index);
            _oldName = elem.FindPropertyRelative("name").stringValue ?? string.Empty;
            _newName = _oldName;

            // Build identifier map (id -> unique sanitized enum name)
            var cfg = LogConfig.Instance;
            var id = elem.FindPropertyRelative("id").intValue;
            var oldMap = BuildEnumMemberMap(cfg.channels.Select(c => (c.Id, c.name)).ToList());
            _oldIdent = oldMap.TryGetValue(id, out var ident) ? ident : Sanitize(_oldName);

            CountEnumUsages(_oldIdent, out _usageFiles, out _usageCount);
        }

        private void OnGUI()
        {
            if (_settingsSO == null || _channelsProp == null || _index < 0 || _index >= _channelsProp.arraySize)
            {
                EditorGUILayout.HelpBox("Channel reference lost. Please reopen the rename window.", MessageType.Warning);
                if (GUILayout.Button("Close")) Close();
                return;
            }

            _scroll = EditorGUILayout.BeginScrollView(_scroll);

            EditorGUILayout.Space(4);
            EditorGUILayout.LabelField("Rename Channel", EditorStyles.boldLabel);

            using (new EditorGUILayout.HorizontalScope())
            {
                GUILayout.Label("Current", GUILayout.Width(100));
                EditorGUILayout.LabelField($"{_oldName} (LogChannels.{_oldIdent})", EditorStyles.miniLabel);
            }

            using (new EditorGUILayout.HorizontalScope())
            {
                GUILayout.Label("New name", GUILayout.Width(100));
                GUI.SetNextControlName("NewNameField");
                _newName = EditorGUILayout.TextField(_newName);
            }

            EditorGUILayout.LabelField(
                $"Usages of LogChannels.{_oldIdent}: {_usageCount} occurrence(s) in {_usageFiles} file(s).",
                EditorStyles.miniLabel);

            ValidateName();

            if (!string.IsNullOrEmpty(_error))
                EditorGUILayout.HelpBox(_error, MessageType.Warning);

            EditorGUILayout.Space();

            using (new EditorGUILayout.HorizontalScope())
            {
                GUILayout.FlexibleSpace();

                bool noChange = string.Equals((_newName ?? "").Trim(), (_oldName ?? "").Trim(), StringComparison.Ordinal);
                EditorGUI.BeginDisabledGroup(!string.IsNullOrEmpty(_error) || noChange);
                if (GUILayout.Button("Rename", GUILayout.Width(100)))
                {
                    DoRenameAndRefactor(_newName);
                }
                EditorGUI.EndDisabledGroup();

                if (GUILayout.Button("Cancel", GUILayout.Width(80)))
                {
                    _onFinished?.Invoke(false);
                    Close();
                }
            }

            EditorGUILayout.EndScrollView();

            // Keyboard shortcuts
            var e = Event.current;
            if (e.type == EventType.KeyDown)
            {
                if (e.keyCode == KeyCode.Escape)
                {
                    _onFinished?.Invoke(false);
                    Close();
                }
                else if (e.keyCode == KeyCode.Return || e.keyCode == KeyCode.KeypadEnter)
                {
                    if (string.IsNullOrEmpty(_error) && !string.Equals((_newName ?? "").Trim(), (_oldName ?? "").Trim(), StringComparison.Ordinal))
                    {
                        DoRenameAndRefactor(_newName);
                    }
                }
            }
        }

        private void ValidateName()
        {
            _error = null;
            if (string.IsNullOrWhiteSpace(_newName))
            {
                _error = "Name cannot be empty.";
                return;
            }
            if (IsIllegalName(_newName))
            {
                _error = "Illegal name (Default cannot be used).";
                return;
            }
            if (IsDuplicateName(_channelsProp, _index, _newName))
            {
                _error = "Duplicate name.";
                return;
            }
        }

        private void DoRenameAndRefactor(string newDisplayName)
        {
            var cfg = LogConfig.Instance;
            if (cfg == null) return;

            // Map BEFORE rename
            var oldMap = BuildEnumMemberMap(cfg.channels.Select(c => (c.Id, c.name)).ToList());

            // Apply rename on the serialized property (does not hard-save to disk)
            var elem = _channelsProp.GetArrayElementAtIndex(_index);
            elem.FindPropertyRelative("name").stringValue = newDisplayName;
            _settingsSO.ApplyModifiedProperties();

            // Map AFTER rename
            var newMap = BuildEnumMemberMap(cfg.channels.Select(c => (c.Id, c.name)).ToList());

            // Compute identifier rename pairs
            var renamePairs = new List<(string oldIdent, string newIdent)>();
            foreach (var kvp in oldMap)
            {
                var id = kvp.Key;
                var oldIdent = kvp.Value;
                var newIdent = newMap[id];
                if (!string.Equals(oldIdent, newIdent, StringComparison.Ordinal))
                    renamePairs.Add((oldIdent, newIdent));
            }

            if (renamePairs.Count == 0)
            {
                EditorUtility.DisplayDialog("Rename", "Nothing to rename in code (enum identifiers unchanged).", "OK");
                _onFinished?.Invoke(false);
                Close();
                return;
            }

            // Refactor across project (.cs under Assets), excluding generated enum
            RefactorEnumUsages(renamePairs, out int filesChanged, out int totalReplacements);

            // Ask to regenerate enum now (optional)
            bool regen = EditorUtility.DisplayDialog("Rename complete",
                $"Updated {totalReplacements} occurrence(s) in {filesChanged} file(s).\n\n" +
                "Do you want to regenerate LogChannels enum now?",
                "Generate Now", "Later");

            if (regen)
            {
                EnsureUniqueNamesOnAssetUnderscore(cfg);
                EnsureUniqueIdsOnce(cfg);
                LogEnumGenerator.TryGenerate(true);
                EditorUtility.SetDirty(cfg); // persist because user chose a write step
                LogSettingsProvider.SyncSavedBaseline();
            }

            _onFinished?.Invoke(regen);
            Close();
        }

        // ---------------- Utilities ----------------

        private static void CountEnumUsages(string ident, out int files, out int matches)
        {
            files = 0;
            matches = 0;
            var projectPath = Application.dataPath;
            var csFiles = Directory.GetFiles(projectPath, "*.cs", SearchOption.AllDirectories)
                                   .Where(p => !p.Replace('\\', '/').EndsWith("/Logging/Generated/LogChannels.generated.cs", StringComparison.OrdinalIgnoreCase))
                                   .ToArray();
            var pattern = $@"\bLogChannels\.{Regex.Escape(ident)}\b";
            var rx = new Regex(pattern, RegexOptions.CultureInvariant);

            foreach (var filePath in csFiles)
            {
                string text;
                try { text = File.ReadAllText(filePath); }
                catch { continue; }
                var m = rx.Matches(text);
                if (m.Count > 0)
                {
                    files++;
                    matches += m.Count;
                }
            }
        }

        private static bool IsDuplicateName(SerializedProperty channels, int index, string name)
        {
            if (string.IsNullOrWhiteSpace(name)) return false;
            string target = name.Trim();
            for (int i = 0; i < channels.arraySize; i++)
            {
                if (i == index) continue;
                var other = channels.GetArrayElementAtIndex(i).FindPropertyRelative("name").stringValue;
                if (!string.IsNullOrEmpty(other) && string.Equals(other.Trim(), target, StringComparison.OrdinalIgnoreCase))
                    return true;
            }
            return false;
        }

        private static bool IsIllegalName(string name)
        {
            if (string.IsNullOrWhiteSpace(name)) return false;
            return string.Equals(name.Trim(), "Default", StringComparison.OrdinalIgnoreCase);
        }

        private static Dictionary<int, string> BuildEnumMemberMap(List<(int Id, string Name)> rows)
        {
            var taken = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var map = new Dictionary<int, string>();
            foreach (var row in rows)
            {
                var baseName = Sanitize(row.Name);
                var unique = MakeUniqueMember(baseName, taken);
                map[row.Id] = unique;
            }
            return map;
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

        private static void RefactorEnumUsages(List<(string oldIdent, string newIdent)> pairs,
                                               out int filesChanged, out int totalReplacements)
        {
            filesChanged = 0;
            totalReplacements = 0;

            // Sort by descending old name length to avoid partial overlaps
            pairs.Sort((a, b) => b.oldIdent.Length.CompareTo(a.oldIdent.Length));

            var projectPath = Application.dataPath;
            var csFiles = Directory.GetFiles(projectPath, "*.cs", SearchOption.AllDirectories)
                                   .Where(p => !p.Replace('\\', '/').EndsWith("/Logging/Generated/LogChannels.generated.cs", StringComparison.OrdinalIgnoreCase))
                                   .ToArray();

            AssetDatabase.StartAssetEditing();
            try
            {
                foreach (var filePath in csFiles)
                {
                    string text;
                    try { text = File.ReadAllText(filePath); }
                    catch { continue; }

                    var original = text;
                    foreach (var (oldIdent, newIdent) in pairs)
                    {
                        var pattern = $@"\bLogChannels\.{Regex.Escape(oldIdent)}\b";
                        text = Regex.Replace(text, pattern, $"LogChannels.{newIdent}", RegexOptions.CultureInvariant);
                    }

                    if (!ReferenceEquals(text, original) && !string.Equals(text, original, StringComparison.Ordinal))
                    {
                        File.WriteAllText(filePath, text);
                        filesChanged++;

                        // Count replacements approximately
                        foreach (var (oldIdent, _) in pairs)
                        {
                            var m = Regex.Matches(original, $@"\bLogChannels\.{Regex.Escape(oldIdent)}\b", RegexOptions.CultureInvariant);
                            totalReplacements += m.Count;
                            original = Regex.Replace(original, $@"\bLogChannels\.{Regex.Escape(oldIdent)}\b", "", RegexOptions.CultureInvariant);
                        }

                        var rel = "Assets" + filePath.Substring(Application.dataPath.Length);
                        AssetDatabase.ImportAsset(rel);
                    }
                }
            }
            finally
            {
                AssetDatabase.StopAssetEditing();
            }
        }

        private static void EnsureUniqueNamesOnAssetUnderscore(LogConfig cfg)
        {
            var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var ch in cfg.channels)
            {
                string baseName = string.IsNullOrWhiteSpace(ch.name) ? "Channel" : ch.name.Trim();
                var name = baseName;
                int n = 1;
                while (used.Contains(name)) { name = $"{baseName}_{n++}"; }
                ch.name = name;
                used.Add(name);
            }
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
        }

        private static string Sanitize(string name)
        {
            if (string.IsNullOrEmpty(name)) return "Channel";
            var filtered = new string(name.Select(ch => char.IsLetterOrDigit(ch) ? ch : '_').ToArray());
            if (string.IsNullOrEmpty(filtered)) filtered = "Channel";
            if (!char.IsLetter(filtered[0]) && filtered[0] != '_') filtered = "_" + filtered;
            return filtered;
        }
    }
}
#endif