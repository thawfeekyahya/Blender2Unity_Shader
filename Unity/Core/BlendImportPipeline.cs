using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using UnityEditor;
using UnityEngine;

namespace B2U
{
    public sealed class ImportReport
    {
        public bool Success;
        public string Error;
        public string OutputFolder;
        public string FbxAssetPath;
        public string BlenderLog;
        public readonly List<string> Warnings = new List<string>();
        public readonly List<string> CreatedAssets = new List<string>();
        public readonly List<string> Materials = new List<string>();
    }

    /// <summary>
    /// End-to-end conversion: Blender (FBX + materials.json + textures) -> Unity assets
    /// (textures, Shader Graph or HLSL shaders, materials, FBX with remapped materials).
    /// </summary>
    public static class BlendImportPipeline
    {
        const string Title = "Blender to Unity";

        public static ImportReport Run(ExportOptions opt)
        {
            var report = new ImportReport();
            var tempDir = Path.Combine(Path.GetTempPath(), "B2U_" + Guid.NewGuid().ToString("N").Substring(0, 8));
            try
            {
                RunInternal(opt, tempDir, report);
            }
            catch (Exception e)
            {
                report.Success = false;
                report.Error = e.Message;
                Debug.LogException(e);
            }
            finally
            {
                EditorUtility.ClearProgressBar();
                try { if (Directory.Exists(tempDir)) Directory.Delete(tempDir, true); } catch { /* best effort */ }
            }
            if (!string.IsNullOrEmpty(report.OutputFolder)) WriteReportFile(report);
            return report;
        }

        /// <summary>Converts an existing materials.json export (no Blender run). Useful for re-generating shaders.</summary>
        public static ImportReport ConvertExport(string exportDir, ExportOptions opt)
        {
            var report = new ImportReport();
            try
            {
                ImportFromExportDir(exportDir, opt, report);
            }
            catch (Exception e)
            {
                report.Error = e.Message;
                Debug.LogException(e);
            }
            finally { EditorUtility.ClearProgressBar(); }
            return report;
        }

        static void RunInternal(ExportOptions opt, string tempDir, ImportReport report)
        {
            if (!B2USettings.BlenderPathValid)
                throw new Exception("Blender executable is not set. Configure it in Preferences > Blender to Unity.");
            var blend = Path.GetFullPath(opt.blendPath ?? "");
            if (!File.Exists(blend)) throw new Exception("Blend file not found: " + blend);
            if (!opt.outputFolder.Replace('\\', '/').StartsWith("Assets"))
                throw new Exception("Output folder must be inside the project's Assets folder.");

            if (opt.exportFbx && !opt.AxesValid)
                throw new Exception($"FBX Forward ({opt.axisForward}) and Up ({opt.axisUp}) must use different axes.");

            Directory.CreateDirectory(tempDir);
            var configPath = Path.Combine(tempDir, "job.json");
            File.WriteAllText(configPath, JsonWriter.Write(BuildJob(opt, tempDir, blend)));

            var result = BlenderRunner.Run(blend, configPath, line =>
                EditorUtility.DisplayCancelableProgressBar(Title, "Blender: " + Truncate(line, 120), 0.3f));
            report.BlenderLog = result.Log;
            if (result.Cancelled) throw new Exception("Cancelled by user.");
            if (!result.Success)
                throw new Exception($"Blender failed (exit code {result.ExitCode}). See the Blender log for details.\n" + LastLines(result.Log, 12));

            ImportFromExportDir(tempDir, opt, report);
        }

