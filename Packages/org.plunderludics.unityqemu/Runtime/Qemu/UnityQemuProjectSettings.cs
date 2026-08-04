#if UNITY_EDITOR
using System.IO;
using UnityEditor;
using UnityEngine;

namespace UnityQemu {
/// <summary>
/// Per-project UnityQemu editor settings (Project Settings → UnityQemu).
/// </summary>
[FilePath("ProjectSettings/UnityQemuSettings.asset", FilePathAttribute.Location.ProjectFolder)]
public sealed class UnityQemuProjectSettings : ScriptableSingleton<UnityQemuProjectSettings>
{
    const string DefaultQemuDirectory = "Assets/qemu";

    [SerializeField]
    [Tooltip("Project-relative folder used as the default for media file/folder pickers " +
             "(ISO, floppy, vvfat). Snapshot save/load still uses the snap/disk folder.")]
    string qemuDirectory = DefaultQemuDirectory;

    [SerializeField]
    [Tooltip(
        "When on (default), Windows player builds copy only qemu-i386.manifest (~120 MB). " +
        "When off, copy the entire Windows qemu tree (~1.2 GB). Ignored for macOS/Linux " +
        "(full host tree is copied; PE trim does not apply).")]
    bool trimQemuToI386 = true;

    [SerializeField]
    [Tooltip(
        "When on, player builds store guest images under QemuAssets as SHA-256(path) filenames " +
        "(qcow2 backing headers are rebased). Off by default.")]
    bool obfuscateGuestFileNames = false;

    /// <summary>Project-relative media root (e.g. <c>Assets/qemu</c>).</summary>
    public string QemuDirectory
    {
        get
        {
            if (string.IsNullOrWhiteSpace(qemuDirectory))
                return DefaultQemuDirectory;
            return qemuDirectory.Replace('\\', '/').Trim().TrimEnd('/');
        }
        set
        {
            qemuDirectory = string.IsNullOrWhiteSpace(value)
                ? DefaultQemuDirectory
                : value.Replace('\\', '/').Trim().TrimEnd('/');
            Save(true);
        }
    }

    /// <summary>
    /// When true, player builds package only <c>qemu-i386.manifest</c>; otherwise the full
    /// <c>qemu~</c> tree.
    /// </summary>
    public bool TrimQemuToI386
    {
        get => trimQemuToI386;
        set
        {
            trimQemuToI386 = value;
            Save(true);
        }
    }

    /// <summary>
    /// When true, guest images in the player build use opaque SHA-256 filenames derived from
    /// their project-relative paths.
    /// </summary>
    public bool ObfuscateGuestFileNames
    {
        get => obfuscateGuestFileNames;
        set
        {
            obfuscateGuestFileNames = value;
            Save(true);
        }
    }

    /// <summary>
    /// Absolute directory for <c>OpenFilePanel</c>/<c>OpenFolderPanel</c>, or empty when
    /// the configured folder does not exist yet.
    /// </summary>
    public static string GetPickerDirectory()
    {
        string rel = instance.QemuDirectory;
        string abs = Path.GetFullPath(Path.Combine(Application.dataPath, "..", rel));
        return Directory.Exists(abs) ? abs : "";
    }

    [SettingsProvider]
    static SettingsProvider CreateSettingsProvider()
    {
        return new SettingsProvider("Project/UnityQemu", SettingsScope.Project)
        {
            label = "UnityQemu",
            guiHandler = _ =>
            {
                var settings = instance;

                EditorGUI.BeginChangeCheck();
                DefaultAsset folder = AssetDatabase.LoadAssetAtPath<DefaultAsset>(
                    settings.QemuDirectory);
                folder = (DefaultAsset)EditorGUILayout.ObjectField(
                    new GUIContent(
                        "QEMU Directory",
                        "Default folder for media pickers (ISO, floppy, vvfat)."),
                    folder,
                    typeof(DefaultAsset),
                    false);
                if (EditorGUI.EndChangeCheck())
                {
                    string path = folder != null ? AssetDatabase.GetAssetPath(folder) : "";
                    if (!string.IsNullOrEmpty(path) && !AssetDatabase.IsValidFolder(path))
                    {
                        Debug.LogWarning(
                            $"UnityQemu: '{path}' is not a folder — keeping previous QEMU Directory.");
                    }
                    else
                    {
                        settings.QemuDirectory = string.IsNullOrEmpty(path)
                            ? DefaultQemuDirectory
                            : path;
                    }
                }

                EditorGUI.BeginChangeCheck();
                bool trim = EditorGUILayout.Toggle(
                    new GUIContent(
                        "Trim QEMU To i386",
                        "Windows player builds: copy only qemu-i386.manifest (~120 MB) instead of " +
                        "the full Windows qemu tree (~1.2 GB). macOS/Linux always copy their full tree."),
                    settings.TrimQemuToI386);
                if (EditorGUI.EndChangeCheck())
                    settings.TrimQemuToI386 = trim;

                EditorGUI.BeginChangeCheck();
                bool obfuscate = EditorGUILayout.Toggle(
                    new GUIContent(
                        "Obfuscate Guest File Names",
                        "Player builds: store disks/ISOs/etc. as SHA-256(project path) under " +
                        "QemuAssets (rebase qcow2 backing headers). Off by default."),
                    settings.ObfuscateGuestFileNames);
                if (EditorGUI.EndChangeCheck())
                    settings.ObfuscateGuestFileNames = obfuscate;

                EditorGUILayout.HelpBox(
                    "QEMU Directory is the starting folder for PeripheralsUI media pickers. " +
                    "Snapshot save/load keeps using the current snap or disk location.\n\n" +
                    "Host QEMU trees live under Packages/…/qemu~/win|macos|macos-x64|linux " +
                    "(Windows also accepts a legacy flat qemu~ layout). See docs/host-qemu.md.\n\n" +
                    "Trim QEMU To i386 packages Windows qemu-system-i386 + qemu-img + DLL closure + " +
                    "SeaBIOS (regenerate qemu-i386.manifest after updating QEMU). " +
                    "Turn off only if you need other softmmu arches or the full share/ tree.\n\n" +
                    "Obfuscate Guest File Names hides original filenames in the build folder; " +
                    "content is unchanged (not encryption).\n\n" +
                    "Durable .uqsnap save uses migrate fd: on Windows (get-win32-socket) " +
                    "and on macOS/Linux (getfd over unix-domain QMP).",
                    MessageType.Info);
            },
        };
    }
}
}
#endif
