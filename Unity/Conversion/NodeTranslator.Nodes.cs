using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace B2U
{
    public partial class NodeTranslator
    {
        const PortType F1 = PortType.Float;
        const PortType F3 = PortType.Float3;

        static readonly Dictionary<string, int> BlendModes = new Dictionary<string, int>
        {
            { "MIX", 0 }, { "DARKEN", 1 }, { "MULTIPLY", 2 }, { "BURN", 3 }, { "LIGHTEN", 4 }, { "SCREEN", 5 },
            { "DODGE", 6 }, { "ADD", 7 }, { "OVERLAY", 8 }, { "SOFT_LIGHT", 9 }, { "LINEAR_LIGHT", 10 },
            { "DIFFERENCE", 11 }, { "EXCLUSION", 12 }, { "SUBTRACT", 13 }, { "DIVIDE", 14 }, { "HUE", 15 },
            { "SATURATION", 16 }, { "VALUE", 17 }, { "COLOR", 18 },
        };

        static readonly Dictionary<string, string> MathOps = new Dictionary<string, string>
        {
            { "ADD", "A + B" }, { "SUBTRACT", "A - B" }, { "MULTIPLY", "A * B" }, { "DIVIDE", "b2u_safe_div(A, B)" },
            { "MULTIPLY_ADD", "A * B + C" }, { "POWER", "b2u_safe_pow(A, B)" }, { "LOGARITHM", "b2u_safe_log(A, B)" },
            { "SQRT", "b2u_safe_sqrt(A)" }, { "INVERSE_SQRT", "(A > 0.0 ? rsqrt(A) : 0.0)" }, { "ABSOLUTE", "abs(A)" },
            { "EXPONENT", "exp(A)" }, { "MINIMUM", "min(A, B)" }, { "MAXIMUM", "max(A, B)" },
            { "LESS_THAN", "(A < B ? 1.0 : 0.0)" }, { "GREATER_THAN", "(A > B ? 1.0 : 0.0)" }, { "SIGN", "sign(A)" },
            { "COMPARE", "(abs(A - B) <= max(C, 1e-5) ? 1.0 : 0.0)" }, { "SMOOTH_MIN", "b2u_smoothmin(A, B, C)" },
            { "SMOOTH_MAX", "-b2u_smoothmin(-A, -B, C)" }, { "ROUND", "floor(A + 0.5)" }, { "FLOOR", "floor(A)" },
            { "CEIL", "ceil(A)" }, { "TRUNC", "trunc(A)" }, { "FRACT", "(A - floor(A))" },
            { "MODULO", "b2u_safe_mod(A, B)" }, { "FLOORED_MODULO", "b2u_floored_mod(A, B)" },
            { "WRAP", "b2u_wrap(A, B, C)" }, { "SNAP", "floor(b2u_safe_div(A, B)) * B" },
            { "PINGPONG", "b2u_pingpong(A, B)" }, { "SINE", "sin(A)" }, { "COSINE", "cos(A)" }, { "TANGENT", "tan(A)" },
            { "ARCSINE", "asin(clamp(A, -1.0, 1.0))" }, { "ARCCOSINE", "acos(clamp(A, -1.0, 1.0))" },
            { "ARCTANGENT", "atan(A)" }, { "ARCTAN2", "atan2(A, B)" }, { "SINH", "sinh(A)" }, { "COSH", "cosh(A)" },
            { "TANH", "tanh(A)" }, { "RADIANS", "A * (B2U_PI / 180.0)" }, { "DEGREES", "A * (180.0 / B2U_PI)" },
        };

        // Vector math: (vector expression, value expression). Null means the output is 0.
        static readonly Dictionary<string, (string vec, string val)> VectorOps = new Dictionary<string, (string, string)>
        {
            { "ADD", ("A + B", null) }, { "SUBTRACT", ("A - B", null) }, { "MULTIPLY", ("A * B", null) },
            { "DIVIDE", ("b2u_safe_div3(A, B)", null) }, { "MULTIPLY_ADD", ("A * B + C", null) },
            { "CROSS_PRODUCT", ("cross(A, B)", null) },
            { "PROJECT", ("(dot(B, B) != 0.0 ? B * (dot(A, B) / dot(B, B)) : float3(0.0, 0.0, 0.0))", null) },
            { "REFLECT", ("reflect(A, normalize(B))", null) },
            { "REFRACT", ("refract(A, normalize(B), Scale)", null) },
            { "FACEFORWARD", ("(dot(C, B) < 0.0 ? A : -A)", null) },
            { "DOT_PRODUCT", (null, "dot(A, B)") }, { "DISTANCE", (null, "distance(A, B)") },
            { "LENGTH", (null, "length(A)") }, { "SCALE", ("A * Scale", null) },
            { "NORMALIZE", ("(dot(A, A) > 0.0 ? normalize(A) : float3(0.0, 0.0, 0.0))", null) },
            { "ABSOLUTE", ("abs(A)", null) }, { "MINIMUM", ("min(A, B)", null) }, { "MAXIMUM", ("max(A, B)", null) },
            { "FLOOR", ("floor(A)", null) }, { "CEIL", ("ceil(A)", null) }, { "FRACTION", ("(A - floor(A))", null) },
            { "MODULO", ("float3(b2u_safe_mod(A.x, B.x), b2u_safe_mod(A.y, B.y), b2u_safe_mod(A.z, B.z))", null) },
            { "WRAP", ("float3(b2u_wrap(A.x, B.x, C.x), b2u_wrap(A.y, B.y, C.y), b2u_wrap(A.z, B.z, C.z))", null) },
            { "SNAP", ("floor(b2u_safe_div3(A, B)) * B", null) },
            { "SINE", ("sin(A)", null) }, { "COSINE", ("cos(A)", null) }, { "TANGENT", ("tan(A)", null) },
            { "POWER", ("float3(b2u_safe_pow(A.x, B.x), b2u_safe_pow(A.y, B.y), b2u_safe_pow(A.z, B.z))", null) },
            { "SIGN", ("sign(A)", null) },
        };

        Dictionary<string, IRRef> TranslateValueNode(BlendNode b)
        {
            switch (b.type)
            {
                case "ShaderNodeTexImage": return TexImage(b);
                case "ShaderNodeTexCoord": return TexCoord(b);
                case "ShaderNodeUVMap":
                {
                    var n = _g.AddFunction("UV Map", "UV = float3(UVIn.xy, 0.0);");
                    AddImplicit(n, "UVIn", F3, b.uvIndex == 1 ? ImplicitInput.UV1 : ImplicitInput.UV0);
                    if (b.uvIndex > 1) Warn($"'{b.label}': only the first two UV maps are supported; using UV1.");
                    n.Outputs.Add(new IROutput { Name = "UV", Type = F3 });
                    return Map(n, ("UV", "UV"));
                }
                case "ShaderNodeRGB":
                {
                    var n = _g.AddFunction("RGB", "Color = In;");
                    AddConst(n, "In", F3, b.outputs[0].value);
                    n.Outputs.Add(new IROutput { Name = "Color", Type = F3 });
                    return Map(n, (b.outputs[0].identifier, "Color"));
                }
                case "ShaderNodeValue":
                {
                    var n = _g.AddFunction("Value", "Value = In;");
                    AddConst(n, "In", F1, b.outputs[0].value);
                    n.Outputs.Add(new IROutput { Name = "Value", Type = F1 });
                    return Map(n, (b.outputs[0].identifier, "Value"));
                }
                case "ShaderNodeMath": return MathNode(b);
                case "ShaderNodeVectorMath": return VectorMathNode(b);
                case "ShaderNodeMix": return MixNode(b);
                case "ShaderNodeMixRGB":
                {
                    var mode = BlendModeOf(b);
                    var n = _g.AddFunction("Mix RGB (" + b.Prop("blend_type") + ")",
                        $"Color = b2u_blend({mode}, saturate(Fac), Color1, Color2);" +
                        (b.PropBool("use_clamp") ? "\nColor = saturate(Color);" : ""));
                    Bind(n, "Fac", F1, b, "Fac");
                    Bind(n, "Color1", F3, b, "Color1");
                    Bind(n, "Color2", F3, b, "Color2");
                    n.Outputs.Add(new IROutput { Name = "Color", Type = F3 });
                    return Map(n, ("Color", "Color"));
                }
                case "ShaderNodeInvert":
                    return Simple(b, "Invert", "Result = lerp(Color, 1.0 - Color, Fac);",
                        new[] { ("Fac", F1, "Fac"), ("Color", F3, "Color") }, ("Color", "Result", F3));
                case "ShaderNodeHueSaturation":
                    return Simple(b, "Hue/Saturation/Value",
                        "float3 hsv = b2u_rgb_to_hsv(Color);\n" +
                        "hsv.x = frac(hsv.x + Hue + 0.5);\n" +
                        "hsv.y = saturate(hsv.y * Saturation);\n" +
                        "hsv.z *= Val;\n" +
                        "Result = lerp(Color, max(b2u_hsv_to_rgb(hsv), 0.0), Fac);",
                        new[] { ("Hue", F1, "Hue"), ("Saturation", F1, "Saturation"), ("Val", F1, "Value"),
                                ("Fac", F1, "Fac"), ("Color", F3, "Color") }, ("Color", "Result", F3));
                case "ShaderNodeBrightContrast":
                    return Simple(b, "Bright/Contrast",
                        "float a = 1.0 + Contrast;\nfloat b = Bright - Contrast * 0.5;\nResult = max(a * Color + b, 0.0);",
                        new[] { ("Color", F3, "Color"), ("Bright", F1, "Bright"), ("Contrast", F1, "Contrast") },
                        ("Color", "Result", F3));
                case "ShaderNodeGamma":
                    return Simple(b, "Gamma",
                        "Result = float3(Color.r > 0.0 ? pow(Color.r, Gamma) : Color.r, Color.g > 0.0 ? pow(Color.g, Gamma) : Color.g, Color.b > 0.0 ? pow(Color.b, Gamma) : Color.b);",
                        new[] { ("Color", F3, "Color"), ("Gamma", F1, "Gamma") }, ("Color", "Result", F3));
                case "ShaderNodeRGBToBW":
                    return Simple(b, "RGB to BW", "Val = b2u_luminance(Color);",
                        new[] { ("Color", F3, "Color") }, ("Val", "Val", F1));
                case "ShaderNodeValToRGB": return ColorRamp(b);
                case "ShaderNodeRGBCurve":
                {
                    var n = _g.AddFunction("RGB Curves", Lut(b, 4) +
                        "float4 r, g, bl;\nB2U_LUT_SAMPLE(lut, Color.r, r);\nB2U_LUT_SAMPLE(lut, Color.g, g);\nB2U_LUT_SAMPLE(lut, Color.b, bl);\n" +
                        "Result = lerp(Color, float3(r.x, g.y, bl.z), Fac);");
                    Bind(n, "Fac", F1, b, "Fac");
                    Bind(n, "Color", F3, b, "Color");
                    n.Outputs.Add(new IROutput { Name = "Result", Type = F3 });
                    return Map(n, ("Color", "Result"));
                }
                case "ShaderNodeVectorCurve":
                {
                    var n = _g.AddFunction("Vector Curves", Lut(b, 4) +
                        "float3 t = Vec * 0.5 + 0.5;\nfloat4 x, y, z;\nB2U_LUT_SAMPLE(lut, t.x, x);\nB2U_LUT_SAMPLE(lut, t.y, y);\nB2U_LUT_SAMPLE(lut, t.z, z);\n" +
                        "Result = lerp(Vec, float3(x.x, y.y, z.z), Fac);");
                    Bind(n, "Fac", F1, b, "Fac");
                    Bind(n, "Vec", F3, b, "Vector");
                    n.Outputs.Add(new IROutput { Name = "Result", Type = F3 });
                    return Map(n, ("Vector", "Result"));
                }
                case "ShaderNodeFloatCurve":
                {
                    var n = _g.AddFunction("Float Curve", Lut(b, 4) +
                        "float4 c;\nB2U_LUT_SAMPLE(lut, Val, c);\nResult = lerp(Val, c.x, Factor);");
                    Bind(n, "Factor", F1, b, "Factor");
                    Bind(n, "Val", F1, b, "Value");
                    n.Outputs.Add(new IROutput { Name = "Result", Type = F1 });
                    return Map(n, ("Value", "Result"));
                }
                case "ShaderNodeSeparateColor":
                case "ShaderNodeSeparateRGB":
                case "ShaderNodeSeparateHSV":
                {
                    var mode = b.type == "ShaderNodeSeparateHSV" ? "HSV" : b.Prop("mode", "RGB");
                    var conv = mode == "HSV" ? "b2u_rgb_to_hsv(Color)" : mode == "HSL" ? "b2u_rgb_to_hsl(Color)" : "Color";
                    var n = _g.AddFunction("Separate " + mode, $"float3 c = {conv};\nX = c.x;\nY = c.y;\nZ = c.z;");
                    Bind(n, "Color", F3, b, new[] { "Color", "Image" });
                    n.Outputs.Add(new IROutput { Name = "X", Type = F1 });
                    n.Outputs.Add(new IROutput { Name = "Y", Type = F1 });
                    n.Outputs.Add(new IROutput { Name = "Z", Type = F1 });
                    var d = new Dictionary<string, IRRef>();
                    var ports = new[] { "X", "Y", "Z" };
                    for (int k = 0; k < b.outputs.Count && k < 3; k++) d[b.outputs[k].identifier] = n[ports[k]];
                    return d;
                }
                case "ShaderNodeCombineColor":
                case "ShaderNodeCombineRGB":
                case "ShaderNodeCombineHSV":
                {
                    var mode = b.type == "ShaderNodeCombineHSV" ? "HSV" : b.Prop("mode", "RGB");
                    var conv = mode == "HSV" ? "b2u_hsv_to_rgb(c)" : mode == "HSL" ? "b2u_hsl_to_rgb(c)" : "c";
                    var n = _g.AddFunction("Combine " + mode, $"float3 c = float3(X, Y, Z);\nColor = {conv};");
                    for (int k = 0; k < 3; k++)
                        Bind(n, new[] { "X", "Y", "Z" }[k], F1, b, b.inputs[k].identifier);
                    n.Outputs.Add(new IROutput { Name = "Color", Type = F3 });
                    return Map(n, (b.outputs[0].identifier, "Color"));
                }
                case "ShaderNodeSeparateXYZ":
                    return Simple3(b, "Separate XYZ", "X = Vec.x;\nY = Vec.y;\nZ = Vec.z;");
                case "ShaderNodeCombineXYZ":
                    return Simple(b, "Combine XYZ", "Vec = float3(X, Y, Z);",
                        new[] { ("X", F1, "X"), ("Y", F1, "Y"), ("Z", F1, "Z") }, ("Vector", "Vec", F3));
                case "ShaderNodeClamp":
                    return Simple(b, "Clamp",
                        b.Prop("clamp_type") == "RANGE"
                            ? "Result = (Min > Max) ? clamp(Val, Max, Min) : clamp(Val, Min, Max);"
                            : "Result = min(max(Val, Min), Max);",
                        new[] { ("Val", F1, "Value"), ("Min", F1, "Min"), ("Max", F1, "Max") }, ("Result", "Result", F1));
                case "ShaderNodeMapRange": return MapRange(b);
                case "ShaderNodeMapping": return Mapping(b);
                case "ShaderNodeNormalMap": return NormalMap(b);
                case "ShaderNodeBump": return Bump(b);
                case "ShaderNodeFresnel":
                {
                    var n = _g.AddFunction("Fresnel",
                        "float c = abs(dot(View, Normal));\n" +
                        "float eta = max(IOR, 0.00001);\n" +
                        "float g = eta * eta - 1.0 + c * c;\n" +
                        "if (g > 0.0) { g = sqrt(g); float A = (g - c) / (g + c); float B = (c * (g + c) - 1.0) / (c * (g - c) + 1.0); Fac = 0.5 * A * A * (1.0 + B * B); }\n" +
                        "else Fac = 1.0;");
                    Bind(n, "IOR", F1, b, "IOR");
                    Bind(n, "Normal", F3, b, "Normal", ImplicitInput.NormalWS);
                    AddImplicit(n, "View", F3, ImplicitInput.ViewDirWS);
                    n.Outputs.Add(new IROutput { Name = "Fac", Type = F1 });
                    return Map(n, ("Fac", "Fac"));
                }
                case "ShaderNodeLayerWeight":
                {
                    var n = _g.AddFunction("Layer Weight",
                        "float c = abs(dot(View, Normal));\n" +
                        "float bl = clamp(BlendF, 0.0, 0.99999);\n" +
                        "float eta = 1.0 / (1.0 - bl);\n" +
                        "float g = eta * eta - 1.0 + c * c;\n" +
                        "if (g > 0.0) { g = sqrt(g); float A = (g - c) / (g + c); float B = (c * (g + c) - 1.0) / (c * (g - c) + 1.0); Fresnel = 0.5 * A * A * (1.0 + B * B); }\n" +
                        "else Fresnel = 1.0;\n" +
                        "float facing = c;\n" +
                        "if (BlendF != 0.5) { float e = bl < 0.5 ? 2.0 * bl : 0.5 / (1.0 - bl); facing = pow(facing, e); }\n" +
                        "Facing = 1.0 - facing;");
                    Bind(n, "BlendF", F1, b, "Blend");
                    Bind(n, "Normal", F3, b, "Normal", ImplicitInput.NormalWS);
                    AddImplicit(n, "View", F3, ImplicitInput.ViewDirWS);
                    n.Outputs.Add(new IROutput { Name = "Fresnel", Type = F1 });
                    n.Outputs.Add(new IROutput { Name = "Facing", Type = F1 });
                    return Map(n, ("Fresnel", "Fresnel"), ("Facing", "Facing"));
                }
                case "ShaderNodeNewGeometry": return Geometry(b);
                case "ShaderNodeTexChecker":
                {
                    var n = _g.AddFunction("Checker Texture",
                        "float3 p = (Vec * Scale + 0.000001) * 0.999999;\n" +
                        "int xi = (int)abs(floor(p.x)); int yi = (int)abs(floor(p.y)); int zi = (int)abs(floor(p.z));\n" +
                        "bool c2 = ((xi % 2) == (yi % 2)) == ((zi % 2) != 0);\n" +
                        "Color = c2 ? Color2 : Color1;\nFac = c2 ? 0.0 : 1.0;");
                    Bind(n, "Vec", F3, b, "Vector", ImplicitInput.UV0);
                    Bind(n, "Color1", F3, b, "Color1");
                    Bind(n, "Color2", F3, b, "Color2");
                    Bind(n, "Scale", F1, b, "Scale");
                    n.Outputs.Add(new IROutput { Name = "Color", Type = F3 });
                    n.Outputs.Add(new IROutput { Name = "Fac", Type = F1 });
                    return Map(n, ("Color", "Color"), ("Fac", "Fac"));
                }
                case "ShaderNodeTexGradient":
                {
                    string expr;
                    switch (b.Prop("gradient_type", "LINEAR"))
                    {
                        case "QUADRATIC": expr = "float r = max(p.x, 0.0); f = r * r;"; break;
                        case "EASING": expr = "float r = saturate(p.x); float t = r * r; f = 3.0 * t - 2.0 * t * r;"; break;
                        case "DIAGONAL": expr = "f = (p.x + p.y) * 0.5;"; break;
                        case "RADIAL": expr = "f = atan2(p.y, p.x) / (2.0 * B2U_PI) + 0.5;"; break;
                        case "QUADRATIC_SPHERE": expr = "float r = max(0.999999 - length(p), 0.0); f = r * r;"; break;
                        case "SPHERICAL": expr = "f = max(0.999999 - length(p), 0.0);"; break;
                        default: expr = "f = p.x;"; break;
                    }
                    var n = _g.AddFunction("Gradient Texture", "float3 p = Vec;\nfloat f;\n" + expr + "\nFac = saturate(f);\nColor = Fac.xxx;");
                    Bind(n, "Vec", F3, b, "Vector", ImplicitInput.UV0);
                    n.Outputs.Add(new IROutput { Name = "Color", Type = F3 });
                    n.Outputs.Add(new IROutput { Name = "Fac", Type = F1 });
                    return Map(n, ("Color", "Color"), ("Fac", "Fac"));
                }
            }
            return null;
        }

        // ------------------------------------------------------------------------------

        int BlendModeOf(BlendNode b)
        {
            var name = b.Prop("blend_type", "MIX");
            if (BlendModes.TryGetValue(name, out var m)) return m;
            Warn($"'{b.label}': blend mode {name} not supported, using MIX.");
            return 0;
        }

        /// <summary>One-output node built from (port, type, blender socket) inputs.</summary>
        Dictionary<string, IRRef> Simple(BlendNode b, string title, string body,
            (string port, PortType type, string socket)[] inputs, (string blenderOut, string port, PortType type) output)
        {
            var n = _g.AddFunction(title, body);
            foreach (var i in inputs) Bind(n, i.port, i.type, b, i.socket);
            n.Outputs.Add(new IROutput { Name = output.port, Type = output.type });
            var outSock = b.Output(output.blenderOut);
            return Map(n, (outSock != null ? outSock.identifier : output.blenderOut, output.port));
        }

        Dictionary<string, IRRef> Simple3(BlendNode b, string title, string body)
        {
            var n = _g.AddFunction(title, body);
            Bind(n, "Vec", F3, b, "Vector");
            foreach (var p in new[] { "X", "Y", "Z" }) n.Outputs.Add(new IROutput { Name = p, Type = F1 });
            return Map(n, ("X", "X"), ("Y", "Y"), ("Z", "Z"));
        }

        string Lut(BlendNode b, int stride)
        {
            var lut = b.lut;
            const int size = 64;
            var sb = new StringBuilder();
            sb.Append("static const float4 lut[B2U_LUT_SIZE] = {");
            for (int i = 0; i < size; i++)
            {
                float V(int c) => lut != null && i * stride + c < lut.Length ? lut[i * stride + c] : i / (size - 1f);
                if (i % 4 == 0) sb.Append("\n    ");
                sb.Append($"float4({Hlsl.F(V(0))}, {Hlsl.F(V(1))}, {Hlsl.F(V(2))}, {Hlsl.F(V(3))})");
                if (i < size - 1) sb.Append(", ");
            }
            sb.Append("\n};\n");
            if (lut == null || lut.Length < size * stride)
                Warn($"'{b.label}': look-up table missing; using identity.");
            return sb.ToString();
        }

        Dictionary<string, IRRef> TexImage(BlendNode b)
        {
            var tex = TextureFor(b);
            if (tex == null)
            {
                Warn($"Image Texture '{b.label}' has no exported image; using magenta.");
                var m = _g.AddFunction("Missing Texture", "Color = float3(1.0, 0.0, 1.0);\nAlpha = 1.0;");
                m.Outputs.Add(new IROutput { Name = "Color", Type = F3 });
                m.Outputs.Add(new IROutput { Name = "Alpha", Type = F1 });
                return Map(m, ("Color", "Color"), ("Alpha", "Alpha"));
            }
            var projection = b.Prop("projection", "FLAT");
            if (projection != "FLAT") Warn($"'{b.label}': {projection} projection is not supported; using flat UV mapping.");
            if (b.Prop("extension") == "MIRROR") Warn($"'{b.label}': mirror extension mapped to repeat.");

            var n = _g.AddTexture(tex);
            n.Title = "Image " + tex.DisplayName;
            n.Inputs.Clear();
            Bind(n, "UV", F3, b, "Vector", b.uvIndex == 1 ? ImplicitInput.UV1 : ImplicitInput.UV0);
            return Map(n, ("Color", "Color"), ("Alpha", "Alpha"));
        }

        Dictionary<string, IRRef> TexCoord(BlendNode b)
        {
            bool Used(string s) { var o = b.Output(s); return o != null && o.linked; }
            var body = new StringBuilder();
            var n = _g.AddFunction("Texture Coordinate", "");
            AddImplicit(n, "UVIn", F3, ImplicitInput.UV0);
            body.Append("UV = float3(UVIn.xy, 0.0);\n");
            if (Used("Normal") || Used("Reflection")) AddImplicit(n, "NormalWS", F3, ImplicitInput.NormalWS);
            if (Used("Reflection")) AddImplicit(n, "ViewWS", F3, ImplicitInput.ViewDirWS);
            if (Used("Object") || Used("Generated")) AddImplicit(n, "PositionOS", F3, ImplicitInput.PositionOS);
            if (Used("Window") || Used("Camera")) Warn($"'{b.label}': Camera/Window coordinates are not supported.");
            if (Used("Generated")) Warn($"'{b.label}': Generated coordinates approximated from object space position.");
            body.Append(Used("Normal") ? "Normal = NormalWS;\n" : "Normal = float3(0.0, 0.0, 1.0);\n");
            body.Append(Used("Reflection") ? "Reflection = reflect(-ViewWS, NormalWS);\n" : "Reflection = float3(0.0, 0.0, 1.0);\n");
            body.Append(Used("Object") || Used("Generated") ? "Object = b2u_to_blender(PositionOS);\n" : "Object = float3(0.0, 0.0, 0.0);\n");
            body.Append("Generated = Object * 0.5 + 0.5;\nCamera = float3(0.0, 0.0, 0.0);\nWindow = float3(0.0, 0.0, 0.0);");
            n.Body = body.ToString();
            foreach (var o in new[] { "UV", "Normal", "Reflection", "Object", "Generated", "Camera", "Window" })
                n.Outputs.Add(new IROutput { Name = o, Type = F3 });
            return Map(n, ("UV", "UV"), ("Normal", "Normal"), ("Reflection", "Reflection"), ("Object", "Object"),
                ("Generated", "Generated"), ("Camera", "Camera"), ("Window", "Window"));
        }

        Dictionary<string, IRRef> Geometry(BlendNode b)
        {
            var n = _g.AddFunction("Geometry",
                "Position = b2u_to_blender(PositionWS);\nNormal = NormalWS;\nTangent = TangentWS;\nTrueNormal = NormalWS;\n" +
                "Incoming = View;\nParametric = float3(0.0, 0.0, 0.0);\nBackfacing = 0.0;\nPointiness = 0.5;\nRandomPerIsland = 0.0;");
            AddImplicit(n, "PositionWS", F3, ImplicitInput.PositionWS);
            AddImplicit(n, "NormalWS", F3, ImplicitInput.NormalWS);
            AddImplicit(n, "TangentWS", F3, ImplicitInput.TangentWS);
            AddImplicit(n, "View", F3, ImplicitInput.ViewDirWS);
            foreach (var o in new[] { "Position", "Normal", "Tangent", "TrueNormal", "Incoming", "Parametric" })
                n.Outputs.Add(new IROutput { Name = o, Type = F3 });
            foreach (var o in new[] { "Backfacing", "Pointiness", "RandomPerIsland" })
                n.Outputs.Add(new IROutput { Name = o, Type = F1 });
            foreach (var o in new[] { "Backfacing", "Pointiness", "Random Per Island", "Parametric" })
                if (b.Output(o)?.linked == true) Warn($"'{b.label}': '{o}' output is not supported (constant).");
            return Map(n, ("Position", "Position"), ("Normal", "Normal"), ("Tangent", "Tangent"),
                ("True Normal", "TrueNormal"), ("Incoming", "Incoming"), ("Parametric", "Parametric"),
                ("Backfacing", "Backfacing"), ("Pointiness", "Pointiness"), ("Random Per Island", "RandomPerIsland"));
        }

        Dictionary<string, IRRef> MathNode(BlendNode b)
        {
            var op = b.Prop("operation", "ADD");
            if (!MathOps.TryGetValue(op, out var expr))
            {
                Warn($"'{b.label}': math operation {op} not supported; using ADD.");
                expr = "A + B";
            }
            var body = "Result = " + expr + ";" + (b.PropBool("use_clamp") ? "\nResult = saturate(Result);" : "");
            var n = _g.AddFunction("Math " + op, body);
            Bind(n, "A", F1, b, "Value");
            Bind(n, "B", F1, b, "Value_001");
            Bind(n, "C", F1, b, "Value_002");
            n.Outputs.Add(new IROutput { Name = "Result", Type = F1 });
            return Map(n, (b.outputs[0].identifier, "Result"));
        }

        Dictionary<string, IRRef> VectorMathNode(BlendNode b)
        {
            var op = b.Prop("operation", "ADD");
            if (!VectorOps.TryGetValue(op, out var e))
            {
                Warn($"'{b.label}': vector math operation {op} not supported; using ADD.");
                e = ("A + B", null);
            }
            var n = _g.AddFunction("Vector Math " + op,
                "Vec = " + (e.vec ?? "float3(0.0, 0.0, 0.0)") + ";\nVal = " + (e.val ?? "0.0") + ";");
            Bind(n, "A", F3, b, "Vector");
            Bind(n, "B", F3, b, "Vector_001");
            Bind(n, "C", F3, b, "Vector_002");
            Bind(n, "Scale", F1, b, "Scale", fallback: new[] { 1f });
            n.Outputs.Add(new IROutput { Name = "Vec", Type = F3 });
            n.Outputs.Add(new IROutput { Name = "Val", Type = F1 });
            return Map(n, ("Vector", "Vec"), ("Value", "Val"));
        }

        Dictionary<string, IRRef> MixNode(BlendNode b)
        {
            var dataType = b.Prop("data_type", "RGBA");
            var clampF = b.PropBool("clamp_factor", true);
            var f = clampF ? "saturate(Fac)" : "Fac";
            IRNode n;
            switch (dataType)
            {
                case "FLOAT":
                    n = _g.AddFunction("Mix Float", $"Result = lerp(A, B, {f});");
                    Bind(n, "Fac", F1, b, "Factor_Float");
                    Bind(n, "A", F1, b, "A_Float");
                    Bind(n, "B", F1, b, "B_Float");
                    n.Outputs.Add(new IROutput { Name = "Result", Type = F1 });
                    return Map(n, ("Result_Float", "Result"));
                case "VECTOR":
                {
                    var nonUniform = b.Prop("factor_mode") == "NON_UNIFORM";
                    n = _g.AddFunction("Mix Vector", $"Result = lerp(A, B, {f});");
                    if (nonUniform) Bind(n, "Fac", F3, b, "Factor_Vector");
                    else Bind(n, "Fac", F1, b, "Factor_Float");
                    Bind(n, "A", F3, b, "A_Vector");
                    Bind(n, "B", F3, b, "B_Vector");
                    n.Outputs.Add(new IROutput { Name = "Result", Type = F3 });
                    return Map(n, ("Result_Vector", "Result"));
                }
                case "ROTATION":
                    return null;
                default:
                {
                    var mode = BlendModeOf(b);
                    n = _g.AddFunction("Mix Color (" + b.Prop("blend_type") + ")",
                        $"Result = b2u_blend({mode}, {f}, A, B);" + (b.PropBool("clamp_result") ? "\nResult = saturate(Result);" : ""));
                    Bind(n, "Fac", F1, b, "Factor_Float");
                    Bind(n, "A", F3, b, "A_Color");
                    Bind(n, "B", F3, b, "B_Color");
                    n.Outputs.Add(new IROutput { Name = "Result", Type = F3 });
                    return Map(n, ("Result_Color", "Result"));
                }
            }
        }

        Dictionary<string, IRRef> ColorRamp(BlendNode b)
        {
            var constant = b.Prop("rampInterpolation") == "CONSTANT";
            var sample = constant
                ? "float4 c = lut[(int)floor(saturate(Fac) * (B2U_LUT_SIZE - 1))];\n"
                : "float4 c;\nB2U_LUT_SAMPLE(lut, Fac, c);\n";
            var n = _g.AddFunction("Color Ramp", Lut(b, 4) + sample + "Color = c.rgb;\nAlpha = c.a;");
            Bind(n, "Fac", F1, b, "Fac");
            n.Outputs.Add(new IROutput { Name = "Color", Type = F3 });
            n.Outputs.Add(new IROutput { Name = "Alpha", Type = F1 });
            return Map(n, ("Color", "Color"), ("Alpha", "Alpha"));
        }

        Dictionary<string, IRRef> MapRange(BlendNode b)
        {
            var vector = b.Prop("data_type", "FLOAT") == "FLOAT_VECTOR";
            var t = vector ? F3 : F1;
            var tn = vector ? "float3" : "float";
            var div = vector ? "b2u_safe_div3" : "b2u_safe_div";
            var interp = b.Prop("interpolation_type", "LINEAR");
            var body = new StringBuilder();
            body.Append($"{tn} f = {div}(Val - FromMin, FromMax - FromMin);\n");
            switch (interp)
            {
                case "STEPPED":
                    body.Append($"f = {div}(floor(f * (Steps + 1.0)), Steps);\n");
                    break;
                case "SMOOTHSTEP":
                    body.Append($"{tn} x = saturate(f);\nf = x * x * (3.0 - 2.0 * x);\n");
                    break;
                case "SMOOTHERSTEP":
                    body.Append($"{tn} x = saturate(f);\nf = x * x * x * (x * (x * 6.0 - 15.0) + 10.0);\n");
                    break;
            }
            body.Append("Result = ToMin + f * (ToMax - ToMin);");
            if (b.PropBool("clamp", true) && (interp == "LINEAR" || interp == "STEPPED"))
                body.Append("\nResult = clamp(Result, min(ToMin, ToMax), max(ToMin, ToMax));");

            var n = _g.AddFunction("Map Range", body.ToString());
            var sfx = vector ? "_FLOAT3" : "";
            Bind(n, "Val", t, b, vector ? "Vector" : "Value");
            Bind(n, "FromMin", t, b, "From_Min" + sfx, fallback: new[] { 0f, 0, 0 });
            Bind(n, "FromMax", t, b, "From_Max" + sfx, fallback: new[] { 1f, 1, 1 });
            Bind(n, "ToMin", t, b, "To_Min" + sfx, fallback: new[] { 0f, 0, 0 });
            Bind(n, "ToMax", t, b, "To_Max" + sfx, fallback: new[] { 1f, 1, 1 });
            Bind(n, "Steps", t, b, "Steps" + sfx, fallback: new[] { 4f, 4, 4 });
            // Older files use display names instead of identifiers.
            FixupByName(n, b, "FromMin", "From Min"); FixupByName(n, b, "FromMax", "From Max");
            FixupByName(n, b, "ToMin", "To Min"); FixupByName(n, b, "ToMax", "To Max");
            n.Outputs.Add(new IROutput { Name = "Result", Type = t });
            return Map(n, (vector ? "Vector" : "Result", "Result"));
        }

        void FixupByName(IRNode n, BlendNode b, string port, string socketName)
        {
            var input = n.Input(port);
            if (input == null || input.Link != null) return;
            var s = b.inputs.Find(x => x.name == socketName && x.enabled);
            if (s == null) return;
            n.Inputs.Remove(input);
            Bind(n, port, input.Type, b, s.identifier);
        }

        Dictionary<string, IRRef> Mapping(BlendNode b)
        {
            string body;
            switch (b.Prop("vector_type", "POINT"))
            {
                case "TEXTURE": body = "Result = b2u_safe_div3(b2u_rotate_euler(Vec - Location, Rotation, true), Scale);"; break;
                case "VECTOR": body = "Result = b2u_rotate_euler(Vec * Scale, Rotation, false);"; break;
                case "NORMAL": body = "Result = normalize(b2u_rotate_euler(b2u_safe_div3(Vec, Scale), Rotation, false));"; break;
                default: body = "Result = b2u_rotate_euler(Vec * Scale, Rotation, false) + Location;"; break;
            }
            var n = _g.AddFunction("Mapping", body);
            Bind(n, "Vec", F3, b, "Vector", ImplicitInput.UV0);
            Bind(n, "Location", F3, b, "Location", fallback: new[] { 0f, 0, 0 });
            Bind(n, "Rotation", F3, b, "Rotation", fallback: new[] { 0f, 0, 0 });
            Bind(n, "Scale", F3, b, "Scale", fallback: new[] { 1f, 1, 1 });
            n.Outputs.Add(new IROutput { Name = "Result", Type = F3 });
            return Map(n, ("Vector", "Result"));
        }

        Dictionary<string, IRRef> NormalMap(BlendNode b)
        {
            var space = b.Prop("space", "TANGENT");
            string body;
            if (space == "TANGENT")
                body = "float3 t = Color * 2.0 - 1.0;\n" +
                       "float3 n = normalize(t.x * TangentWS + t.y * BitangentWS + t.z * NormalWS);\n" +
                       "Normal = normalize(lerp(NormalWS, n, max(Strength, 0.0)));";
            else
            {
                Warn($"'{b.label}': {space} space normal map is converted assuming Blender world/object axes.");
                body = "float3 n = normalize(b2u_from_blender(Color * 2.0 - 1.0));\n" +
                       "Normal = normalize(lerp(NormalWS, n, max(Strength, 0.0)));";
            }
            if (b.uvIndex > 0) Warn($"'{b.label}': tangents from UV map {b.uvIndex} are not available; using the first UV map.");
            var node = _g.AddFunction("Normal Map", body);
            Bind(node, "Strength", F1, b, "Strength");
            Bind(node, "Color", F3, b, "Color", fallback: new[] { 0.5f, 0.5f, 1f });
            AddImplicit(node, "NormalWS", F3, ImplicitInput.NormalWS);
            AddImplicit(node, "TangentWS", F3, ImplicitInput.TangentWS);
            AddImplicit(node, "BitangentWS", F3, ImplicitInput.BitangentWS);
            node.Outputs.Add(new IROutput { Name = "Normal", Type = F3 });
            return Map(node, ("Normal", "Normal"));
        }

        Dictionary<string, IRRef> Bump(BlendNode b)
        {
            var invert = b.PropBool("invert") ? "-1.0" : "1.0";
            var n = _g.AddFunction("Bump",
                "float3 dPdx = ddx(PositionWS);\nfloat3 dPdy = ddy(PositionWS);\n" +
                "float3 Rx = cross(dPdy, Normal);\nfloat3 Ry = cross(Normal, dPdx);\n" +
                "float det = dot(dPdx, Rx);\n" +
                "float3 grad = sign(det) * (ddx(Height) * Rx + ddy(Height) * Ry);\n" +
                $"float3 nb = normalize(abs(det) * Normal - Distance * {invert} * grad);\n" +
                "Result = normalize(lerp(Normal, nb, max(Strength, 0.0)));");
            Bind(n, "Strength", F1, b, "Strength");
            Bind(n, "Distance", F1, b, "Distance");
            Bind(n, "Height", F1, b, "Height");
            Bind(n, "Normal", F3, b, "Normal", ImplicitInput.NormalWS);
            AddImplicit(n, "PositionWS", F3, ImplicitInput.PositionWS);
            n.Outputs.Add(new IROutput { Name = "Result", Type = F3 });
            return Map(n, ("Normal", "Result"));
        }
    }
}
