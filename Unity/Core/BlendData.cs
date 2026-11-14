using System;
using System.Collections.Generic;
using System.Linq;

namespace B2U
{
    // Mirrors materials.json written by Blender~/b2u_unity_exporter/b2u_export.py. Kept JsonUtility-friendly
    // (plain fields, arrays of objects, no dictionaries).

    [Serializable]
    public class BlendExportData
    {
        public int version;
        public string blenderVersion;
        public string blendFile;
        public string fbxFile;
        public List<BlendMaterial> materials = new List<BlendMaterial>();
        public List<BlendImage> images = new List<BlendImage>();
        public List<string> warnings = new List<string>();
        public BundleUnityOptions unity = new BundleUnityOptions();

        public BlendImage FindImage(string name) => images.FirstOrDefault(i => i.name == name);
    }

    /// <summary>Unity conversion options chosen in the Blender add-on (empty for Unity-driven exports).</summary>
    [Serializable]
    public class BundleUnityOptions
    {
        public string shaderOutput;   // "ShaderGraph" | "HLSL"
        public string pipeline;       // "URP" | "BuiltIn"
        public bool bakeAnimation;
    }

    [Serializable]
    public class BlendImage
    {
        public string name;
        public string file;
        public string colorSpace;
        public bool srgb;
        public string alphaMode;
        public int width;
        public int height;
    }

    [Serializable]
    public class BlendMaterial
    {
        public string name;
        public bool useNodes;
        public string blendMethod;
        public string renderMethod;
        public float alphaThreshold;
        public bool backfaceCulling;
        public float[] diffuseColor;
        public float metallic;
        public float roughness;
        public string outputNode;
        public List<BlendNode> nodes = new List<BlendNode>();
        public List<BlendLink> links = new List<BlendLink>();
        public List<string> warnings = new List<string>();

        [NonSerialized] Dictionary<string, BlendNode> _byId;
        [NonSerialized] Dictionary<string, BlendLink> _linkInto;

        public BlendNode Node(string id)
        {
            if (_byId == null)
            {
                _byId = new Dictionary<string, BlendNode>();
                foreach (var n in nodes) _byId[n.id] = n;
            }
            return id != null && _byId.TryGetValue(id, out var node) ? node : null;
        }

        public BlendLink LinkInto(string nodeId, string socketId)
        {
            if (_linkInto == null)
            {
                _linkInto = new Dictionary<string, BlendLink>();
                foreach (var l in links) _linkInto[l.toNode + "\n" + l.toSocket] = l;
            }
            return _linkInto.TryGetValue(nodeId + "\n" + socketId, out var link) ? link : null;
        }

        /// <summary>True when the material wants alpha blending (Blender "Blend" / "Blended").</summary>
        public bool IsTransparent => blendMethod == "BLEND" || renderMethod == "BLENDED";

        /// <summary>True when the material wants alpha clipping (Blender "Clip" / "Hashed").</summary>
        public bool IsAlphaClip => !IsTransparent && (blendMethod == "CLIP" || blendMethod == "HASHED");
    }

    [Serializable]
    public class BlendNode
    {
        public string id;
        public string type;
        public string label;
        public List<BlendProp> props = new List<BlendProp>();
        public List<BlendSocket> inputs = new List<BlendSocket>();
        public List<BlendSocket> outputs = new List<BlendSocket>();
        public float[] lut;
        public string image;
        public int uvIndex = -1;

        public string Prop(string key, string fallback = "")
        {
            foreach (var p in props) if (p.key == key) return p.value;
            return fallback;
        }

        public bool PropBool(string key, bool fallback = false)
        {
            var v = Prop(key, null);
            return v == null ? fallback : v == "True";
        }

        public float PropFloat(string key, float fallback = 0f)
        {
            var v = Prop(key, null);
            return v != null && float.TryParse(v, System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out var f) ? f : fallback;
        }

        /// <summary>Find an input by identifier first, then by display name.</summary>
        public BlendSocket Input(params string[] keys) => Find(inputs, keys);
        public BlendSocket Output(params string[] keys) => Find(outputs, keys);

        static BlendSocket Find(List<BlendSocket> list, string[] keys)
        {
            foreach (var k in keys)
                foreach (var s in list)
                    if (s.identifier == k) return s;
            foreach (var k in keys)
                foreach (var s in list)
                    if (s.name == k && s.enabled) return s;
            return null;
        }
    }

    [Serializable]
    public class BlendSocket
    {
        public string name;
        public string identifier;
        public string type;       // VALUE, INT, BOOLEAN, VECTOR, RGBA, SHADER
        public float[] value;
        public bool implicit;
        public bool enabled = true;
        public bool linked;

        public bool IsShader => type == "SHADER";
        public bool IsScalar => type == "VALUE" || type == "INT" || type == "BOOLEAN";

        public float V(int i) => value != null && i < value.Length ? value[i] : 0f;
    }

    [Serializable]
    public class BlendProp
    {
        public string key;
        public string value;
    }

    [Serializable]
    public class BlendLink
    {
        public string fromNode;
        public string fromSocket;
        public string toNode;
        public string toSocket;
    }
}
