using System;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEngine;

namespace B2U
{
    public class BlenderToUnityWindow : EditorWindow
    {
        const string PrefsKey = "B2U.WindowOptions";

        [SerializeField] ExportOptions _opt = new ExportOptions();
        Vector2 _scroll;
        Vector2 _warnScroll;
        ImportReport _last;
        bool _showFbx = true, _showTransform = true, _showMaterials = true, _showAnim = true, _showLog;

        [MenuItem("Tools/Blender to Unity/Converter", priority = 0)]
        public static BlenderToUnityWindow Open()
        {
            var w = GetWindow<BlenderToUnityWindow>("Blender to Unity");
            w.minSize = new Vector2(420, 520);
            return w;
        }

        [MenuItem("Tools/Blender to Unity/Preferences", priority = 20)]
        static void OpenPrefs() => SettingsService.OpenUserPreferences("Preferences/Blender to Unity");

        [MenuItem("Assets/Blender to Unity/Convert .blend (FBX + Shaders)", false, 2000)]
        static void ConvertSelected()
        {
            var path = AssetDatabase.GetAssetPath(Selection.activeObject);
            var w = Open();
            w._opt.blendPath = Path.GetFullPath(path);
            w._opt.outputFolder = Path.GetDirectoryName(path)?.Replace('\\', '/') ?? "Assets";
            w.Repaint();
        }

        [MenuItem("Assets/Blender to Unity/Convert .blend (FBX + Shaders)", true)]
        static bool ConvertSelectedValidate()
        {
            var path = AssetDatabase.GetAssetPath(Selection.activeObject);
            return !string.IsNullOrEmpty(path) && path.EndsWith(".blend", System.StringComparison.OrdinalIgnoreCase);
        }

        void OnEnable()
        {
            var json = EditorPrefs.GetString(PrefsKey, "");
            if (!string.IsNullOrEmpty(json))
            {
                try { JsonUtility.FromJsonOverwrite(json, _opt); } catch { /* corrupt prefs */ }
            }
            if (!_opt.initialized)
            {
                _opt.pipeline = ExportOptions.DetectPipeline();
                _opt.initialized = true;
            }
        }

        void OnDisable() => EditorPrefs.SetString(PrefsKey, JsonUtility.ToJson(_opt));

