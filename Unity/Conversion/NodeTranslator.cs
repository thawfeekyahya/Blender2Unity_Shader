using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace B2U
{
    /// <summary>
    /// Translates one flattened Blender material graph (from materials.json) into an <see cref="IRGraph"/>.
    /// Value nodes produce IR outputs keyed by Blender output socket identifier. Shader (closure)
    /// nodes are expanded into six "surface" channels (S_Base, S_Metal, S_Rough, S_Normal, S_Emit,
    /// S_Alpha) so that Mix/Add Shader become plain arithmetic in any backend.
    /// </summary>
    public partial class NodeTranslator
    {
        /// <summary>Blender node types handled natively. Anything else is baked by Blender (if enabled).</summary>
        public static readonly HashSet<string> SupportedTypes = new HashSet<string>
        {
            "ShaderNodeOutputMaterial",
            // Shaders
            "ShaderNodeBsdfPrincipled", "ShaderNodeBsdfDiffuse", "ShaderNodeEmission", "ShaderNodeMixShader",
            "ShaderNodeAddShader", "ShaderNodeBsdfTransparent", "ShaderNodeBsdfGlossy", "ShaderNodeBsdfAnisotropic",
            "ShaderNodeBsdfGlass", "ShaderNodeBsdfRefraction", "ShaderNodeHoldout", "ShaderNodeBsdfTranslucent",
            "ShaderNodeSubsurfaceScattering", "ShaderNodeBsdfToon", "ShaderNodeBsdfSheen", "ShaderNodeBsdfVelvet",
            "ShaderNodeEeveeSpecular",
            // Inputs
            "ShaderNodeTexCoord", "ShaderNodeUVMap", "ShaderNodeRGB", "ShaderNodeValue", "ShaderNodeFresnel",
            "ShaderNodeLayerWeight", "ShaderNodeNewGeometry",
            // Textures
            "ShaderNodeTexImage", "ShaderNodeTexChecker", "ShaderNodeTexGradient",
            // Color
            "ShaderNodeMix", "ShaderNodeMixRGB", "ShaderNodeInvert", "ShaderNodeHueSaturation",
            "ShaderNodeBrightContrast", "ShaderNodeGamma", "ShaderNodeRGBCurve",
            // Vector
            "ShaderNodeMapping", "ShaderNodeNormalMap", "ShaderNodeBump", "ShaderNodeVectorCurve",
            "ShaderNodeVectorMath",
            // Converter
            "ShaderNodeMath", "ShaderNodeValToRGB", "ShaderNodeRGBToBW", "ShaderNodeSeparateColor",
            "ShaderNodeCombineColor", "ShaderNodeSeparateRGB", "ShaderNodeCombineRGB", "ShaderNodeSeparateHSV",
            "ShaderNodeCombineHSV", "ShaderNodeSeparateXYZ", "ShaderNodeCombineXYZ", "ShaderNodeClamp",
            "ShaderNodeMapRange", "ShaderNodeFloatCurve",
        };

        static readonly (string name, PortType type)[] SurfacePorts =
        {
            ("Base", PortType.Float3), ("Metal", PortType.Float), ("Rough", PortType.Float),
            ("Normal", PortType.Float3), ("Emit", PortType.Float3), ("Alpha", PortType.Float),
        };

        readonly BlendMaterial _mat;
        readonly BlendExportData _data;
        readonly IRGraph _g = new IRGraph();
        readonly Dictionary<string, Dictionary<string, IRRef>> _values = new Dictionary<string, Dictionary<string, IRRef>>();
        readonly Dictionary<string, IRNode> _surfaces = new Dictionary<string, IRNode>();
        readonly Dictionary<string, IRRef> _conversions = new Dictionary<string, IRRef>();
        readonly Dictionary<string, IRTexture> _textures = new Dictionary<string, IRTexture>();
        readonly HashSet<string> _inProgress = new HashSet<string>();
        bool _alphaUsed;

        NodeTranslator(BlendMaterial mat, BlendExportData data)
        {
            _mat = mat;
            _data = data;
            _g.MaterialName = mat.name;
        }

        public static IRGraph Translate(BlendMaterial mat, BlendExportData data)
        {
            var t = new NodeTranslator(mat, data);
            t.Run();
            return t._g;
        }

        void Warn(string msg) => _g.Warnings.Add(msg);

        // ------------------------------------------------------------------------------
        // Top level
        // ------------------------------------------------------------------------------

        void Run()
        {
            IRNode surface = null;
            if (!_mat.useNodes)
            {
                surface = _g.AddFunction("Viewport material", SurfaceAssign(
                    "BaseColor", "0", "Roughness", "Normal", "0", "1"));
                AddConst(surface, "BaseColor", PortType.Float3, _mat.diffuseColor);
                AddConst(surface, "Roughness", PortType.Float, new[] { _mat.roughness });
                surface.Body = surface.Body.Replace("S_Metal = 0;", "S_Metal = " + Hlsl.F(_mat.metallic) + ";");
                surface.Inputs.Add(new IRInput { Name = "Normal", Type = PortType.Float3, Implicit = ImplicitInput.NormalWS });
                AddSurfaceOutputs(surface);
            }
            else
            {
                var output = _mat.Node(_mat.outputNode);
                if (output == null)
                    Warn("No Material Output node found; using a default surface.");
                else
                {
                    surface = SurfaceFromInput(output, "Surface");
                    var disp = output.Input("Displacement");
                    if (disp != null && disp.linked)
                        Warn("Material Output 'Displacement' is not converted (use a Bump node to affect normals).");
                }
            }

            var final = _g.AddFunction("Surface Output",
                "BaseColor = max(S_Base, 0.0);\n" +
                "Metallic = saturate(S_Metal);\n" +
                "Smoothness = 1.0 - saturate(S_Rough);\n" +
                "NormalWS = normalize(S_Normal);\n" +
                "Emission = max(S_Emit, 0.0);\n" +
                "Alpha = saturate(S_Alpha);");
            BindSurfaceInputs(final, "S_", surface);
            final.Outputs.Add(new IROutput { Name = "BaseColor", Type = PortType.Float3 });
            final.Outputs.Add(new IROutput { Name = "Metallic", Type = PortType.Float });
            final.Outputs.Add(new IROutput { Name = "Smoothness", Type = PortType.Float });
            final.Outputs.Add(new IROutput { Name = "NormalWS", Type = PortType.Float3 });
            final.Outputs.Add(new IROutput { Name = "Emission", Type = PortType.Float3 });
            final.Outputs.Add(new IROutput { Name = "Alpha", Type = PortType.Float });
            _g.Surface = final;

            _g.Transparent = _mat.IsTransparent;
            _g.AlphaClip = _mat.IsAlphaClip || (!_g.Transparent && _alphaUsed && _mat.renderMethod == "DITHERED");
            _g.AlphaThreshold = _mat.blendMethod == "HASHED" || _mat.renderMethod == "DITHERED" ? 0.5f : _mat.alphaThreshold;
            _g.DoubleSided = !_mat.backfaceCulling;
            if (_alphaUsed && !_g.Transparent && !_g.AlphaClip)
                Warn("Material uses alpha but is opaque in Blender; alpha is ignored.");
            foreach (var w in _mat.warnings) Warn(w);
        }

        // ------------------------------------------------------------------------------
        // Value resolution
        // ------------------------------------------------------------------------------

        static PortType PortTypeOf(BlendSocket s) => s != null && s.IsScalar ? PortType.Float : PortType.Float3;

        /// <summary>IR outputs of a Blender value node, keyed by output socket identifier.</summary>
        Dictionary<string, IRRef> ValuesOf(BlendNode b)
        {
            if (_values.TryGetValue(b.id, out var cached)) return cached;
            if (!_inProgress.Add(b.id))
            {
                Warn($"Cycle detected at node '{b.label}'.");
                return new Dictionary<string, IRRef>();
            }
            Dictionary<string, IRRef> result;
            try
            {
                result = TranslateValueNode(b) ?? Unsupported(b);
            }
            catch (Exception e)
            {
                Warn($"Failed to translate '{b.label}' ({b.type}): {e.Message}");
                result = Unsupported(b, quiet: true);
            }
            _inProgress.Remove(b.id);
            _values[b.id] = result;
            return result;
        }

        /// <summary>Resolve what feeds an input socket; returns null for unlinked sockets.</summary>
        IRRef Resolve(BlendNode b, BlendSocket s, PortType target, out float[] constant)
        {
            constant = null;
            if (s == null || !s.linked) return null;
            var link = _mat.LinkInto(b.id, s.identifier);
            if (link == null) return null;
            var src = _mat.Node(link.fromNode);
            if (src == null) return null;
            var srcSock = src.Output(link.fromSocket);

            if (src.type == "B2U_Constant")
            {
                constant = ConvertConstant(srcSock, target);
                return null;
            }
            if (srcSock != null && srcSock.IsShader)
            {
                Warn($"Shader output of '{src.label}' plugged into value input '{s.name}' of '{b.label}'; ignored.");
                constant = new float[4];
                return null;
            }

            var values = ValuesOf(src);
            if (!values.TryGetValue(link.fromSocket, out var r))
            {
                Warn($"Output '{link.fromSocket}' of '{src.label}' ({src.type}) is not available.");
                constant = new float[4];
                return null;
            }
            return Convert(r, srcSock, target);
        }

        float[] ConvertConstant(BlendSocket srcSock, PortType target)
        {
            var v = srcSock?.value ?? new float[4];
            var scalar = srcSock != null && srcSock.IsScalar;
            if (target == PortType.Float && !scalar)
                return new[] { srcSock.type == "RGBA" ? 0.2126f * v[0] + 0.7152f * v[1] + 0.0722f * v[2] : (v[0] + v[1] + v[2]) / 3f };
            if (target != PortType.Float && scalar)
                return new[] { v[0], v[0], v[0], 1f };
            return v;
        }

        IRRef Convert(IRRef r, BlendSocket srcSock, PortType target)
        {
            if (r.Type == target || target != PortType.Float) return r; // scalar->vector broadcasts natively
            var key = r + "->" + target;
            if (_conversions.TryGetValue(key, out var conv)) return conv;
            var isColor = srcSock != null && srcSock.type == "RGBA";
            var n = _g.AddFunction(isColor ? "Color to Float" : "Vector to Float",
                isColor ? "Out = dot(In.rgb, float3(0.2126, 0.7152, 0.0722));" : "Out = dot(In.xyz, 1.0 / 3.0);");
            n.Inputs.Add(new IRInput { Name = "In", Type = r.Type, Link = r });
            n.Outputs.Add(new IROutput { Name = "Out", Type = PortType.Float });
            conv = n["Out"];
            _conversions[key] = conv;
            return conv;
        }

        /// <summary>
        /// Adds an input to <paramref name="n"/> bound to Blender socket <paramref name="keys"/> of node <paramref name="b"/>.
        /// Unlinked sockets become constants, or the implicit value if one is given.
        /// </summary>
        IRInput Bind(IRNode n, string name, PortType type, BlendNode b, string[] keys,
                     ImplicitInput implicitValue = ImplicitInput.None, float[] fallback = null)
        {
            var s = b.Input(keys);
            var input = new IRInput { Name = name, Type = type };
            var link = Resolve(b, s, type, out var constant);
            if (link != null) input.Link = link;
            else if (constant != null) input.Constant = Pad(constant);
            else if (implicitValue != ImplicitInput.None) input.Implicit = implicitValue;
            else if (s != null) input.Constant = Pad(type == PortType.Float || !s.IsScalar ? s.value : new[] { s.V(0), s.V(0), s.V(0), 1f });
            else input.Constant = Pad(fallback);
            n.Inputs.Add(input);
            return input;
        }

        IRInput Bind(IRNode n, string name, PortType type, BlendNode b, string key,
                     ImplicitInput implicitValue = ImplicitInput.None, float[] fallback = null)
            => Bind(n, name, type, b, new[] { key }, implicitValue, fallback);

        static float[] Pad(float[] v)
        {
            var r = new float[4];
            if (v != null) Array.Copy(v, r, Math.Min(4, v.Length));
            return r;
        }

        static void AddConst(IRNode n, string name, PortType type, float[] value) =>
            n.Inputs.Add(new IRInput { Name = name, Type = type, Constant = Pad(value) });

        static void AddImplicit(IRNode n, string name, PortType type, ImplicitInput imp) =>
            n.Inputs.Add(new IRInput { Name = name, Type = type, Implicit = imp });

        static Dictionary<string, IRRef> Map(IRNode n, params (string blenderOut, string port)[] pairs)
        {
            var d = new Dictionary<string, IRRef>();
            foreach (var p in pairs) d[p.blenderOut] = n[p.port];
            return d;
        }

        Dictionary<string, IRRef> Unsupported(BlendNode b, bool quiet = false)
        {
            if (!quiet)
                Warn($"Node '{b.label}' ({b.type}) is not supported; its outputs are replaced with defaults. " +
                     "Enable 'Bake unsupported nodes' to bake it to a texture.");
            var body = new StringBuilder();
            var n = _g.AddFunction("Unsupported " + b.type, "");
            var d = new Dictionary<string, IRRef>();
            int k = 0;
            foreach (var o in b.outputs)
            {
                if (o.IsShader) continue;
                var port = "Out" + k++;
                var t = PortTypeOf(o);
                n.Outputs.Add(new IROutput { Name = port, Type = t });
                body.Append(port).Append(" = ").Append(Hlsl.Literal(t, o.value)).Append(";\n");
                d[o.identifier] = n[port];
            }
            if (k == 0)
            {
                n.Outputs.Add(new IROutput { Name = "Out0", Type = PortType.Float });
                body.Append("Out0 = 0.0;");
            }
            n.Body = body.ToString().TrimEnd();
            return d;
        }

        // ------------------------------------------------------------------------------
        // Surfaces (closures)
        // ------------------------------------------------------------------------------

        static void AddSurfaceOutputs(IRNode n)
        {
            foreach (var p in SurfacePorts) n.Outputs.Add(new IROutput { Name = "S_" + p.name, Type = p.type });
        }

        /// <summary>Bind six surface inputs named prefix+channel to the surface node <paramref name="src"/>.</summary>
        static void BindSurfaceInputs(IRNode n, string prefix, IRNode src)
        {
            foreach (var p in SurfacePorts)
            {
                var input = new IRInput { Name = prefix + p.name, Type = p.type };
                if (src != null) input.Link = src["S_" + p.name];
                else if (p.name == "Normal") input.Implicit = ImplicitInput.NormalWS;
                else if (p.name == "Alpha") input.Constant = new[] { 1f, 0, 0, 0 };
                else if (p.name == "Rough") input.Constant = new[] { 0.5f, 0, 0, 0 };
                n.Inputs.Add(input);
            }
        }

        static string SurfaceAssign(string baseColor, string metal, string rough, string normal, string emit, string alpha) =>
            $"S_Base = {baseColor};\nS_Metal = {metal};\nS_Rough = {rough};\nS_Normal = {normal};\nS_Emit = {emit};\nS_Alpha = {alpha};";

        IRNode SurfaceFromInput(BlendNode b, string socketKey)
        {
            var s = b.Input(socketKey);
            if (s == null || !s.linked) return null;
            var link = _mat.LinkInto(b.id, s.identifier);
            var src = link != null ? _mat.Node(link.fromNode) : null;
            if (src == null) return null;
            var srcSock = src.Output(link.fromSocket);
            if (srcSock != null && !srcSock.IsShader)
            {
                // Blender treats a color plugged into a shader socket as emission.
                var n = _g.AddFunction("Color as Emission", SurfaceAssign("0.0", "0.0", "1.0", "N", "Color", "1.0"));
                Bind(n, "Color", PortType.Float3, b, s.identifier);
                AddImplicit(n, "N", PortType.Float3, ImplicitInput.NormalWS);
                AddSurfaceOutputs(n);
                return n;
            }
            return SurfaceOf(src);
        }

        IRNode SurfaceOf(BlendNode b)
        {
            if (_surfaces.TryGetValue(b.id, out var cached)) return cached;
            if (!_inProgress.Add(b.id))
            {
                Warn($"Cycle detected at shader '{b.label}'.");
                return null;
            }
            IRNode n;
            try { n = TranslateShaderNode(b); }
            catch (Exception e)
            {
                Warn($"Failed to translate shader '{b.label}': {e.Message}");
                n = null;
            }
            _inProgress.Remove(b.id);
            _surfaces[b.id] = n;
            return n;
        }

        IRNode TranslateShaderNode(BlendNode b)
        {
            IRNode n;
            switch (b.type)
            {
                case "ShaderNodeBsdfPrincipled":
                {
                    n = _g.AddFunction("Principled BSDF", SurfaceAssign(
                        "BaseColor", "Metallic", "Roughness", "Normal", "EmissionColor * EmissionStrength", "Alpha"));
                    Bind(n, "BaseColor", PortType.Float3, b, "Base Color");
                    Bind(n, "Metallic", PortType.Float, b, "Metallic");
                    Bind(n, "Roughness", PortType.Float, b, "Roughness");
                    Bind(n, "Normal", PortType.Float3, b, "Normal", ImplicitInput.NormalWS);
                    Bind(n, "EmissionColor", PortType.Float3, b, new[] { "Emission Color", "Emission" });
                    Bind(n, "EmissionStrength", PortType.Float, b, new[] { "Emission Strength" }, fallback: new[] { 1f });
                    var alpha = Bind(n, "Alpha", PortType.Float, b, "Alpha");
                    if (!alpha.IsConstant || alpha.Constant[0] < 0.999f) _alphaUsed = true;
                    var trans = b.Input("Transmission Weight", "Transmission");
                    if (trans != null && (trans.linked || trans.V(0) > 0.001f))
                        Warn($"'{b.label}': transmission is not supported by Unity's Lit model; ignored.");
                    foreach (var key in new[] { "Coat Weight", "Clearcoat", "Sheen Weight", "Sheen", "Subsurface Weight", "Subsurface" })
                    {
                        var sock = b.Input(key);
                        if (sock != null && (sock.linked || sock.V(0) > 0.001f))
                            Warn($"'{b.label}': '{sock.name}' is not converted.");
                    }
                    break;
                }
                case "ShaderNodeBsdfDiffuse":
                case "ShaderNodeBsdfTranslucent":
                case "ShaderNodeSubsurfaceScattering":
                case "ShaderNodeBsdfToon":
                case "ShaderNodeBsdfSheen":
                case "ShaderNodeBsdfVelvet":
                    if (b.type != "ShaderNodeBsdfDiffuse")
                        Warn($"'{b.label}' ({b.type}) approximated as a diffuse surface.");
                    n = _g.AddFunction(b.label, SurfaceAssign("Color", "0.0", "1.0", "Normal", "0.0", "1.0"));
                    Bind(n, "Color", PortType.Float3, b, new[] { "Color", "Base Color" }, fallback: new[] { 0.8f, 0.8f, 0.8f });
                    Bind(n, "Normal", PortType.Float3, b, "Normal", ImplicitInput.NormalWS);
                    break;
                case "ShaderNodeEeveeSpecular":
                    n = _g.AddFunction("Specular BSDF", SurfaceAssign("BaseColor", "0.0", "Roughness", "Normal", "Emissive", "1.0 - Transparency"));
                    Bind(n, "BaseColor", PortType.Float3, b, "Base Color");
                    Bind(n, "Roughness", PortType.Float, b, "Roughness");
                    Bind(n, "Normal", PortType.Float3, b, "Normal", ImplicitInput.NormalWS);
                    Bind(n, "Emissive", PortType.Float3, b, new[] { "Emissive Color" });
                    Bind(n, "Transparency", PortType.Float, b, "Transparency");
                    break;
                case "ShaderNodeEmission":
                    n = _g.AddFunction("Emission", SurfaceAssign("0.0", "0.0", "1.0", "Normal", "Color * Strength", "1.0"));
                    Bind(n, "Color", PortType.Float3, b, "Color");
                    Bind(n, "Strength", PortType.Float, b, "Strength");
                    AddImplicit(n, "Normal", PortType.Float3, ImplicitInput.NormalWS);
                    break;
                case "ShaderNodeBsdfGlossy":
                case "ShaderNodeBsdfAnisotropic":
                    n = _g.AddFunction("Glossy BSDF", SurfaceAssign("Color", "1.0", "Roughness", "Normal", "0.0", "1.0"));
                    Bind(n, "Color", PortType.Float3, b, "Color");
                    Bind(n, "Roughness", PortType.Float, b, "Roughness");
                    Bind(n, "Normal", PortType.Float3, b, "Normal", ImplicitInput.NormalWS);
                    break;
                case "ShaderNodeBsdfGlass":
                case "ShaderNodeBsdfRefraction":
                    Warn($"'{b.label}' approximated as a smooth semi-transparent surface (no refraction).");
                    _alphaUsed = true;
                    n = _g.AddFunction(b.label, SurfaceAssign("Color", "0.0", "Roughness", "Normal", "0.0", "0.25"));
                    Bind(n, "Color", PortType.Float3, b, "Color");
                    Bind(n, "Roughness", PortType.Float, b, "Roughness");
                    Bind(n, "Normal", PortType.Float3, b, "Normal", ImplicitInput.NormalWS);
                    break;
                case "ShaderNodeBsdfTransparent":
                case "ShaderNodeHoldout":
                    _alphaUsed = true;
                    n = _g.AddFunction(b.label, SurfaceAssign("0.0", "0.0", "1.0", "Normal", "0.0", "0.0"));
                    AddImplicit(n, "Normal", PortType.Float3, ImplicitInput.NormalWS);
                    break;
                case "ShaderNodeMixShader":
                {
                    var a = SurfaceFromInput(b, "Shader");
                    var c = SurfaceFromInput(b, "Shader_001");
                    n = _g.AddFunction("Mix Shader",
                        "float f = saturate(Fac);\n" +
                        "S_Base = lerp(A_Base, B_Base, f);\nS_Metal = lerp(A_Metal, B_Metal, f);\n" +
                        "S_Rough = lerp(A_Rough, B_Rough, f);\nS_Normal = normalize(lerp(A_Normal, B_Normal, f));\n" +
                        "S_Emit = lerp(A_Emit, B_Emit, f);\nS_Alpha = lerp(A_Alpha, B_Alpha, f);");
                    Bind(n, "Fac", PortType.Float, b, "Fac");
                    BindSurfaceInputs(n, "A_", a);
                    BindSurfaceInputs(n, "B_", c);
                    break;
                }
                case "ShaderNodeAddShader":
                {
                    var a = SurfaceFromInput(b, "Shader");
                    var c = SurfaceFromInput(b, "Shader_001");
                    n = _g.AddFunction("Add Shader",
                        "S_Base = A_Base + B_Base;\nS_Metal = max(A_Metal, B_Metal);\n" +
                        "S_Rough = min(A_Rough, B_Rough);\nS_Normal = normalize(A_Normal + B_Normal);\n" +
                        "S_Emit = A_Emit + B_Emit;\nS_Alpha = saturate(A_Alpha + B_Alpha);");
                    BindSurfaceInputs(n, "A_", a);
                    BindSurfaceInputs(n, "B_", c);
                    break;
                }
                default:
                {
                    Warn($"Shader node '{b.label}' ({b.type}) is not supported; approximated as diffuse.");
                    n = _g.AddFunction(b.label, SurfaceAssign("Color", "0.0", "1.0", "Normal", "0.0", "1.0"));
                    Bind(n, "Color", PortType.Float3, b, new[] { "Color", "Base Color" }, fallback: new[] { 0.8f, 0.8f, 0.8f });
                    AddImplicit(n, "Normal", PortType.Float3, ImplicitInput.NormalWS);
                    break;
                }
            }
            AddSurfaceOutputs(n);
            return n;
        }

        // ------------------------------------------------------------------------------
        // Textures
        // ------------------------------------------------------------------------------

        IRTexture TextureFor(BlendNode b)
        {
            var img = _data.FindImage(b.image);
            if (img == null || string.IsNullOrEmpty(img.file)) return null;
            var key = img.name;
            if (_textures.TryGetValue(key, out var tex)) return tex;
            tex = new IRTexture
            {
                PropertyName = "_Tex" + _textures.Count,
                DisplayName = img.name,
                ImageName = img.name,
                Srgb = img.srgb,
                Closest = b.Prop("interpolation") == "Closest",
                Clamp = b.Prop("extension") == "EXTEND" || b.Prop("extension") == "CLIP",
            };
            _textures[key] = tex;
            return tex;
        }
    }
}
