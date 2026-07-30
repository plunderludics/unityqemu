using System;
using System.Threading.Tasks;
using TriInspector;
using UnityEngine;

namespace UnityQemu {
/// <summary>
/// Inspector UI for sending HMP monitor commands over QMP (no native QEMU GUI needed).
/// </summary>
[ExecuteAlways]
public class MonitorUI : MonoBehaviour
{
    public VirtualMachine virtualMachine;

#if UNITY_EDITOR
    [ShowInInspector, ReadOnly]
    bool QmpReady => virtualMachine != null && virtualMachine.QmpConnected;

    [Tooltip("HMP command to run (e.g. info mice, mouse_set 3)")]
    public string command = "info mice";

    [TextArea(6, 20)]
    [ReadOnly]
    public string lastOutput = "";

    void OnEnable()
    {
        if (virtualMachine == null)
            virtualMachine = GetComponent<VirtualMachine>();
    }

    [Button("Run")]
    public async void RunButton()
    {
        await RunAsync(command);
    }

    [Button("info mice")]
    public async void InfoMiceButton()
    {
        command = "info mice";
        await RunAsync(command);
    }

    [Button("info usb")]
    public async void InfoUsbButton()
    {
        command = "info usb";
        await RunAsync(command);
    }

    public async Task<string> RunAsync(string commandLine)
    {
        if (virtualMachine == null)
        {
            lastOutput = "No VirtualMachine assigned";
            Debug.LogWarning(lastOutput);
            return lastOutput;
        }

        if (string.IsNullOrWhiteSpace(commandLine))
        {
            lastOutput = "Empty command";
            return lastOutput;
        }

        try
        {
            string result = await virtualMachine.RunHumanMonitorCommandAsync(
                commandLine.Trim(), expectTextOutput: true);
            lastOutput = VirtualMachine.IsHmpSuccessReply(result)
                ? "(ok)"
                : result.TrimEnd();
            Debug.Log($"HMP `{commandLine.Trim()}`:\n{lastOutput}");
            return lastOutput;
        }
        catch (Exception e)
        {
            lastOutput = e.Message;
            Debug.LogError($"HMP failed: {e.Message}");
            return lastOutput;
        }
    }
#endif
}
}
