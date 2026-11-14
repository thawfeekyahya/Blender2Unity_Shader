using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;

namespace B2U
{
    // ----------------------------------------------------------------------------------
    // Backend-neutral intermediate representation.
    //
    // A Blender node tree is translated into a DAG of IRNodes. Each IRNode is either
    //   * a Function: an HLSL function body with typed inputs/outputs. The HLSL backend
    //     emits it as a plain function, the Shader Graph backend as a Custom Function node.
    //   * a Texture: a 2D texture sample (native Sample Texture 2D node in Shader Graph).
    // Inputs are bound to another node's output, a constant, or an implicit mesh value
    // (UV, normal, view direction...) that each backend provides in its own way.
    // ----------------------------------------------------------------------------------

    public enum PortType { Float = 1, Float2 = 2, Float3 = 3, Float4 = 4 }

    public enum ImplicitInput
    {
        None, UV0, UV1, NormalWS, TangentWS, BitangentWS, ViewDirWS, PositionWS, PositionOS
    }

    public enum IRNodeKind { Function, Texture }

    public sealed class IRRef
    {
        public readonly IRNode Node;
        public readonly string Port;
        public IRRef(IRNode node, string port) { Node = node; Port = port; }
        public PortType Type => Node.Output(Port).Type;
        public override string ToString() => $"{Node.Id}.{Port}";
    }

    public sealed class IRInput
    {
        public string Name;
        public PortType Type;
        public IRRef Link;
        public float[] Constant = new float[4];
        public ImplicitInput Implicit;
        public bool IsConstant => Link == null && Implicit == ImplicitInput.None;
    }

    public sealed class IROutput
    {
        public string Name;
        public PortType Type;
    }

    public sealed class IRTexture
    {
        public string PropertyName;   // shader property reference, e.g. _Tex0
        public string DisplayName;
        public string ImageName;      // Blender image name
        public string AssetPath;      // Unity asset path once imported
        public bool Srgb;
        public bool Closest;
        public bool Clamp;
    }

    public sealed class IRNode
    {
        public int Id;
        public IRNodeKind Kind;
        public string Title;
        public string Body;
        public readonly List<IRInput> Inputs = new List<IRInput>();
        public readonly List<IROutput> Outputs = new List<IROutput>();
        public IRTexture Texture;

        public string FunctionName => "B2U_N" + Id;

        public IROutput Output(string name)
        {
            foreach (var o in Outputs) if (o.Name == name) return o;
            throw new ArgumentException($"IR node {Id} ({Title}) has no output '{name}'");
        }

        public IRInput Input(string name) => Inputs.FirstOrDefault(i => i.Name == name);

        public IRRef this[string port] => new IRRef(this, Output(port).Name);
    }

    public sealed class IRGraph
    {
        public string MaterialName;
        public readonly List<IRNode> Nodes = new List<IRNode>();
        public readonly List<IRTexture> Textures = new List<IRTexture>();
        public readonly List<string> Warnings = new List<string>();
        public IRNode Surface;          // final node: BaseColor, Metallic, Smoothness, NormalWS, Emission, Alpha
        public bool Transparent;
        public bool AlphaClip;
        public float AlphaThreshold = 0.5f;
        public bool DoubleSided = true;

        public static readonly string[] SurfaceOutputs =
            { "BaseColor", "Metallic", "Smoothness", "NormalWS", "Emission", "Alpha" };

        public IRNode AddFunction(string title, string body)
        {
            var n = new IRNode { Id = Nodes.Count, Kind = IRNodeKind.Function, Title = title, Body = body };
            Nodes.Add(n);
            return n;
        }

        public IRNode AddTexture(IRTexture tex)
        {
            var n = new IRNode { Id = Nodes.Count, Kind = IRNodeKind.Texture, Title = "Texture " + tex.DisplayName, Texture = tex };
            n.Inputs.Add(new IRInput { Name = "UV", Type = PortType.Float3, Implicit = ImplicitInput.UV0 });
            n.Outputs.Add(new IROutput { Name = "Color", Type = PortType.Float3 });
            n.Outputs.Add(new IROutput { Name = "Alpha", Type = PortType.Float });
            Nodes.Add(n);
            if (!Textures.Contains(tex)) Textures.Add(tex);
            return n;
        }

