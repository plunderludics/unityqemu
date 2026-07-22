using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using TriInspector;
using UnityEngine;
using UnityEngine.Serialization;
#if UNITY_EDITOR
using UnityEditor;
#endif

namespace UnityQemu {
/// <summary>
/// Inspector UI for QEMU internal snapshots (HMP savevm / loadvm / delvm).
/// </summary>
[ExecuteAlways]
public class SnapshotUI : MonoBehaviour
{
    [FormerlySerializedAs("qemu")]
    public VirtualMachine virtualMachine;

    [Tooltip("Refresh the snapshot list when the QEMU emulator finishes starting")]
    public bool refreshOnReady = true;

#if UNITY_EDITOR
    [ShowInInspector, ReadOnly]
    bool QmpReady => virtualMachine != null && virtualMachine.QmpConnected;

    [ListDrawerSettings(
        Draggable = false,
        HideAddButton = true,
        HideRemoveButton = true,
        AlwaysExpanded = true,
        ShowElementLabels = false)]
    [SerializeField]
    List<SnapshotEntry> snapshots = new List<SnapshotEntry>();

    VirtualMachine _boundMachine;

    void OnEnable()
    {
        if (virtualMachine == null)
            virtualMachine = GetComponent<VirtualMachine>();
        BindMachine(virtualMachine);

        if (refreshOnReady && virtualMachine != null && virtualMachine.QmpConnected)
            _ = RefreshSnapshotsAsync();
    }

    void OnDisable()
    {
        BindMachine(null);
    }

    void OnValidate()
    {
        if (!Application.isPlaying && virtualMachine != _boundMachine)
            BindMachine(virtualMachine);
    }

    void BindMachine(VirtualMachine next)
    {
        if (_boundMachine != null)
            _boundMachine.OnReady -= HandleMachineReady;
        _boundMachine = next;
        if (_boundMachine != null)
            _boundMachine.OnReady += HandleMachineReady;
    }

    void HandleMachineReady()
    {
        if (!refreshOnReady)
            return;
        _ = RefreshSnapshotsAsync();
    }

    [Button("Refresh Snapshots")]
    public async void RefreshSnapshotsButton()
    {
        await RefreshSnapshotsAsync();
    }

    [Button("Save New Snapshot")]
    public async void SaveNewSnapshotButton()
    {
        if (virtualMachine == null || !virtualMachine.QmpConnected)
        {
            Debug.LogWarning("QMP not connected");
            return;
        }

        string tag = SnapshotNameDialog.Prompt("snap1");
        if (string.IsNullOrWhiteSpace(tag))
            return;

        tag = tag.Trim();
        if (tag.IndexOfAny(new[] { ' ', '\t' }) >= 0)
        {
            Debug.LogWarning($"Snapshot name cannot contain spaces: '{tag}'");
            return;
        }

        await SaveTagWithPauseAsync(tag);
    }

    /// <summary>Pause, savevm (overwrites if tag exists), resume, refresh list.</summary>
    async Task SaveTagWithPauseAsync(string tag)
    {
        if (virtualMachine == null || !virtualMachine.QmpConnected)
        {
            Debug.LogWarning("QMP not connected");
            return;
        }

        bool paused = false;
        try
        {
            await virtualMachine.PauseAsync();
            paused = true;
            await SaveVmAsync(tag);
            await RefreshSnapshotsAsync();
        }
        catch (Exception e)
        {
            Debug.LogError($"Failed to save snapshot '{tag}': {e.Message}");
        }
        finally
        {
            if (paused)
            {
                try { await virtualMachine.ResumeAsync(); }
                catch (Exception e) { Debug.LogError($"Failed to resume after savevm: {e.Message}"); }
            }
        }
    }