        static JObj BuildJob(ExportOptions opt, string outDir, string blend)
        {
            return new JObj
            {
                { "outputDir", outDir },
                { "fbxName", Path.GetFileNameWithoutExtension(blend) + ".fbx" },
                { "exportFbx", opt.exportFbx },
                { "fbx", new JObj
                    {
                        { "scale", opt.scale },
                        { "applyScaleOptions", opt.applyScaleOptions },
                        { "axisForward", opt.axisForward },
                        { "axisUp", opt.axisUp },
                        { "applyUnitScale", opt.applyUnitScale },
                        { "useSpaceTransform", opt.useSpaceTransform },
                        { "applyModifiers", opt.applyModifiers },
                        { "bakeAnimation", opt.bakeAnimation },
                        { "animKeyAllBones", opt.animKeyAllBones },
                        { "animNlaStrips", opt.animNlaStrips },
                        { "animAllActions", opt.animAllActions },
                        { "animForceStartEndKeying", opt.animForceStartEndKeying },
                        { "animSamplingRate", Mathf.Clamp(opt.animSamplingRate, 0.01f, 100f) },
                        { "animSimplify", Mathf.Clamp(opt.animSimplify, 0f, 100f) },
                        { "objectTypes", opt.ObjectTypes() },
                        { "applyTransform", opt.applyTransform },
                    }
                },
                { "allMaterials", opt.allMaterials },
                { "materials", (opt.onlyMaterials ?? "").Split(',').Select(s => s.Trim()).Where(s => s.Length > 0).ToList() },
                { "bakeUnsupported", opt.bakeUnsupported },
                { "bakeSize", Mathf.Clamp(opt.bakeSize, 16, 8192) },
                { "bakeSamples", Mathf.Clamp(opt.bakeSamples, 1, 4096) },
                { "supportedNodes", NodeTranslator.SupportedTypes.OrderBy(s => s).ToList() },
            };
        }

        /// <summary>
        /// Converts a bundle written by the Blender add-on directly into Assets
        /// (&lt;name&gt;.fbx, &lt;name&gt;.b2u.json, textures/). Shaders and materials are created next to it.
        /// </summary>
        public static ImportReport ImportBundle(string manifestAssetPath)
        {
            var report = new ImportReport();
            try
            {
                manifestAssetPath = manifestAssetPath.Replace('\\', '/');
                var root = Path.GetDirectoryName(manifestAssetPath)?.Replace('\\', '/');
                var data = JsonUtility.FromJson<BlendExportData>(File.ReadAllText(ToFull(manifestAssetPath)));
                var opt = new ExportOptions { blendPath = data.blendFile ?? "", outputFolder = root };
                var u = data.unity ?? new BundleUnityOptions();
                opt.shaderOutput = u.shaderOutput == "HLSL" ? ShaderOutput.HLSL : ShaderOutput.ShaderGraph;
                opt.pipeline = u.pipeline == "BuiltIn" ? TargetPipeline.BuiltIn
                             : u.pipeline == "URP" ? TargetPipeline.URP : ExportOptions.DetectPipeline();
                opt.bakeAnimation = u.bakeAnimation;
                ImportFromExportDir(ToFull(root), opt, report, Path.GetFileName(manifestAssetPath), root);
            }
            catch (Exception e)
            {
                report.Success = false;
                report.Error = e.Message;
                Debug.LogException(e);
            }
            finally { EditorUtility.ClearProgressBar(); }
            if (!string.IsNullOrEmpty(report.OutputFolder)) WriteReportFile(report);
            return report;
        }

