using UnityEngine;
using UnityQemu;
using System.Text;
using TriInspector;

namespace UnityQemu {
/// <summary>
/// Simple inspector hex dump of guest memory via QemuEmulator's gdbstub API.
/// </summary>
public class QemuMemViewer : MonoBehaviour
{
    public QemuEmulator qemu;
    public long startAddress = 0x0;
    public int length = 256;
    public int bytesPerRow = 16;
    [Tooltip("How often to refresh the dump (seconds). 0 = every frame (slow).")]
    public float refreshInterval = 0.5f;

    [ReadOnly, TextArea(10, 20)]
    public string memoryHex;

    [ReadOnly]
    public string status;

    float _nextRefresh;

    void Update()
    {
        if (qemu == null)
        {
            status = "No QemuEmulator assigned";
            return;
        }

        if (!qemu.GdbConnected)
        {
            status = "GDB not connected";
            return;
        }

        if (refreshInterval > 0f && Time.unscaledTime < _nextRefresh)
        {
            return;
        }
        _nextRefresh = Time.unscaledTime + refreshInterval;

        try
        {
            byte[] bytes = qemu.ReadBytes(startAddress, length);
            var sb = new StringBuilder(length * 3 + length / Mathf.Max(1, bytesPerRow));
            for (int i = 0; i < bytes.Length; i++)
            {
                if (i > 0 && i % bytesPerRow == 0)
                {
                    sb.AppendLine();
                }
                sb.Append(bytes[i].ToString("X2"));
                sb.Append(' ');
            }
            memoryHex = sb.ToString();
            status = $"OK @ 0x{startAddress:X} ({length} bytes)";
        }
        catch (System.Exception e)
        {
            status = $"Read failed: {e.Message}";
            memoryHex = "";
        }
    }

    [Button("Write test byte 0xAB at startAddress")]
    public void WriteTestByte()
    {
        if (qemu == null || !qemu.GdbConnected)
        {
            Debug.LogWarning("GDB not connected");
            return;
        }
        qemu.WriteUnsigned(startAddress, 0xAB, 1, false);
        _nextRefresh = 0f;
        Debug.Log($"Wrote 0xAB to 0x{startAddress:X}");
    }
}
}
