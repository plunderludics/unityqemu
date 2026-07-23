using System;
using UnityEngine;

namespace UnityQemu {
/// <summary>
/// Extra Unity-side data for durable <c>.uqsnap</c> images (embedded <c>savevm</c> + launch config).
/// Plain <c>.qcow2</c> disks leave <see cref="DiskAsset.hasUqsnapMetadata"/> false.
/// User-facing annotations live on <see cref="DiskAsset.note"/>.
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

    public static UqsnapMetadata CreateEmpty() => new UqsnapMetadata
    {
        createdAt = "",
        launchConfig = LaunchConfig.CreateDefault(),
        qemuVersion = "",
        unityQemuVersion = "",
    };

    public UqsnapMetadata Clone()
    {
        return new UqsnapMetadata
        {
            createdAt = createdAt ?? "",
            launchConfig = launchConfig != null ? launchConfig.Clone() : LaunchConfig.CreateDefault(),
            qemuVersion = qemuVersion ?? "",
            unityQemuVersion = unityQemuVersion ?? "",
        };
    }

    public void StampCreatedNow()
    {
        createdAt = DateTime.UtcNow.ToString("o");
    }
}
}