        /// <summary>Nodes reachable from <see cref="Surface"/>, dependencies first.</summary>
        public List<IRNode> Ordered()
        {
            var result = new List<IRNode>();
            var state = new Dictionary<IRNode, int>();
            void Visit(IRNode n)
            {
                if (state.TryGetValue(n, out var s))
                {
                    if (s == 1) throw new InvalidOperationException("Cycle in shader graph at " + n.Title);
                    return;
                }
                state[n] = 1;
                foreach (var i in n.Inputs) if (i.Link != null) Visit(i.Link.Node);
                state[n] = 2;
                result.Add(n);
            }
            if (Surface != null) Visit(Surface);
            return result;
        }

        /// <summary>Longest distance from each node to the surface node (for layout).</summary>
        public Dictionary<IRNode, int> Depths()
        {
            var depth = new Dictionary<IRNode, int>();
            var ordered = Ordered();
            for (int k = ordered.Count - 1; k >= 0; k--)
            {
                var n = ordered[k];
                if (!depth.ContainsKey(n)) depth[n] = 0;
                foreach (var i in n.Inputs)
                    if (i.Link != null)
                    {
                        var d = depth[n] + 1;
                        if (!depth.TryGetValue(i.Link.Node, out var cur) || cur < d) depth[i.Link.Node] = d;
                    }
            }
            return depth;
        }

        public HashSet<ImplicitInput> UsedImplicits()
        {
            var set = new HashSet<ImplicitInput>();
            foreach (var n in Ordered())
                foreach (var i in n.Inputs)
                    if (i.Link == null && i.Implicit != ImplicitInput.None) set.Add(i.Implicit);
            return set;
        }
    }

    public static class Hlsl
    {
        public static string TypeName(PortType t) =>
            t == PortType.Float ? "float" : "float" + (int)t;

        public static string F(float v)
        {
            if (float.IsNaN(v) || float.IsInfinity(v)) v = 0f;
            var s = v.ToString("0.0######", CultureInfo.InvariantCulture);
            return s;
        }

        public static string Literal(PortType t, float[] v)
        {
            float C(int i) => v != null && i < v.Length ? v[i] : 0f;
            switch (t)
            {
                case PortType.Float: return F(C(0));
                case PortType.Float2: return $"float2({F(C(0))}, {F(C(1))})";
                case PortType.Float3: return $"float3({F(C(0))}, {F(C(1))}, {F(C(2))})";
                default: return $"float4({F(C(0))}, {F(C(1))}, {F(C(2))}, {F(C(3))})";
            }
        }

        /// <summary>Emit an HLSL function definition for a Function IR node.</summary>
        public static string FunctionDefinition(IRNode n, string suffix)
        {
            var sb = new StringBuilder();
            sb.Append("// ").Append(n.Title.Replace("\n", " ")).Append('\n');
            sb.Append("void ").Append(n.FunctionName).Append(suffix).Append('(');
            var args = new List<string>();
            foreach (var i in n.Inputs) args.Add(TypeName(i.Type) + " " + i.Name);
            foreach (var o in n.Outputs) args.Add("out " + TypeName(o.Type) + " " + o.Name);
            sb.Append(string.Join(", ", args)).Append(")\n{\n");
            foreach (var line in n.Body.Split('\n'))
                sb.Append("    ").Append(line.TrimEnd()).Append('\n');
            sb.Append("}\n");
            return sb.ToString();
        }

        public static string Identifier(string s)
        {
            var sb = new StringBuilder();
            foreach (var c in s ?? "") sb.Append(char.IsLetterOrDigit(c) ? c : '_');
            var r = sb.ToString().Trim('_');
            if (r.Length == 0) r = "Unnamed";
            if (char.IsDigit(r[0])) r = "_" + r;
            return r;
        }
    }
}