        static void ImportFromExportDir(string exportDir, ExportOptions opt, ImportReport report,
                                        string manifestFile = "materials.json", string bundleRoot = null)
        {
            var jsonPath = Path.Combine(exportDir, manifestFile);
            if (!File.Exists(jsonPath)) throw new Exception("Blender did not produce " + manifestFile + ".");
            var data = JsonUtility.FromJson<BlendExportData>(File.ReadAllText(jsonPath));
            report.Warnings.AddRange(data.warnings);

            var baseName = !string.IsNullOrEmpty(data.blendFile)
                ? Path.GetFileNameWithoutExtension(data.blendFile)
                : Path.GetFileNameWithoutExtension(opt.blendPath);
            // Unity-driven exports get their own folder; add-on bundles are converted where they are.
            var root = bundleRoot ?? EnsureFolder(opt.outputFolder.TrimEnd('/', '\\') + "/" + SafeFileName(baseName));
            report.OutputFolder = root;
            var texFolder = bundleRoot != null ? root + "/textures" : EnsureFolder(root + "/Textures");
            var shaderFolder = EnsureFolder(root + "/Shaders");
            var matFolder = EnsureFolder(root + "/Materials");

            // 1. Translate every material first (texture import settings depend on usage).
            EditorUtility.DisplayProgressBar(Title, "Translating node trees...", 0.5f);
            var graphs = new List<IRGraph>();
            foreach (var m in data.materials)
            {
                try
                {
                    var g = NodeTranslator.Translate(m, data);
                    graphs.Add(g);
                    foreach (var w in g.Warnings) report.Warnings.Add($"[{m.name}] {w}");
                }
                catch (Exception e)
                {
                    report.Warnings.Add($"[{m.name}] Translation failed: {e.Message}");
                }
            }

            // 2. Textures.
            var texAssets = ImportTextures(exportDir, data, texFolder, graphs, report);

            // 3. Shaders + materials.
            var materials = new Dictionary<string, Material>();
            for (int k = 0; k < graphs.Count; k++)
            {
                var g = graphs[k];
                EditorUtility.DisplayProgressBar(Title, "Generating shader for " + g.MaterialName, 0.6f + 0.3f * k / Math.Max(1, graphs.Count));
                foreach (var t in g.Textures)
                    texAssets.TryGetValue(t.ImageName, out t.AssetPath);
                try
                {
                    var shader = opt.shaderOutput == ShaderOutput.ShaderGraph
                        ? WriteShaderGraph(g, opt, shaderFolder, baseName, report)
                        : WriteHlsl(g, opt, shaderFolder, baseName, report);
                    var mat = CreateMaterial(g, shader, matFolder, opt, report);
                    if (mat != null)
                    {
                        materials[g.MaterialName] = mat;
                        report.Materials.Add(g.MaterialName);
                    }
                }
                catch (Exception e)
                {
                    report.Warnings.Add($"[{g.MaterialName}] Shader generation failed: {e.Message}");
                    Debug.LogException(e);
                }
            }

            // 4. FBX.
            if (!string.IsNullOrEmpty(data.fbxFile))
            {
                EditorUtility.DisplayProgressBar(Title, "Importing FBX...", 0.95f);
                var src = Path.Combine(exportDir, data.fbxFile);
                if (File.Exists(src))
                {
                    var dst = root + "/" + data.fbxFile;
                    if (!SamePath(src, ToFull(dst))) File.Copy(src, ToFull(dst), true);
                    AssetDatabase.ImportAsset(dst, ImportAssetOptions.ForceUpdate);
                    ConfigureModel(dst, materials, opt, report);
                    report.FbxAssetPath = dst;
                    report.CreatedAssets.Add(dst);
                }
                else report.Warnings.Add("FBX file was not produced by Blender.");
            }

            AssetDatabase.SaveAssets();
            report.Success = true;
        }

        // ------------------------------------------------------------------------------
        // Textures
        // ------------------------------------------------------------------------------

        static Dictionary<string, string> ImportTextures(string exportDir, BlendExportData data, string texFolder,
                                                         List<IRGraph> graphs, ImportReport report)
        {
            var map = new Dictionary<string, string>();
            var copied = new List<(BlendImage img, string asset)>();
            AssetDatabase.StartAssetEditing();
            try
            {
                foreach (var img in data.images)
                {
                    if (string.IsNullOrEmpty(img.file)) continue;
                    var src = Path.Combine(exportDir, img.file);
                    if (!File.Exists(src)) { report.Warnings.Add($"Texture file missing for image '{img.name}'."); continue; }
                    var asset = texFolder + "/" + Path.GetFileName(img.file);
                    if (!SamePath(src, ToFull(asset))) File.Copy(src, ToFull(asset), true);
                    map[img.name] = asset;
                    copied.Add((img, asset));
                }
            }
            finally { AssetDatabase.StopAssetEditing(); }

            // Usage flags gathered from all graphs.
            var usages = graphs.SelectMany(g => g.Textures).GroupBy(t => t.ImageName).ToDictionary(x => x.Key, x => x.First());
            foreach (var (img, asset) in copied)
            {
                AssetDatabase.ImportAsset(asset, ImportAssetOptions.ForceUpdate);
                var importer = AssetImporter.GetAtPath(asset) as TextureImporter;
                if (importer == null) continue;
                importer.textureType = TextureImporterType.Default;
                importer.sRGBTexture = img.srgb;
                importer.alphaSource = TextureImporterAlphaSource.FromInput;
                importer.alphaIsTransparency = false;
                importer.mipmapEnabled = true;
                if (usages.TryGetValue(img.name, out var use))
                {
                    importer.wrapMode = use.Clamp ? TextureWrapMode.Clamp : TextureWrapMode.Repeat;
                    importer.filterMode = use.Closest ? FilterMode.Point : FilterMode.Bilinear;
                }
                if (!img.srgb) importer.textureCompression = TextureImporterCompression.CompressedHQ;
                importer.SaveAndReimport();
                report.CreatedAssets.Add(asset);
            }
            return map;
        }

