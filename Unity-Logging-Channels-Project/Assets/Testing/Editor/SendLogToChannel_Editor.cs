#if UNITY_EDITOR
using UnityEditor;
using UnityEngine;

[CustomEditor(typeof(SendLogToChannel))]
public class SendLogToChannel_Editor : Editor
{
    public override void OnInspectorGUI()
    {
        base.OnInspectorGUI();
        
        SendLogToChannel sendLogToChannel = (SendLogToChannel)target;

        if (GUILayout.Button("Send", GUILayout.Width(100)))
        {
            sendLogToChannel.SendLog();
        }
    }
}
#endif