        void OnGUI()
        {
            _scroll = EditorGUILayout.BeginScrollView(_scroll);

            EditorGUILayout.LabelField("Blender to Unity Shader Converter", EditorStyles.boldLabel);
            EditorGUILayout.HelpBox(
                "Exports a .blend file to FBX through Blender, converts every Blender shader node tree to a " +
                "Shader Graph or an HLSL shader, creates materials and assigns them to the imported model.",
                MessageType.None);

            if (!B2USettings.BlenderPathValid)
            {
                EditorGUILayout.HelpBox("Blender executable not configured.", MessageType.Error);
                B2USettings.DrawPreferences();
            }
            else
            {
                EditorGUILayout.LabelField("Blender", B2USettings.BlenderPath, EditorStyles.miniLabel);
            }

            EditorGUILayout.Space();
            EditorGUILayout.LabelField("Source", EditorStyles.boldLabel);
            using (new EditorGUILayout.HorizontalScope())
            {
                _opt.blendPath = EditorGUILayout.TextField(".blend File", _opt.blendPath);
                if (GUILayout.Button("...", GUILayout.Width(30)))
                {
                    var p = EditorUtility.OpenFilePanel("Select .blend file", DirOf(_opt.blendPath), "blend");
                    if (!string.IsNullOrEmpty(p)) _opt.blendPath = p;
                }
            }
            HandleDragAndDrop(GUILayoutUtility.GetLastRect());

            using (new EditorGUILayout.HorizontalScope())
            {
                _opt.outputFolder = EditorGUILayout.TextField("Output Folder", _opt.outputFolder);
                if (GUILayout.Button("...", GUILayout.Width(30)))
                {
                    var p = EditorUtility.OpenFolderPanel("Output folder (inside Assets)", Application.dataPath, "");
                    if (!string.IsNullOrEmpty(p))
                    {
                        var root = Path.GetDirectoryName(Application.dataPath)?.Replace('\\', '/') + "/";
                        p = p.Replace('\\', '/');
                        if (p.StartsWith(root)) _opt.outputFolder = p.Substring(root.Length);
                        else EditorUtility.DisplayDialog("Blender to Unity", "Please choose a folder inside Assets.", "OK");
                    }
                }
            }

            EditorGUILayout.Space();
            EditorGUILayout.LabelField("Shader Conversion", EditorStyles.boldLabel);
            _opt.shaderOutput = (ShaderOutput)EditorGUILayout.EnumPopup(
                new GUIContent("Output", "Shader Graph: editable .shadergraph assets. HLSL: hand-written .shader files."), _opt.shaderOutput);
            _opt.pipeline = (TargetPipeline)EditorGUILayout.EnumPopup("Render Pipeline", _opt.pipeline);
            if (_opt.pipeline != ExportOptions.DetectPipeline())
                EditorGUILayout.HelpBox($"The project appears to use {ExportOptions.DetectPipeline()}.", MessageType.Info);
            if (_opt.shaderOutput == ShaderOutput.ShaderGraph && _opt.pipeline == TargetPipeline.BuiltIn)
                EditorGUILayout.HelpBox("Shader Graph for Built-in requires Shader Graph 12+ (Unity 2021.2+).", MessageType.None);

            _showMaterials = EditorGUILayout.Foldout(_showMaterials, "Material Options", true);
            if (_showMaterials)
            {
                EditorGUI.indentLevel++;
                _opt.bakeUnsupported = EditorGUILayout.Toggle(new GUIContent("Bake Unsupported Nodes",
                    "Procedural textures and other nodes without a Unity equivalent are baked to textures with Cycles."), _opt.bakeUnsupported);
                using (new EditorGUI.DisabledScope(!_opt.bakeUnsupported))
                {
                    _opt.bakeSize = EditorGUILayout.IntPopup("Bake Resolution", _opt.bakeSize,
                        new[] { "256", "512", "1024", "2048", "4096" }, new[] { 256, 512, 1024, 2048, 4096 });
                    _opt.bakeSamples = EditorGUILayout.IntSlider("Bake Samples", _opt.bakeSamples, 1, 256);
                }
                _opt.allMaterials = EditorGUILayout.Toggle(new GUIContent("Include Unused Materials",
                    "Also convert materials not assigned to any object."), _opt.allMaterials);
                _opt.onlyMaterials = EditorGUILayout.TextField(new GUIContent("Only These Materials",
                    "Comma separated Blender material names. Leave empty to convert all. " +
                    "Untick 'Export FBX' to import just the materials."), _opt.onlyMaterials);
                if (!string.IsNullOrWhiteSpace(_opt.onlyMaterials) && _opt.exportFbx)
                    EditorGUILayout.HelpBox("The FBX still contains all objects. Untick 'Export FBX' for a materials-only import.", MessageType.None);
                _opt.remapFbxMaterials = EditorGUILayout.Toggle("Assign To FBX", _opt.remapFbxMaterials);
                _opt.overwriteMaterials = EditorGUILayout.Toggle("Overwrite Existing", _opt.overwriteMaterials);
                EditorGUI.indentLevel--;
            }

            _showFbx = EditorGUILayout.Foldout(_showFbx, "FBX Options", true);
            if (_showFbx)
            {
                EditorGUI.indentLevel++;
                _opt.exportFbx = EditorGUILayout.Toggle("Export FBX", _opt.exportFbx);
                using (new EditorGUI.DisabledScope(!_opt.exportFbx))
                {
                    _opt.applyModifiers = EditorGUILayout.Toggle("Apply Modifiers", _opt.applyModifiers);
                    _opt.includeArmatures = EditorGUILayout.Toggle("Armatures", _opt.includeArmatures);
                    _opt.includeEmpties = EditorGUILayout.Toggle("Empties", _opt.includeEmpties);
                    _opt.includeLights = EditorGUILayout.Toggle("Lights", _opt.includeLights);
                    _opt.includeCameras = EditorGUILayout.Toggle("Cameras", _opt.includeCameras);
                    _opt.bakeAxisConversion = EditorGUILayout.Toggle(new GUIContent("Bake Axis Conversion (Unity)",
                        "Removes the -90° X rotation on imported objects."), _opt.bakeAxisConversion);
                }
                EditorGUI.indentLevel--;
            }

            _showTransform = EditorGUILayout.Foldout(_showTransform, "Transform", true);
            if (_showTransform)
            {
                EditorGUI.indentLevel++;
                using (new EditorGUI.DisabledScope(!_opt.exportFbx))
                {
                    _opt.scale = EditorGUILayout.Slider(new GUIContent("Scale",
                        "Scale all data (some importers do not support scaled armatures)."), _opt.scale, 0.001f, 1000f);
                    var si = Mathf.Max(0, Array.IndexOf(ExportOptions.ScaleOptionIds, _opt.applyScaleOptions));
                    si = EditorGUILayout.Popup(new GUIContent("Apply Scalings",
                        "How custom and unit scaling are applied in the FBX file."), si, ExportOptions.ScaleOptionLabels);
                    _opt.applyScaleOptions = ExportOptions.ScaleOptionIds[si];
                    _opt.axisForward = AxisPopup("Forward", _opt.axisForward);
                    _opt.axisUp = AxisPopup("Up", _opt.axisUp);
                    if (!_opt.AxesValid)
                        EditorGUILayout.HelpBox("Forward and Up must use different axes.", MessageType.Error);
                    _opt.applyUnitScale = EditorGUILayout.Toggle(new GUIContent("Apply Unit",
                        "Take Blender's unit settings into account."), _opt.applyUnitScale);
                    _opt.useSpaceTransform = EditorGUILayout.Toggle(new GUIContent("Use Space Transform",
                        "Apply the global space transform to object rotations."), _opt.useSpaceTransform);
                    _opt.applyTransform = EditorGUILayout.Toggle(new GUIContent("Apply Transform",
                        "Experimental: bake the space transform into object data. Known to break armatures/animations."), _opt.applyTransform);
                }
                EditorGUI.indentLevel--;
            }

            _showAnim = EditorGUILayout.Foldout(_showAnim, "Animation", true);
            if (_showAnim)
            {
                EditorGUI.indentLevel++;
                using (new EditorGUI.DisabledScope(!_opt.exportFbx))
                {
                    _opt.bakeAnimation = EditorGUILayout.Toggle(new GUIContent("Bake Animation",
                        "Export baked keyframe animation (Blender FBX exporter option)."), _opt.bakeAnimation);
                    using (new EditorGUI.DisabledScope(!_opt.bakeAnimation))
                    {
                        _opt.animKeyAllBones = EditorGUILayout.Toggle(new GUIContent("Key All Bones",
                            "Force at least one key of animation for all bones."), _opt.animKeyAllBones);
                        _opt.animNlaStrips = EditorGUILayout.Toggle(new GUIContent("NLA Strips",
                            "Export each non-muted NLA strip as a separate animation clip."), _opt.animNlaStrips);
                        _opt.animAllActions = EditorGUILayout.Toggle(new GUIContent("All Actions",
                            "Export each action as a separate animation clip."), _opt.animAllActions);
                        _opt.animForceStartEndKeying = EditorGUILayout.Toggle(new GUIContent("Force Start/End Keying",
                            "Always add a keyframe at the start and end of actions for animated channels."), _opt.animForceStartEndKeying);
                        _opt.animSamplingRate = EditorGUILayout.Slider(new GUIContent("Sampling Rate",
                            "How often to evaluate animated values (in frames)."), _opt.animSamplingRate, 0.01f, 100f);
                        _opt.animSimplify = EditorGUILayout.Slider(new GUIContent("Simplify",
                            "How much to simplify baked values (0 disables)."), _opt.animSimplify, 0f, 100f);
                    }
                }
                EditorGUI.indentLevel--;
            }

            EditorGUILayout.Space();
            using (new EditorGUI.DisabledScope(!B2USettings.BlenderPathValid || !File.Exists(_opt.blendPath ?? "") ||
                                               (_opt.exportFbx && !_opt.AxesValid)))
            {
                if (GUILayout.Button("Convert", GUILayout.Height(32)))
                {
                    _last = BlendImportPipeline.Run(_opt);
                    if (_last.Success && !string.IsNullOrEmpty(_last.FbxAssetPath))
                        EditorGUIUtility.PingObject(AssetDatabase.LoadMainAssetAtPath(_last.FbxAssetPath));
                    GUIUtility.ExitGUI();
                }
            }

            DrawReport();
            EditorGUILayout.EndScrollView();
        }

