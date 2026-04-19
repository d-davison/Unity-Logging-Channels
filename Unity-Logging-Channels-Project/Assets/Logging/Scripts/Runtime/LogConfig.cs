using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;

namespace Logging
{
    [Serializable]
    public class LogChannelDef
    {
        public string name = "DefaultName";
        public bool enabled = true;
        public Color color = Color.white;

        [SerializeField] private int id; // stable ID for enum value, not shown in UI
        public int Id => id;

        public void EnsureId()
        {
            if (id != 0) return;
            var g = Guid.NewGuid().ToByteArray();
            id = BitConverter.ToInt32(g, 0);
            if (id == 0) id = 1;
        }

        public void SetId(int newID)
        {
            id = newID;
        }
    }

    public class LogConfig : ScriptableObject
    {
        public List<LogChannelDef> channels = new  List<LogChannelDef>();

        private Dictionary<string, LogChannelDef> _byName;
        private Dictionary<int, LogChannelDef> _byId;

        private void OnValidate()
        {
            CheckForDefaultChannel();
            foreach (var c in channels) c.EnsureId();
            
            //EnsureUniqueIds();
            BuildLookups();
        }

        private void Reset()
        {
            Debug.Log("Resetting LogConfig");
            CheckForDefaultChannel();
        }
        
        private void EnsureUniqueIds()
        {
            var seen = new HashSet<int>();
            foreach (var c in channels)
            {
                if (c == null) continue;
                if (c.Id == 0 || !seen.Add(c.Id))
                {
                    do { c.EnsureId(); } while (!seen.Add(c.Id));
                }
            }
        }

        private void BuildLookups()
        {
            _byName = new Dictionary<string, LogChannelDef>(StringComparer.OrdinalIgnoreCase);
            _byId = new Dictionary<int, LogChannelDef>();
            foreach (var c in channels)
            {
                if (string.IsNullOrWhiteSpace(c.name)) continue;
                _byName[c.name] = c;
                _byId[c.Id] = c;
            }
        }

        private void CheckForDefaultChannel()
        {
            bool containsDefaultChannel = channels.Any(c => c.name == "Default");

            if (!containsDefaultChannel)
            {
                var defaultChannel = new LogChannelDef { name = "Default" };
                defaultChannel.SetId(0);
                channels.Add(defaultChannel);
            }
        }

        public LogChannelDef GetByName(string name)
        {
            if (_byName == null) BuildLookups();
            return name != null && _byName.TryGetValue(name, out var c) ? c : null;
        }

        public LogChannelDef GetById(int id)
        {
            if (_byId == null) BuildLookups();
            return _byId.TryGetValue(id, out var c) ? c : null;
        }

        private static LogConfig _instance;
        public static LogConfig Instance
        {
            get
            {
                if (_instance == null)
                {
                    _instance = Resources.Load<LogConfig>("LogConfig");
#if UNITY_EDITOR
                    if (_instance == null)
                    {
                        _instance = CreateInstance<LogConfig>();
                        System.IO.Directory.CreateDirectory("Assets/Resources");
                        UnityEditor.AssetDatabase.CreateAsset(_instance, "Assets/Resources/LogConfig.asset");
                        UnityEditor.AssetDatabase.SaveAssets();
                    }
#endif
                }
                return _instance;
            }
        }
    }
}