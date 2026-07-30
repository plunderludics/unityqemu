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

                EditorGUILayout.HelpBox(
                    "Used as the starting folder for PeripheralsUI file/folder pickers. " +
                    "Snapshot save/load keeps using the current snap or disk location.",
                    MessageType.Info);
            },
        };
    }
}
}
#endif