        static string AxisPopup(string label, string value)
        {
            var i = Mathf.Max(0, Array.IndexOf(ExportOptions.Axes, value));
            i = EditorGUILayout.Popup(label, i, ExportOptions.Axes);
            return ExportOptions.Axes[i];
        }

        void DrawReport()
        {
            if (_last == null) return;
            EditorGUILayout.Space();
            EditorGUILayout.LabelField("Last Conversion", EditorStyles.boldLabel);
            if (_last.Success)
                EditorGUILayout.HelpBox($"Converted {_last.Materials.Count} material(s) into {_last.OutputFolder} " +
                                        $"with {_last.Warnings.Count} warning(s).", MessageType.Info);
            else
                EditorGUILayout.HelpBox("Conversion failed: " + _last.Error, MessageType.Error);

            if (_last.Warnings.Count > 0)
            {
                _warnScroll = EditorGUILayout.BeginScrollView(_warnScroll, GUILayout.MaxHeight(180));
                foreach (var w in _last.Warnings)
                    EditorGUILayout.LabelField("• " + w, EditorStyles.wordWrappedMiniLabel);
                EditorGUILayout.EndScrollView();
            }

            using (new EditorGUILayout.HorizontalScope())
            {
                if (!string.IsNullOrEmpty(_last.OutputFolder) && GUILayout.Button("Show Output"))
                    EditorGUIUtility.PingObject(AssetDatabase.LoadMainAssetAtPath(_last.OutputFolder));
                if (!string.IsNullOrEmpty(_last.BlenderLog) && GUILayout.Button(_showLog ? "Hide Blender Log" : "Show Blender Log"))
                    _showLog = !_showLog;
            }
            if (_showLog && !string.IsNullOrEmpty(_last.BlenderLog))
                EditorGUILayout.TextArea(_last.BlenderLog, GUILayout.MaxHeight(300));
        }

        void HandleDragAndDrop(Rect rect)
        {
            var e = Event.current;
            if (!rect.Contains(e.mousePosition)) return;
            if (e.type != EventType.DragUpdated && e.type != EventType.DragPerform) return;
            var path = DragAndDrop.paths.FirstOrDefault(p => p.EndsWith(".blend", System.StringComparison.OrdinalIgnoreCase));
            if (path == null) return;
            DragAndDrop.visualMode = DragAndDropVisualMode.Copy;
            if (e.type == EventType.DragPerform)
            {
                DragAndDrop.AcceptDrag();
                _opt.blendPath = Path.GetFullPath(path);
            }
            e.Use();
        }

        static string DirOf(string p)
        {
            try { return string.IsNullOrEmpty(p) ? "" : Path.GetDirectoryName(p); }
            catch { return ""; }
        }
    }
}