        // ------------------------------------------------------------------------------
        // Shaders
        // ------------------------------------------------------------------------------

        static Shader WriteShaderGraph(IRGraph g, ExportOptions opt, string folder, string baseName, ImportReport report)
        {
            var file = SafeFileName(g.MaterialName);
            var includePath = folder + "/" + file + "_B2U.hlsl";
            File.WriteAllText(ToFull(includePath), ShaderGraphWriter.WriteInclude(g));
            AssetDatabase.ImportAsset(includePath, ImportAssetOptions.ForceUpdate);
            var includeGuid = AssetDatabase.AssetPathToGUID(includePath);
            report.CreatedAssets.Add(includePath);

            var writer = new ShaderGraphWriter(g, opt.pipeline == TargetPipeline.URP ? GraphTarget.URP : GraphTarget.BuiltIn,
                includeGuid, t => string.IsNullOrEmpty(t.AssetPath) ? "" : AssetDatabase.AssetPathToGUID(t.AssetPath))
            {
                GraphPath = "Blender/" + baseName,
            };
            var graphPath = folder + "/" + file + ".shadergraph";
            File.WriteAllText(ToFull(graphPath), writer.Write());
            AssetDatabase.ImportAsset(graphPath, ImportAssetOptions.ForceUpdate);
            report.CreatedAssets.Add(graphPath);

            var shader = AssetDatabase.LoadAssetAtPath<Shader>(graphPath);
            if (shader == null)
                report.Warnings.Add($"[{g.MaterialName}] Shader Graph was written but did not import as a shader. " +
                                    "Is the Shader Graph package (and the URP / Built-in target) installed?");
            return shader;
        }

        static Shader WriteHlsl(IRGraph g, ExportOptions opt, string folder, string baseName, ImportReport report)
        {
            var file = SafeFileName(g.MaterialName);
            var path = folder + "/" + file + ".shader";
            var name = "Blender/" + baseName + "/" + g.MaterialName;
            var code = HlslShaderWriter.Write(g, opt.pipeline == TargetPipeline.URP ? HlslPipeline.URP : HlslPipeline.BuiltIn, name);
            File.WriteAllText(ToFull(path), code);
            AssetDatabase.ImportAsset(path, ImportAssetOptions.ForceUpdate);
            report.CreatedAssets.Add(path);
            var shader = AssetDatabase.LoadAssetAtPath<Shader>(path);
            if (shader != null && ShaderUtil.ShaderHasError(shader))
                report.Warnings.Add($"[{g.MaterialName}] Generated shader has compile errors; select '{path}' to inspect them.");
            return shader;
        }

        // ------------------------------------------------------------------------------
        // Materials & model
        // ------------------------------------------------------------------------------

        static Material CreateMaterial(IRGraph g, Shader shader, string folder, ExportOptions opt, ImportReport report)
        {
            if (shader == null) return null;
            var path = folder + "/" + SafeFileName(g.MaterialName) + ".mat";
            var mat = AssetDatabase.LoadAssetAtPath<Material>(path);
            if (mat != null && !opt.overwriteMaterials) return mat;

            var isNew = mat == null;
            if (isNew) mat = new Material(shader) { name = g.MaterialName };
            else mat.shader = shader;

            foreach (var t in g.Textures)
            {
                if (string.IsNullOrEmpty(t.AssetPath) || !mat.HasProperty(t.PropertyName)) continue;
                mat.SetTexture(t.PropertyName, AssetDatabase.LoadAssetAtPath<Texture>(t.AssetPath));
            }
            if (mat.HasProperty("_Cutoff")) mat.SetFloat("_Cutoff", g.AlphaThreshold);
            mat.doubleSidedGI = g.DoubleSided;

            if (isNew) AssetDatabase.CreateAsset(mat, path);
            else EditorUtility.SetDirty(mat);
            report.CreatedAssets.Add(path);
            return mat;
        }

