using System;
using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEngine;

namespace B2U
{
    /// <summary>
    /// Detects bundles exported by the Blender add-on (*.b2u.json next to an FBX and textures/)
    /// and converts them to Shader Graph / HLSL shaders and materials automatically.
    /// </summary>
    public class B2UBundlePostprocessor : AssetPostprocessor
    {
        public const string ManifestSuffix = ".b2u.json";
        const string AutoConvertKey = "B2U.AutoConvertBundles";

        static readonly HashSet<string> Pending = new HashSet<string>();

        public static bool AutoConvert
        {
            get => EditorPrefs.GetBool(AutoConvertKey, true);
            set => EditorPrefs.SetBool(AutoConvertKey, value);
        }

        public static bool IsManifest(string path) =>
            !string.IsNullOrEmpty(path) && path.EndsWith(ManifestSuffix, StringComparison.OrdinalIgnoreCase);

        static void OnPostprocessAllAssets(string[] imported, string[] deleted, string[] moved, string[] movedFrom)
        {
            if (!AutoConvert) return;
            var any = false;
            foreach (var p in imported)
                if (IsManifest(p) && Pending.Add(p)) any = true;
            if (!any) return;
            // Never touch the AssetDatabase from inside an import callback.
            EditorApplication.delayCall -= ProcessPending;
            EditorApplication.delayCall += ProcessPending;
        }

        static void ProcessPending()
        {
            var list = Pending.ToList();
            Pending.Clear();
            foreach (var p in list) Convert(p);
        }

        public static void Convert(string manifestAssetPath)
        {
            var r = BlendImportPipeline.ImportBundle(manifestAssetPath);
            if (r.Success)
            {
                Debug.Log($"[Blender to Unity] Converted {r.Materials.Count} material(s) from {manifestAssetPath}" +
                          (r.Warnings.Count > 0 ? $" with {r.Warnings.Count} warning(s); see {r.OutputFolder}/B2U_Report.txt" : "."));
                foreach (var w in r.Warnings) Debug.LogWarning("[Blender to Unity] " + w);
            }
            else
            {
                Debug.LogError($"[Blender to Unity] Conversion of {manifestAssetPath} failed: {r.Error}");
            }
        }

        [MenuItem("Assets/Blender to Unity/Convert Blender Export Bundle", false, 2001)]
        static void ConvertSelected()
        {
            foreach (var o in Selection.objects)
            {
                var p = AssetDatabase.GetAssetPath(o);
                if (IsManifest(p)) Convert(p);
            }
        }

        [MenuItem("Assets/Blender to Unity/Convert Blender Export Bundle", true)]
        static bool ConvertSelectedValidate() => Selection.objects.Any(o => IsManifest(AssetDatabase.GetAssetPath(o)));
    }
}
