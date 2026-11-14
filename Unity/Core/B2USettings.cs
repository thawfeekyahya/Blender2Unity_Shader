using System;
using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;

namespace B2U
{
    public enum ShaderOutput { ShaderGraph, HLSL }
    public enum TargetPipeline { URP, BuiltIn }

    /// <summary>Options for one .blend conversion. Serialized by the window so they persist.</summary>
    [Serializable]
    public class ExportOptions
    {
        public string blendPath = "";
        public string outputFolder = "Assets/BlenderImports";
        public ShaderOutput shaderOutput = ShaderOutput.ShaderGraph;
        public TargetPipeline pipeline = TargetPipeline.URP;
        public bool initialized;

        // FBX
        public bool exportFbx = true;
        public float scale = 1f;
        public bool applyModifiers = true;
        public bool bakeAnimation = false;
        public bool includeArmatures = true;
        public bool includeEmpties = true;
        public bool includeLights = false;
        public bool includeCameras = false;
        public bool applyTransform = false;
        public bool bakeAxisConversion = true;

        // Materials
        public bool allMaterials = false;
        public bool bakeUnsupported = true;
        public int bakeSize = 1024;
        public int bakeSamples = 16;
        public bool remapFbxMaterials = true;
        public bool overwriteMaterials = true;

        public static TargetPipeline DetectPipeline()
        {
            var rp = GraphicsSettings.defaultRenderPipeline;
            return rp != null && rp.GetType().Name.Contains("Universal") ? TargetPipeline.URP : TargetPipeline.BuiltIn;
        }

        public List<string> ObjectTypes()
        {
            var t = new List<string> { "MESH", "OTHER" };
            if (includeArmatures) t.Add("ARMATURE");
            if (includeEmpties) t.Add("EMPTY");
            if (includeLights) t.Add("LIGHT");
            if (includeCameras) t.Add("CAMERA");
            return t;
        }
    }

    /// <summary>Per-user settings (Blender executable) stored in EditorPrefs.</summary>
    public static class B2USettings
    {
        const string BlenderPathKey = "B2U.BlenderPath";
        const string TimeoutKey = "B2U.TimeoutMinutes";

        public static string BlenderPath
        {
            get
            {
                var p = EditorPrefs.GetString(BlenderPathKey, "");
                if (string.IsNullOrEmpty(p))
                {
                    p = DetectBlender();
                    if (!string.IsNullOrEmpty(p)) EditorPrefs.SetString(BlenderPathKey, p);
                }
                return p;
            }
            set => EditorPrefs.SetString(BlenderPathKey, value ?? "");
        }

        public static int TimeoutMinutes
        {
            get => EditorPrefs.GetInt(TimeoutKey, 30);
            set => EditorPrefs.SetInt(TimeoutKey, Mathf.Max(1, value));
        }

        public static bool BlenderPathValid
        {
            get
            {
                var p = BlenderPath;
                if (string.IsNullOrEmpty(p)) return false;
                if (p.EndsWith(".app")) return Directory.Exists(p);
                return File.Exists(p);
            }
        }

        /// <summary>Executable to launch (resolves macOS .app bundles).</summary>
        public static string BlenderExecutable
        {
            get
            {
                var p = BlenderPath;
                if (p.EndsWith(".app")) return Path.Combine(p, "Contents", "MacOS", "Blender");
                return p;
            }
        }

        public static string DetectBlender()
        {
            var candidates = new List<string>();
            switch (Application.platform)
            {
                case RuntimePlatform.WindowsEditor:
                    foreach (var root in new[] { Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), @"C:\Program Files" })
                    {
                        var dir = Path.Combine(root, "Blender Foundation");
                        if (!Directory.Exists(dir)) continue;
                        var subs = Directory.GetDirectories(dir);
                        Array.Sort(subs);
                        Array.Reverse(subs);
                        foreach (var s in subs) candidates.Add(Path.Combine(s, "blender.exe"));
                    }
                    var steam = @"C:\Program Files (x86)\Steam\steamapps\common\Blender\blender.exe";
                    candidates.Add(steam);
                    break;
                case RuntimePlatform.OSXEditor:
                    candidates.Add("/Applications/Blender.app");
                    candidates.Add(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Personal), "Applications/Blender.app"));
                    break;
                default:
                    candidates.Add("/usr/bin/blender");
                    candidates.Add("/usr/local/bin/blender");
                    candidates.Add("/snap/bin/blender");
                    candidates.Add("/var/lib/flatpak/exports/bin/org.blender.Blender");
                    candidates.Add(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Personal), "blender/blender"));
                    var optDirs = Directory.Exists("/opt") ? Directory.GetDirectories("/opt", "blender*") : new string[0];
                    foreach (var d in optDirs) candidates.Add(Path.Combine(d, "blender"));
                    break;
            }
            foreach (var c in candidates)
                if (File.Exists(c) || (c.EndsWith(".app") && Directory.Exists(c)))
                    return c;
            return "";
        }

        [SettingsProvider]
        static SettingsProvider CreateProvider()
        {
            return new SettingsProvider("Preferences/Blender to Unity", SettingsScope.User)
            {
                label = "Blender to Unity",
                keywords = new HashSet<string> { "Blender", "FBX", "Shader Graph", "HLSL" },
                guiHandler = _ => DrawPreferences(),
            };
        }

        public static void DrawPreferences()
        {
            EditorGUILayout.Space();
            EditorGUILayout.BeginHorizontal();
            var path = EditorGUILayout.TextField("Blender Executable", BlenderPath);
            if (GUILayout.Button("Browse", GUILayout.Width(70)))
            {
                var ext = Application.platform == RuntimePlatform.WindowsEditor ? "exe"
                        : Application.platform == RuntimePlatform.OSXEditor ? "app" : "";
                var picked = EditorUtility.OpenFilePanel("Locate Blender", Path.GetDirectoryName(path) ?? "", ext);
                if (!string.IsNullOrEmpty(picked)) path = picked;
            }
            if (GUILayout.Button("Detect", GUILayout.Width(60)))
            {
                var d = DetectBlender();
                if (!string.IsNullOrEmpty(d)) path = d;
                else EditorUtility.DisplayDialog("Blender to Unity", "Blender was not found in the usual locations.", "OK");
            }
            EditorGUILayout.EndHorizontal();
            if (path != BlenderPath) BlenderPath = path;
            if (!BlenderPathValid)
                EditorGUILayout.HelpBox("Blender executable not found. Blender 3.6 or newer is recommended.", MessageType.Warning);
            TimeoutMinutes = EditorGUILayout.IntField("Timeout (minutes)", TimeoutMinutes);
            EditorGUILayout.Space();
            B2UBundlePostprocessor.AutoConvert = EditorGUILayout.Toggle(
                new GUIContent("Auto-convert Blender exports",
                    "Convert *.b2u.json bundles written by the Blender add-on as soon as they are imported."),
                B2UBundlePostprocessor.AutoConvert);
        }
    }
}
