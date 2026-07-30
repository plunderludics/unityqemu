using System;
using TriInspector;
using UnityEngine;

namespace UnityQemu {
/// <summary>
/// Options for an auto-constructed <see cref="RenderTexture"/>.
/// Size comes from the source (guest framebuffer / blit size reference).
/// </summary>
[Serializable]
public class RenderTextureSettings
{
    [Tooltip(
        "When on, recreate the auto RenderTexture if the source width/height change. " +
        "When off, keep the first size created for this session.")]
    [LabelText("Auto Resize")]
    public bool autoResize = true;

    [Tooltip("Filter mode applied to the auto-created RenderTexture.")]
    [LabelText("Filter Mode")]
    public FilterMode filterMode = FilterMode.Point;

    /// <summary>
    /// True when <paramref name="rt"/> is missing, filter differs, or
    /// (when <see cref="autoResize"/>) size differs from <paramref name="width"/>×<paramref name="height"/>.
    /// </summary>
    public bool NeedsRecreate(RenderTexture rt, int width, int height)
    {
        if (rt == null)
            return true;
        if (rt.filterMode != filterMode)
            return true;
        if (autoResize && (rt.width != width || rt.height != height))
            return true;
        return false;
    }

    /// <summary>Create a depth-0 RenderTexture with these settings.</summary>
    public RenderTexture Create(int width, int height, string name)
    {
        var rt = new RenderTexture(width, height, depth: 0)
        {
            name = name ?? "Auto RenderTexture",
            filterMode = filterMode,
        };
        rt.Create();
        return rt;
    }

    /// <summary>
    /// Ensure <paramref name="rt"/> exists at <paramref name="width"/>×<paramref name="height"/>
    /// with the current filter. Recreates when <see cref="NeedsRecreate"/>; otherwise
    /// only ensures <see cref="RenderTexture.IsCreated"/>.
    /// </summary>
    public RenderTexture Ensure(
        RenderTexture rt,
        int width,
        int height,
        string name,
        Action<RenderTexture> release)
    {
        if (width <= 0 || height <= 0)
            return rt;

        if (!NeedsRecreate(rt, width, height))
        {
            if (rt != null && !rt.IsCreated())
                rt.Create();
            return rt;
        }

        if (rt != null)
            release?.Invoke(rt);

        return Create(width, height, name);
    }
}
}