        static void ConfigureModel(string fbxPath, Dictionary<string, Material> materials, ExportOptions opt, ImportReport report)
        {
            var importer = AssetImporter.GetAtPath(fbxPath) as ModelImporter;
            if (importer == null) return;
            importer.bakeAxisConversion = opt.bakeAxisConversion;
            importer.importAnimation = opt.bakeAnimation;
            importer.materialImportMode = ModelImporterMaterialImportMode.ImportStandard;
            importer.materialLocation = ModelImporterMaterialLocation.InPrefab;

            if (opt.remapFbxMaterials)
            {
                var fbxMaterialNames = AssetDatabase.LoadAllAssetsAtPath(fbxPath).OfType<Material>().Select(m => m.name).ToList();
                foreach (var kv in materials)
                {
                    importer.AddRemap(new AssetImporter.SourceAssetIdentifier(typeof(Material), kv.Key), kv.Value);
                    // Blender appends ".001" style suffixes; FBX keeps them, but some exporters sanitize names.
                    var alt = fbxMaterialNames.FirstOrDefault(n => n != kv.Key && SafeFileName(n) == SafeFileName(kv.Key));
                    if (alt != null)
                        importer.AddRemap(new AssetImporter.SourceAssetIdentifier(typeof(Material), alt), kv.Value);
                }
                var unmatched = fbxMaterialNames.Where(n => !materials.ContainsKey(n) && materials.Keys.All(k => SafeFileName(k) != SafeFileName(n))).ToList();
                foreach (var n in unmatched) report.Warnings.Add($"FBX material '{n}' has no converted counterpart.");
            }
            importer.SaveAndReimport();
        }

        // ------------------------------------------------------------------------------
        // Utilities
        // ------------------------------------------------------------------------------

        static string EnsureFolder(string assetPath)
        {
            assetPath = assetPath.Replace('\\', '/').TrimEnd('/');
            if (AssetDatabase.IsValidFolder(assetPath)) return assetPath;
            var parent = Path.GetDirectoryName(assetPath)?.Replace('\\', '/');
            if (!string.IsNullOrEmpty(parent) && !AssetDatabase.IsValidFolder(parent)) EnsureFolder(parent);
            AssetDatabase.CreateFolder(parent, Path.GetFileName(assetPath));
            return assetPath;
        }

        static bool SamePath(string a, string b) =>
            string.Equals(Path.GetFullPath(a).TrimEnd('/', '\\'), Path.GetFullPath(b).TrimEnd('/', '\\'),
                Application.platform == RuntimePlatform.LinuxEditor ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase);

        static string ToFull(string assetPath) =>
            Path.Combine(Path.GetDirectoryName(Application.dataPath) ?? "", assetPath);

        public static string SafeFileName(string s)
        {
            var invalid = Path.GetInvalidFileNameChars();
            var sb = new StringBuilder();
            foreach (var c in s ?? "") sb.Append(invalid.Contains(c) || c == '.' ? '_' : c);
            var r = sb.ToString().Trim();
            return r.Length == 0 ? "Unnamed" : r;
        }

        static string Truncate(string s, int n) => s == null ? "" : s.Length <= n ? s : s.Substring(0, n) + "...";

        static string LastLines(string s, int n)
        {
            if (string.IsNullOrEmpty(s)) return "";
            var lines = s.Split('\n');
            return string.Join("\n", lines.Skip(Math.Max(0, lines.Length - n)));
        }

        static void WriteReportFile(ImportReport r)
        {
            var sb = new StringBuilder();
            sb.AppendLine("Blender to Unity conversion report - " + DateTime.Now);
            sb.AppendLine(r.Success ? "Status: OK" : "Status: FAILED - " + r.Error);
            sb.AppendLine();
            sb.AppendLine("Materials:");
            foreach (var m in r.Materials) sb.AppendLine("  " + m);
            sb.AppendLine();
            sb.AppendLine("Warnings:");
            foreach (var w in r.Warnings) sb.AppendLine("  " + w);
            sb.AppendLine();
            sb.AppendLine("Blender log:");
            sb.AppendLine(r.BlenderLog);
            try
            {
                var path = r.OutputFolder + "/B2U_Report.txt";
                File.WriteAllText(ToFull(path), sb.ToString());
                AssetDatabase.ImportAsset(path);
            }
            catch { /* reporting must never fail the import */ }
        }
    }
}
