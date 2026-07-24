using System;
using UnityEngine;

namespace UnityQemu {
/// <summary>
/// Extra Unity-side data for durable <c>.uqsnap</c> images (launch config + versions).
/// Lives on <see cref="UqsnapAsset"/>; plain disks have none.
/// User-facing annotations live on <see cref="DiskAsset.note"/> / <see cref="UqsnapAsset.note"/>.
/// </summary>
[Serializable]
public class UqsnapMetadata
{
    [Tooltip("ISO-8601 timestamp when the snapshot was created")]
    public string createdAt;

    [Tooltip("Extra QEMU args and removable media recorded when this snapshot was saved.")]
    public LaunchConfig launchConfig = LaunchConfig.CreateDefault();

    [Tooltip("qemu-system version string at save time (for load-time warnings).")]
    public string qemuVersion;

    [Tooltip("UnityQemu package version at save time.")]
    public string unityQemuVersion;

    [Tooltip(
        "When true, the machine-state file is stored raw (faster save/load, larger). " +
        "When false (default, including older snapshots), it is gzip-compressed.")]
    public bool vmstateUncompressed;

    /// <summary>True when the <c>.uqsnap</c> machine-state bytes should be read/written as gzip.</summary>
    public bool VmstateIsCompressed => !vmstateUncompressed;

    public static UqsnapMetadata CreateEmpty() => new UqsnapMetadata
    {
        createdAt = "",
        launchConfig = LaunchConfig.CreateDefault(),
        qemuVersion = "",
        unityQemuVersion = "",
        vmstateUncompressed = false,
    };

    public UqsnapMetadata Clone()
    {
        return new UqsnapMetadata
        {
            createdAt = createdAt ?? "",
            launchConfig = launchConfig != null ? launchConfig.Clone() : LaunchConfig.CreateDefault(),
            qemuVersion = qemuVersion ?? "",
            unityQemuVersion = unityQemuVersion ?? "",
            vmstateUncompressed = vmstateUncompressed,
        };
    }

    public void StampCreatedNow()
    {
        createdAt = DateTime.UtcNow.ToString("o");
    }
}
}