    public async Task RefreshSnapshotsAsync()
    {
        try
        {
            string output = await RunHmpAsync("info snapshots");
            var names = ParseSnapshotNames(output);
            snapshots.Clear();
            foreach (string name in names)
            {
                snapshots.Add(new SnapshotEntry { name = name, owner = this });
            }
            foreach (var entry in snapshots)
                entry.owner = this;
        }
        catch (Exception e)
        {
            snapshots.Clear();
            Debug.LogError($"Failed to list snapshots: {e.Message}");
        }
    }

    public async Task SaveVmAsync(string tag)
    {
        await RunHmpAsync($"savevm {tag}");
    }

    public async Task LoadVmAsync(string tag)
    {
        await RunHmpAsync($"loadvm {tag}");
    }

    public async Task DeleteVmAsync(string tag)
    {
        await RunHmpAsync($"delvm {tag}");
    }

    async Task LoadEntryAsync(SnapshotEntry entry)
    {
        if (entry == null || string.IsNullOrEmpty(entry.name))
            return;
        await LoadVmAsync(entry.name);
    }

    async Task SaveEntryAsync(SnapshotEntry entry)
    {
        if (entry == null || string.IsNullOrEmpty(entry.name))
            return;
        await SaveTagWithPauseAsync(entry.name);
    }

    async Task DeleteEntryAsync(SnapshotEntry entry)
    {
        if (entry == null || string.IsNullOrEmpty(entry.name))
            return;
        await DeleteVmAsync(entry.name);
        await RefreshSnapshotsAsync();
    }

    async Task<string> RunHmpAsync(string command)
    {
        if (virtualMachine == null)
            throw new InvalidOperationException("No VirtualMachine assigned");
        return await virtualMachine.RunHumanMonitorCommandAsync(command);
    }

    static List<string> ParseSnapshotNames(string infoSnapshotsOutput)
    {
        var names = new List<string>();
        if (string.IsNullOrWhiteSpace(infoSnapshotsOutput))
            return names;
        if (infoSnapshotsOutput.IndexOf("no snapshot", StringComparison.OrdinalIgnoreCase) >= 0)
            return names;

        var lineRe = new Regex(@"^\s*(\S+)\s+(\S+)\s+", RegexOptions.Compiled);
        foreach (string rawLine in infoSnapshotsOutput.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries))
        {
            string line = rawLine.TrimEnd();
            if (line.StartsWith("List of snapshots", StringComparison.OrdinalIgnoreCase))
                continue;
            if (line.StartsWith("ID", StringComparison.OrdinalIgnoreCase) &&
                line.IndexOf("TAG", StringComparison.OrdinalIgnoreCase) >= 0)
                continue;

            Match m = lineRe.Match(line);
            if (!m.Success)
                continue;

            string id = m.Groups[1].Value;
            string tag = m.Groups[2].Value;
            if (tag.Equals("TAG", StringComparison.OrdinalIgnoreCase))
                continue;
            if (id != "--" && !Regex.IsMatch(id, @"^\d+$"))
                continue;
            if (!names.Contains(tag))
                names.Add(tag);
        }
        return names;
    }

    [Serializable]
    [DeclareHorizontalGroup("actions")]
    public class SnapshotEntry
    {
        [HideLabel, DisplayAsString]
        public string name;

        [NonSerialized] public SnapshotUI owner;

        [Group("actions")]
        [Button("Load")]
        public void Load()
        {
            if (owner == null)
            {
                Debug.LogWarning("Snapshot entry has no owner (refresh the list)");
                return;
            }
            _ = owner.LoadEntryAsync(this);
        }

        [Group("actions")]
        [Button("Save")]
        public void Save()
        {
            if (owner == null)
            {
                Debug.LogWarning("Snapshot entry has no owner (refresh the list)");
                return;
            }
            _ = owner.SaveEntryAsync(this);
        }

        [Group("actions")]
        [GUIColor(1.0f, 0.6f, 0.6f)]
        [Button("Delete")]
        public void Delete()
        {
            if (owner == null)
            {
                Debug.LogWarning("Snapshot entry has no owner (refresh the list)");
                return;
            }
            _ = owner.DeleteEntryAsync(this);
        }
    }

#endif
}
}
