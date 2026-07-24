using System;
using System.IO;
using System.Reflection;
using Microsoft.Unity.VisualStudio.Editor;
using Unity.CodeEditor;
using UnityEditor;
using UnityEngine;

/// <summary>
/// Generates .sln/.csproj for Cursor IntelliSense / Go to Definition.
/// Unity's Visual Studio Editor package only discovers *Code*.exe, so Cursor.exe makes
/// <see cref="CodeEditor.CurrentEditor.SyncAll"/> a no-op. We invoke the real SDK-style
/// generator directly (plain <see cref="ProjectGeneration"/> has a stub header and NREs).
/// </summary>
static class RegenerateCsharpProjectFiles
{
    const string CursorExe =
        @"C:\Users\darwi\AppData\Local\Programs\cursor\Cursor.exe";

    [MenuItem("Assets/Open C# Project (Regenerate)")]
    [MenuItem("UnityQemu/Regenerate C# Project Files")]
    public static void Regenerate()
    {
        // Embedded|Local so Packages/org.plunderludics.unityqemu gets its own .csproj
        const string prefKey = "unity_project_generation_flag";
        const int embeddedLocalRegistryGit = 1 | 2 | 4 | 8;
        int flags = EditorPrefs.GetInt(prefKey, 1 | 2);
        if ((flags & embeddedLocalRegistryGit) != embeddedLocalRegistryGit)
            EditorPrefs.SetInt(prefKey, flags | embeddedLocalRegistryGit);

        if (File.Exists(CursorExe))
        {
            string current = CodeEditor.CurrentEditorPath;
            if (string.IsNullOrEmpty(current) ||
                !string.Equals(Path.GetFullPath(current), Path.GetFullPath(CursorExe),
                    StringComparison.OrdinalIgnoreCase))
            {
                CodeEditor.SetExternalScriptEditor(CursorExe);
            }
        }

        CreateSdkStyleGenerator().Sync();

        string root = Path.GetFullPath(Path.Combine(Application.dataPath, ".."));
        int csprojCount = Directory.GetFiles(root, "*.csproj").Length;
        bool hasSln = Directory.GetFiles(root, "*.sln").Length > 0;
        Debug.Log(
            $"UnityQemu: regenerated C# project files " +
            $"(sln={(hasSln ? "yes" : "no")}, csproj={csprojCount}). " +
            "In Cursor: Developer: Reload Window, then try Go to Definition again.");
    }

    /// <summary>
    /// <c>GeneratorFactory.GetInstance(SDK)</c> is internal; invoke it via reflection.
    /// Do not use <c>new ProjectGeneration()</c> — base GetProjectHeader leaves headerBuilder null.
    /// </summary>
    static IGenerator CreateSdkStyleGenerator()
    {
        Assembly asm = typeof(ProjectGeneration).Assembly;
        Type factoryType = asm.GetType(
            "Microsoft.Unity.VisualStudio.Editor.GeneratorFactory", throwOnError: true);
        Type styleType = asm.GetType(
            "Microsoft.Unity.VisualStudio.Editor.GeneratorStyle", throwOnError: true);
        object sdkStyle = Enum.Parse(styleType, "SDK");
        MethodInfo getInstance = factoryType.GetMethod(
            "GetInstance",
            BindingFlags.Public | BindingFlags.Static,
            binder: null,
            types: new[] { styleType },
            modifiers: null);
        if (getInstance == null)
            throw new MissingMethodException(factoryType.FullName, "GetInstance");

        return (IGenerator)getInstance.Invoke(null, new[] { sdkStyle });
    }
}
