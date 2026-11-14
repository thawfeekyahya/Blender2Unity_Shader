# Blender to Unity Shader Converter

A Unity Editor plugin that exports a `.blend` file to FBX through Blender and converts every
Blender shader node tree into either a **Shader Graph** (`.shadergraph`) or a hand-written
**HLSL** shader (`.shader`). It then creates the materials and assigns them to the imported model.

It comes with a companion **Blender add-on**, so you can also start the export from Blender
(**File → Export → Unity (B2U)**). See [Exporting from Blender](#exporting-from-blender).

## Requirements

- Unity 2021.3 or newer
- Blender 3.6 or newer (4.x supported), installed locally
- For Shader Graph output: the Shader Graph package, plus URP (or Shader Graph's Built-in target)
- For HLSL output: URP 12+ or the Built-in Render Pipeline

## Installation

Pick one:

- **Package Manager → Add package from disk…** and select `package.json`, or
- add the repository as a git package, or
- copy the whole folder into `Assets/` (for example `Assets/Plugins/Blender2UnityShader`).

Then open **Edit → Preferences → Blender to Unity** and point it to the Blender executable
(it tries to detect it automatically).

## Usage

1. **Tools → Blender to Unity → Converter**, or right-click a `.blend` in the Project window →
   **Blender to Unity → Convert .blend (FBX + Shaders)**.
2. Choose the `.blend` file (or drag it onto the field), an output folder inside `Assets`,
   **Output** (Shader Graph or HLSL), and the **Render Pipeline**.
3. Click **Convert**.

The output looks like this:

```
Assets/BlenderImports/<blend name>/
  <blend name>.fbx        model, with materials remapped to the converted ones
  Textures/               images used by the materials, plus any baked textures
  Shaders/                .shadergraph (+ _B2U.hlsl include) or .shader per material
  Materials/              .mat per Blender material
  B2U_Report.txt          warnings and the Blender log
```

## How it works

```
Unity (C#)                                Blender (background, Blender~/b2u_unity_exporter/b2u_export.py)
──────────                                ─────────────────────────────────────────
write job.json  ── blender -b file.blend --python b2u_export.py -- --config job.json ──►
                                          1. bake unsupported nodes to textures (Cycles)
                                          2. flatten node groups / reroutes / muted nodes
                                          3. write materials.json + textures/
                                          4. export FBX
read materials.json ◄──────────────────────────────────────────────────────────────────
NodeTranslator  → backend-neutral IR (a graph of HLSL functions + texture samples)
                 ├─ ShaderGraphWriter → .shadergraph + .hlsl include
                 └─ HlslShaderWriter  → .shader (URP forward Lit pass / Built-in surface shader)
create materials, import FBX, remap its materials
```

- **Shader Graph output**: Image textures become native *Sample Texture 2D* nodes with
  exposed texture properties. Mesh inputs (UV, normal, tangent, view direction, position) become
  native input nodes. Every other Blender node becomes a *Custom Function* node whose code is in
  the generated `<Material>_B2U.hlsl`. Each Custom Function node keeps the Blender node's name,
  and you can edit its unconnected inputs in the graph.
- **HLSL output**: one self-contained `.shader` per material, with the same node functions inlined.
- Closures (BSDFs, Mix/Add Shader) are expanded into PBR channels (base color, metallic,
  roughness, world normal, emission, alpha). That makes *Mix Shader* a per-channel blend.
- Color ramps and curves are sampled in Blender into 64-entry tables that are embedded in the shader.

## Exporting from Blender

The add-on lives in `Blender~/b2u_unity_exporter/`. It shares the exporter script with the Unity
plugin, so both paths produce identical results.

**Install (Blender 3.6+ / 4.x)**

1. Build the zip: `python Blender~/build_addon_zip.py` (written to `Blender~/dist/b2u_unity_exporter.zip`).
2. In Blender, open **Edit → Preferences → Add-ons → Install from Disk…** (in 3.6, **Install…**),
   pick the zip and enable **Blender to Unity Shader Exporter**.
3. Optional: in the add-on preferences, set your **Unity Project** folder. The export dialog then
   opens in `Assets/BlenderExports/`.

**Use**

- **File → Export → Unity (B2U) (.fbx + shaders)**. Choose a location inside a Unity project's
  `Assets` folder, then pick Shader Graph or HLSL, the render pipeline, selection-only export,
  baking and the FBX options.
- **Shader Editor → Sidebar (N) → Unity** lists every node feeding the active material's output
  and marks it as *Converted*, *Approximated* or *Baked to texture*. Click a node to jump to it.

The export runs in a background Blender process on a temporary copy of the file, so your open
scene and unsaved changes are never modified. Press **Esc** to cancel. It writes:

```
Assets/BlenderExports/<name>/
  <name>.fbx
  <name>.b2u.json      shader node data + the Unity options chosen in Blender
  textures/
```

When Unity imports `<name>.b2u.json`, the plugin converts the bundle in place, adding `Shaders/`,
`Materials/` and `B2U_Report.txt`, and assigns the materials to the FBX. Re-exporting from Blender
updates everything. You can turn auto-conversion off in **Preferences → Blender to Unity**, and
convert by hand with right-click → **Blender to Unity → Convert Blender Export Bundle**. If you
export outside a Unity project, copy the folder into `Assets/` later.

## Supported nodes

| Category  | Nodes |
|-----------|-------|
| Shader    | Principled BSDF, Diffuse, Emission, Glossy/Anisotropic, Mix Shader, Add Shader, Transparent, Holdout, Specular (EEVEE). Glass/Refraction/Translucent/SSS/Toon/Sheen are approximated |
| Input     | Texture Coordinate, UV Map, RGB, Value, Fresnel, Layer Weight, Geometry |
| Texture   | Image Texture, Checker, Gradient |
| Color     | Mix (all blend modes), MixRGB (legacy), Invert, Hue/Saturation/Value, Bright/Contrast, Gamma, RGB Curves |
| Vector    | Mapping, Normal Map, Bump, Vector Curves, Vector Math (all operations) |
| Converter | Math (all operations), Color Ramp, RGB to BW, Separate/Combine Color/RGB/HSV/XYZ, Clamp, Map Range, Float Curve |
| Structure | Node groups (nested), Reroutes, muted nodes, Frames |

When **Bake Unsupported Nodes** is on, any other node that feeds the material output
(Noise, Voronoi, Wave, Musgrave, Ambient Occlusion, Attribute, Vertex Color, and so on) is baked
to a texture with Cycles. The baked texture uses the mesh's active UV map and replaces the node,
so the rest of the graph stays live. This needs a mesh with UVs that uses the material.

## Limitations

- Unity's Lit model has no transmission, clearcoat, sheen, subsurface or volume.
  The converter warns about these inputs and ignores them.
- *Displacement* on the Material Output isn't converted. Use a Bump node instead.
- Object and Generated coordinates are converted from Unity's to Blender's axes, which is
  approximate. Camera and Window coordinates aren't supported.
- Baked textures depend on non-overlapping UVs.
- HLSL/URP shaders reuse the URP Lit ShadowCaster/DepthOnly passes (`UsePass`). Those passes
  don't alpha-clip, and the shaders aren't SRP-Batcher compatible.
- Shader Graph files are written in the multi-JSON format used by Shader Graph 12+.

## Re-running

Converting again overwrites the generated shaders and textures in place. Existing materials are
updated, not recreated, so references in scenes and prefabs stay intact. Turn off
**Overwrite Existing** to keep materials you've edited by hand.
