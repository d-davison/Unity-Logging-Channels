using Logging;
using UnityEngine;

public class SendLogToChannel : MonoBehaviour
{
    [SerializeField] private LogChannels logChannels;
    [SerializeField] private string log;

    public void SendLog()
    {
        Log.Send(logChannels, log);
    }

    public void SendLog2()
    {

        
        Log.Send(LogChannels.Default, log);
        Log.Send(LogChannels.Default, log);
        Log.Send(LogChannels.Default, log);
    }
}
