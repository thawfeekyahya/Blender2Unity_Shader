"""
Blender -> Unity exporter (run inside Blender, in background mode).

    blender -b scene.blend --python-exit-code 1 --python b2u_export.py -- --config job.json

job.json (written by the Unity plugin):
{
  "outputDir": "/abs/path/for/fbx/and/textures",
  "fbxName": "scene.fbx",
  "exportFbx": true,
  "fbx": { "scale": 1.0, "applyModifiers": true, "bakeAnimation": false,
           "animKeyAllBones": true, "animNlaStrips": true, "animAllActions": true,
           "animForceStartEndKeying": true, "animSamplingRate": 1.0, "animSimplify": 1.0,
           "objectTypes": ["MESH", "ARMATURE", "EMPTY"], "applyTransform": false },
  "bakeUnsupported": true,
  "bakeSize": 1024,
  "bakeSamples": 16,
  "supportedNodes": ["ShaderNodeBsdfPrincipled", ...]
}

Optional keys: "objects" (restrict to these object names), "manifestName" (default
"materials.json"), "sourceBlend" (original .blend path when running on a temp copy),
"unity" (options passed through to the Unity importer).

Produces <outputDir>/<manifestName> describing every material's (flattened) node graph,
plus all referenced images and baked textures in <outputDir>/textures/.
"""

import json
import os
import shutil
import sys
import tempfile
import traceback

import bpy

FORMAT_VERSION = 1
LUT_SAMPLES = 64

# Node types the Unity converter translates natively. Keep in sync with
# Unity/Conversion/NodeTranslator.cs (SupportedTypes). Used when no list is passed in the job.
DEFAULT_SUPPORTED = {
    "ShaderNodeOutputMaterial",
    "ShaderNodeBsdfPrincipled", "ShaderNodeBsdfDiffuse", "ShaderNodeEmission", "ShaderNodeMixShader",
    "ShaderNodeAddShader", "ShaderNodeBsdfTransparent", "ShaderNodeBsdfGlossy", "ShaderNodeBsdfAnisotropic",
    "ShaderNodeBsdfGlass", "ShaderNodeBsdfRefraction", "ShaderNodeHoldout", "ShaderNodeBsdfTranslucent",
    "ShaderNodeSubsurfaceScattering", "ShaderNodeBsdfToon", "ShaderNodeBsdfSheen", "ShaderNodeBsdfVelvet",
    "ShaderNodeEeveeSpecular",
    "ShaderNodeTexCoord", "ShaderNodeUVMap", "ShaderNodeRGB", "ShaderNodeValue", "ShaderNodeFresnel",
    "ShaderNodeLayerWeight", "ShaderNodeNewGeometry",
    "ShaderNodeTexImage", "ShaderNodeTexChecker", "ShaderNodeTexGradient",
    "ShaderNodeMix", "ShaderNodeMixRGB", "ShaderNodeInvert", "ShaderNodeHueSaturation",
    "ShaderNodeBrightContrast", "ShaderNodeGamma", "ShaderNodeRGBCurve",
    "ShaderNodeMapping", "ShaderNodeNormalMap", "ShaderNodeBump", "ShaderNodeVectorCurve",
    "ShaderNodeVectorMath",
    "ShaderNodeMath", "ShaderNodeValToRGB", "ShaderNodeRGBToBW", "ShaderNodeSeparateColor",
    "ShaderNodeCombineColor", "ShaderNodeSeparateRGB", "ShaderNodeCombineRGB", "ShaderNodeSeparateHSV",
    "ShaderNodeCombineHSV", "ShaderNodeSeparateXYZ", "ShaderNodeCombineXYZ", "ShaderNodeClamp",
    "ShaderNodeMapRange", "ShaderNodeFloatCurve",
}

# Shader (closure) nodes the converter approximates rather than translating exactly.
APPROXIMATED = {
    "ShaderNodeBsdfGlass", "ShaderNodeBsdfRefraction", "ShaderNodeBsdfTranslucent",
    "ShaderNodeSubsurfaceScattering", "ShaderNodeBsdfToon", "ShaderNodeBsdfSheen", "ShaderNodeBsdfVelvet",
}


def supported_set(cfg):
    lst = cfg.get("supportedNodes")
    return set(lst) if lst else set(DEFAULT_SUPPORTED)

# Nodes that never need translating: they are removed by link resolution.
PASS_THROUGH_TYPES = {"NodeReroute", "NodeGroupInput", "NodeGroupOutput", "NodeFrame"}

# Base node RNA properties that are not interesting to export.
_IGNORED_PROPS = {
    "rna_type", "type", "location", "width", "width_hidden", "height", "dimensions", "name",
    "label", "inputs", "outputs", "internal_links", "parent", "use_custom_color", "color",
    "select", "show_options", "show_preview", "hide", "mute", "show_texture", "bl_idname",
    "bl_label", "bl_description", "bl_icon", "bl_static_type", "bl_width_default",
    "bl_width_min", "bl_width_max", "bl_height_default", "bl_height_min", "bl_height_max",
    "node_tree", "image", "image_user", "color_ramp", "mapping", "texture_mapping",
    "color_mapping", "script", "filepath", "is_active_output", "location_absolute",
    "warning_propagation", "color_tag",
}


def log(msg):
    print("[B2U] " + str(msg), flush=True)


# --------------------------------------------------------------------------------------
# Value helpers
# --------------------------------------------------------------------------------------

def to_vec4(value):
    """Normalise any socket default_value to a 4 float list."""
    if value is None:
        return [0.0, 0.0, 0.0, 0.0]
    if isinstance(value, bool):
        return [1.0 if value else 0.0, 0.0, 0.0, 0.0]
    if isinstance(value, (int, float)):
        return [float(value), 0.0, 0.0, 0.0]
    try:
        vals = [float(v) for v in value]
    except TypeError:
        return [0.0, 0.0, 0.0, 0.0]
    while len(vals) < 4:
        vals.append(1.0 if len(vals) == 3 else 0.0)
    return vals[:4]


def socket_value(sock):
    return to_vec4(getattr(sock, "default_value", None))


def socket_dict(sock):
    return {
        "name": sock.name,
        "identifier": sock.identifier,
        "type": sock.type,  # VALUE, INT, BOOLEAN, VECTOR, RGBA, SHADER, ...
        "value": socket_value(sock),
        "implicit": bool(getattr(sock, "hide_value", False)),
        "enabled": bool(getattr(sock, "enabled", True)),
    }


def node_props(node):
    props = []
    for prop in node.bl_rna.properties:
        pid = prop.identifier
        if pid in _IGNORED_PROPS or prop.type not in {"ENUM", "BOOLEAN", "INT", "FLOAT", "STRING"}:
            continue
        if getattr(prop, "is_array", False) and prop.type != "STRING":
            continue
        try:
            value = getattr(node, pid)
        except Exception:
            continue
        if isinstance(value, set):
            value = ",".join(sorted(value))
        props.append({"key": pid, "value": str(value)})
    return props


# --------------------------------------------------------------------------------------
# LUT sampling for ramps / curves (avoids needing extra textures in Unity)
# --------------------------------------------------------------------------------------

def sample_color_ramp(ramp):
    out = []
    for i in range(LUT_SAMPLES):
        t = i / (LUT_SAMPLES - 1)
        out.extend(float(c) for c in ramp.evaluate(t))
    return out


def _eval_curve(mapping, curve, x):
    try:
        return float(mapping.evaluate(curve, x))
    except Exception:
        return float(curve.evaluate(x))


def sample_rgb_curves(mapping):
    """Returns LUT_SAMPLES * 4 floats: (R(C(x)), G(C(x)), B(C(x)), C(x))."""
    mapping.initialize()
    curves = mapping.curves
    out = []
    for i in range(LUT_SAMPLES):
        x = i / (LUT_SAMPLES - 1)
        c = _eval_curve(mapping, curves[3], x) if len(curves) > 3 else x
        r = _eval_curve(mapping, curves[0], c)
        g = _eval_curve(mapping, curves[1], c)
        b = _eval_curve(mapping, curves[2], c)
        out.extend([r, g, b, c])
    return out


def sample_vector_curves(mapping):
    """Returns LUT_SAMPLES * 4 floats over x in [-1, 1]: (X(x), Y(x), Z(x), 0)."""
    mapping.initialize()
    curves = mapping.curves
    out = []
    for i in range(LUT_SAMPLES):
        x = -1.0 + 2.0 * i / (LUT_SAMPLES - 1)
        out.extend([_eval_curve(mapping, curves[k], x) for k in range(3)] + [0.0])
    return out


def sample_float_curve(mapping):
    mapping.initialize()
    curve = mapping.curves[0]
    out = []
    for i in range(LUT_SAMPLES):
        x = i / (LUT_SAMPLES - 1)
        out.extend([_eval_curve(mapping, curve, x), 0.0, 0.0, 0.0])
    return out


# --------------------------------------------------------------------------------------
# Images
# --------------------------------------------------------------------------------------

LINEAR_COLORSPACES = {"Non-Color", "Raw", "Linear", "Linear Rec.709", "Linear CIE-XYZ E",
                      "Generic Data", "Linear FilmLight E-Gamut", "Linear Rec.2020", "ACEScg"}

_FORMAT_EXT = {"PNG": ".png", "JPEG": ".jpg", "TARGA": ".tga", "TARGA_RAW": ".tga",
               "OPEN_EXR": ".exr", "OPEN_EXR_MULTILAYER": ".exr", "HDR": ".hdr",
               "TIFF": ".tif", "BMP": ".bmp", "WEBP": ".webp"}

# Formats Unity can import directly.
_UNITY_EXT = {".png", ".jpg", ".jpeg", ".tga", ".exr", ".hdr", ".tif", ".tiff", ".bmp", ".psd", ".gif"}


def safe_name(name):
    keep = []
    for ch in name:
        keep.append(ch if (ch.isalnum() or ch in "-_") else "_")
    return "".join(keep).strip("_") or "unnamed"


class ImageExporter:
    def __init__(self, out_dir):
        self.out_dir = out_dir
        self.tex_dir = os.path.join(out_dir, "textures")
        os.makedirs(self.tex_dir, exist_ok=True)
        self.images = {}       # image name -> info dict
        self.used_files = set()

    def _unique(self, base, ext):
        candidate = base + ext
        i = 1
        while candidate.lower() in self.used_files:
            candidate = "%s_%d%s" % (base, i, ext)
            i += 1
        self.used_files.add(candidate.lower())
        return candidate

    def export(self, image, warnings):
        if image is None:
            return None
        if image.name in self.images:
            return self.images[image.name]

        if image.source in {"SEQUENCE", "MOVIE", "TILED"}:
            warnings.append("Image '%s' is a %s; only the first frame/tile is exported." % (image.name, image.source))

        src_path = bpy.path.abspath(image.filepath, library=image.library) if image.filepath else ""
        src_ext = os.path.splitext(src_path)[1].lower()
        base = safe_name(os.path.splitext(os.path.basename(src_path))[0] if src_path else image.name)

        dest_rel = None
        try:
            if (image.source == "FILE" and not image.packed_file and src_path
                    and os.path.isfile(src_path) and src_ext in _UNITY_EXT):
                fname = self._unique(base, src_ext)
                shutil.copy2(src_path, os.path.join(self.tex_dir, fname))
                dest_rel = "textures/" + fname
            else:
                is_float = bool(image.is_float)
                fmt = "OPEN_EXR" if is_float else "PNG"
                fname = self._unique(base, _FORMAT_EXT[fmt])
                dest = os.path.join(self.tex_dir, fname)
                self._save_copy(image, dest, fmt)
                dest_rel = "textures/" + fname
        except Exception as exc:  # keep going, report in Unity
            warnings.append("Could not export image '%s': %s" % (image.name, exc))

        cs = image.colorspace_settings.name
        info = {
            "name": image.name,
            "file": dest_rel or "",
            "colorSpace": cs,
            "srgb": cs not in LINEAR_COLORSPACES,
            "alphaMode": image.alpha_mode,
            "width": int(image.size[0]),
            "height": int(image.size[1]),
        }
        self.images[image.name] = info
        return info

    @staticmethod
    def _save_copy(image, dest, fmt):
        # Images that only live in memory (packed / generated / baked) are written via a
        # temporary scene render settings copy so the original datablock is untouched.
        scene = bpy.context.scene
        settings = scene.render.image_settings
        old = (settings.file_format, settings.color_depth, settings.color_mode)
        try:
            settings.file_format = fmt
            settings.color_mode = "RGBA"
            settings.color_depth = "32" if fmt == "OPEN_EXR" else "8"
            image.save_render(dest, scene=scene)
        except Exception:
            # Fallback: redirect the datablock and save it directly.
            image.filepath_raw = dest
            image.file_format = fmt
            image.save()
        finally:
            try:
                settings.file_format, settings.color_depth, settings.color_mode = old
            except Exception:
                pass


# --------------------------------------------------------------------------------------
# Node tree flattening (groups, reroutes, muted nodes)
# --------------------------------------------------------------------------------------

def find_material_output(tree):
    outputs = [n for n in tree.nodes if n.bl_idname == "ShaderNodeOutputMaterial"]
    if not outputs:
        return None
    for target in ("ALL", "EEVEE", "CYCLES"):
        for n in outputs:
            if n.is_active_output and n.target == target:
                return n
    for n in outputs:
        if n.target in ("ALL", "EEVEE"):
            return n
    return outputs[0]


class FlatGraph:
    """Flattens a material node tree into a list of 'real' nodes plus direct links."""

    def __init__(self, supported, warnings):
        self.supported = supported
        self.warnings = warnings
        self.real = {}          # node id -> bpy node
        self.inputs = {}        # (node id, socket identifier) -> bpy socket
        self.passthru = {}      # (node id, output identifier) -> (node id, input identifier)
        self.link_into = {}     # (to id, to socket) -> (from id, from socket)
        self.group_out = {}     # group node id -> inner active group output id
        self.dead_outputs = set()

    def add_tree(self, tree, prefix="", group_node_id=None, depth=0):
        if depth > 32:
            self.warnings.append("Node group nesting too deep in '%s'." % tree.name)
            return
        group_outputs = [n for n in tree.nodes if n.bl_idname == "NodeGroupOutput"]
        active_go = next((n for n in group_outputs if n.is_active_output), None)
        if active_go is None and group_outputs:
            active_go = group_outputs[0]

        for node in tree.nodes:
            t = node.bl_idname
            if t == "NodeFrame":
                continue
            nid = prefix + node.name
            for s in node.inputs:
                self.inputs[(nid, s.identifier)] = s

            if node.mute:
                mapped = set()
                for il in node.internal_links:
                    self.passthru[(nid, il.to_socket.identifier)] = (nid, il.from_socket.identifier)
                    mapped.add(il.to_socket.identifier)
                for s in node.outputs:
                    if s.identifier not in mapped:
                        self.dead_outputs.add((nid, s.identifier))
            elif t == "NodeReroute":
                self.passthru[(nid, node.outputs[0].identifier)] = (nid, node.inputs[0].identifier)
            elif t == "NodeGroupInput":
                for s in node.outputs:
                    if group_node_id is not None:
                        self.passthru[(nid, s.identifier)] = (group_node_id, s.identifier)
                    else:
                        self.dead_outputs.add((nid, s.identifier))
            elif t == "NodeGroupOutput":
                if node == active_go and group_node_id is not None:
                    self.group_out[group_node_id] = nid
            elif t == "ShaderNodeGroup":
                if node.node_tree is None:
                    for s in node.outputs:
                        self.dead_outputs.add((nid, s.identifier))
                    continue
                self.add_tree(node.node_tree, nid + "/", nid, depth + 1)
                go = self.group_out.get(nid)
                for s in node.outputs:
                    if go is not None:
                        self.passthru[(nid, s.identifier)] = (go, s.identifier)
                    else:
                        self.dead_outputs.add((nid, s.identifier))
            else:
                self.real[nid] = node

        for link in tree.links:
            if getattr(link, "is_muted", False) or not link.is_valid:
                continue
            if link.from_node.bl_idname == "NodeFrame" or link.to_node.bl_idname == "NodeFrame":
                continue
            self.link_into[(prefix + link.to_node.name, link.to_socket.identifier)] = (
                prefix + link.from_node.name, link.from_socket.identifier)

    def resolve(self, nid, sock_id):
        """What feeds input socket (nid, sock_id)?
        Returns None (unlinked), ('link', from_id, from_sock) or ('const', bpy_socket_or_None)."""
        src = self.link_into.get((nid, sock_id))
        if src is None:
            return None
        guard = 0
        while guard < 10000:
            guard += 1
            if src in self.passthru:
                target = self.passthru[src]
                nxt = self.link_into.get(target)
                if nxt is None:
                    return ("const", self.inputs.get(target))
                src = nxt
                continue
            if src[0] in self.real and src not in self.dead_outputs:
                return ("link", src[0], src[1])
            return ("const", None)
        return ("const", None)


# --------------------------------------------------------------------------------------
# Baking of unsupported (procedural) nodes to textures
# --------------------------------------------------------------------------------------

def upstream_nodes(tree, out_node):
    """Names of nodes that feed the material output (Surface / Displacement only)."""
    into = {}
    for link in tree.links:
        into.setdefault(link.to_node.name, []).append(link)
    seen = set()
    stack = []
    for link in into.get(out_node.name, []):
        if link.to_socket.identifier in ("Surface", "Displacement"):
            stack.append(link.from_node)
    while stack:
        node = stack.pop()
        if node.name in seen:
            continue
        seen.add(node.name)
        for link in into.get(node.name, []):
            stack.append(link.from_node)
    return seen


def tree_has_unsupported(tree, supported, memo, depth=0):
    if tree.name in memo:
        return memo[tree.name]
    memo[tree.name] = False
    result = False
    for n in tree.nodes:
        t = n.bl_idname
        if n.mute or t in PASS_THROUGH_TYPES:
            continue
        if t == "ShaderNodeGroup":
            if n.node_tree and depth < 32 and tree_has_unsupported(n.node_tree, supported, memo, depth + 1):
                result = True
                break
        elif t not in supported:
            result = True
            break
    memo[tree.name] = result
    return result


# Optional restriction to a set of object names (export selection only).
OBJECT_FILTER = None


def scene_objects():
    objs = bpy.context.scene.objects
    if OBJECT_FILTER is None:
        return list(objs)
    return [o for o in objs if o.name in OBJECT_FILTER]


def objects_using(mat):
    return [o for o in scene_objects()
            if o.type == "MESH" and any(s.material == mat for s in o.material_slots)]


class Baker:
    def __init__(self, cfg, out_dir, warnings):
        self.supported = supported_set(cfg)
        self.size = int(cfg.get("bakeSize", 1024))
        self.samples = int(cfg.get("bakeSamples", 16))
        self.margin = int(cfg.get("bakeMargin", 16))
        # Outside the output folder: it may be inside a Unity project.
        self.bake_dir = tempfile.mkdtemp(prefix="b2u_bake_")
        self.warnings = warnings
        self.memo = {}
        self.scene_ready = False

    def needs_bake(self, node):
        t = node.bl_idname
        if node.mute or t in PASS_THROUGH_TYPES:
            return False
        if t == "ShaderNodeGroup":
            return node.node_tree is not None and tree_has_unsupported(node.node_tree, self.supported, self.memo)
        return t not in self.supported

    def _prepare_scene(self):
        if self.scene_ready:
            return
        scene = bpy.context.scene
        scene.render.engine = "CYCLES"
        scene.cycles.samples = self.samples
        scene.cycles.device = "CPU"
        scene.render.bake.target = "IMAGE_TEXTURES"
        scene.render.bake.use_selected_to_active = False
        os.makedirs(self.bake_dir, exist_ok=True)
        self.scene_ready = True

    def bake_material(self, mat):
        tree = mat.node_tree
        out = find_material_output(tree)
        if out is None:
            return
        reachable = upstream_nodes(tree, out)
        targets = []
        for node in tree.nodes:
            if node.name not in reachable or not self.needs_bake(node):
                continue
            socks = [s for s in node.outputs
                     if s.enabled and s.type != "SHADER" and any(
                         l.to_node.name in reachable or l.to_node == out for l in s.links)]
            if socks:
                targets.append((node, socks))
            elif any(s.type == "SHADER" and s.is_linked for s in node.outputs):
                self.warnings.append("[%s] Shader node '%s' (%s) is not supported and cannot be baked; "
                                     "it will be approximated." % (mat.name, node.name, node.bl_idname))
        if not targets:
            return

        objs = [o for o in objects_using(mat) if o.data.uv_layers]
        if not objs:
            self.warnings.append("[%s] Needs baking for %d node(s) but no mesh with UVs uses it."
                                 % (mat.name, len(targets)))
            return
        self._prepare_scene()
        for node, socks in targets:
            for sock in socks:
                try:
                    self._bake_socket(mat, node, sock, objs)
                    log("Baked %s / %s.%s" % (mat.name, node.name, sock.identifier))
                except Exception as exc:
                    self.warnings.append("[%s] Bake of '%s.%s' failed: %s" % (mat.name, node.name, sock.name, exc))

    def _bake_socket(self, mat, node, sock, objs):
        tree = mat.node_tree
        nodes, links = tree.nodes, tree.links
        is_vec = sock.type == "VECTOR"
        img_name = "%s_%s_%s" % (safe_name(mat.name), safe_name(node.name), safe_name(sock.identifier))
        img = bpy.data.images.new(img_name, self.size, self.size, alpha=False, float_buffer=is_vec)
        img.colorspace_settings.name = "sRGB" if sock.type == "RGBA" else "Non-Color"

        old_out = find_material_output(tree)
        emit = nodes.new("ShaderNodeEmission")
        tmp_out = nodes.new("ShaderNodeOutputMaterial")
        tmp_out.target = "ALL"
        links.new(sock, emit.inputs["Color"])
        links.new(emit.outputs[0], tmp_out.inputs["Surface"])
        tmp_out.is_active_output = True

        img_node = nodes.new("ShaderNodeTexImage")
        img_node.image = img
        img_node.label = "B2U Baked " + node.name
        for n in nodes:
            n.select = False
        img_node.select = True
        nodes.active = img_node

        # Every other material on the selected objects needs an active image node too.
        dummies = []
        dummy_img = bpy.data.images.new("B2U_dummy", 8, 8)
        for o in objs:
            for slot in o.material_slots:
                m = slot.material
                if m is None or m == mat or not m.use_nodes:
                    continue
                d = m.node_tree.nodes.new("ShaderNodeTexImage")
                d.image = dummy_img
                m.node_tree.nodes.active = d
                dummies.append((m, d))

        try:
            view_layer = bpy.context.view_layer
            for o in view_layer.objects:
                o.select_set(False)
            for o in objs:
                o.hide_set(False)
                o.hide_select = False
                o.select_set(True)
            view_layer.objects.active = objs[0]
            bpy.ops.object.bake(type="EMIT", margin=self.margin, use_clear=True)

            ext = ".exr" if is_vec else ".png"
            path = os.path.join(self.bake_dir, img_name + ext)
            img.filepath_raw = path
            img.file_format = "OPEN_EXR" if is_vec else "PNG"
            img.save()
            img.source = "FILE"
        finally:
            for m, d in dummies:
                m.node_tree.nodes.remove(d)
            bpy.data.images.remove(dummy_img)
            nodes.remove(emit)
            nodes.remove(tmp_out)
            if old_out is not None:
                old_out.is_active_output = True

        # Rewire: everything that consumed the baked socket now reads the baked image.
        for link in list(sock.links):
            links.new(img_node.outputs["Color"], link.to_socket)
        uv = nodes.new("ShaderNodeUVMap")
        uv.uv_map = objs[0].data.uv_layers.active.name
        links.new(uv.outputs["UV"], img_node.inputs["Vector"])
        img_node.extension = "EXTEND"


# --------------------------------------------------------------------------------------
# Material export
# --------------------------------------------------------------------------------------

def uv_names_for(mat):
    for o in objects_using(mat):
        if o.data.uv_layers:
            return [uv.name for uv in o.data.uv_layers]
    return []


def node_dict(node, nid, images, uv_names, warnings, mat_name):
    d = {
        "id": nid,
        "type": node.bl_idname,
        "label": node.label or node.name,
        "props": node_props(node),
        "inputs": [socket_dict(s) for s in node.inputs],
        "outputs": [socket_dict(s) for s in node.outputs],
        "lut": [],
        "image": "",
        "uvIndex": -1,
    }
    t = node.bl_idname
    try:
        if t in ("ShaderNodeTexImage", "ShaderNodeTexEnvironment"):
            info = images.export(node.image, warnings)
            d["image"] = info["name"] if info else ""
            if info is None:
                warnings.append("[%s] Image Texture node '%s' has no image." % (mat_name, node.name))
        elif t == "ShaderNodeValToRGB":
            d["lut"] = sample_color_ramp(node.color_ramp)
            d["props"].append({"key": "rampInterpolation", "value": node.color_ramp.interpolation})
        elif t == "ShaderNodeRGBCurve":
            d["lut"] = sample_rgb_curves(node.mapping)
        elif t == "ShaderNodeVectorCurve":
            d["lut"] = sample_vector_curves(node.mapping)
        elif t == "ShaderNodeFloatCurve":
            d["lut"] = sample_float_curve(node.mapping)
    except Exception as exc:
        warnings.append("[%s] Failed reading data of node '%s': %s" % (mat_name, node.name, exc))

    uv_map = getattr(node, "uv_map", "")
    if uv_map:
        d["uvIndex"] = uv_names.index(uv_map) if uv_map in uv_names else 0
    return d


def const_node(cid, sock, fallback_type):
    stype = sock.type if sock is not None else fallback_type
    value = socket_value(sock) if sock is not None else [0.0, 0.0, 0.0, 0.0]
    return {
        "id": cid, "type": "B2U_Constant", "label": "Constant", "props": [],
        "inputs": [],
        "outputs": [{"name": "Value", "identifier": "Value", "type": stype, "value": value,
                     "implicit": False, "enabled": True, "linked": False}],
        "lut": [], "image": "", "uvIndex": -1,
    }


def export_material(mat, cfg, images, warnings):
    data = {
        "name": mat.name,
        "useNodes": bool(mat.use_nodes and mat.node_tree),
        "blendMethod": str(getattr(mat, "blend_method", "OPAQUE")),
        "renderMethod": str(getattr(mat, "surface_render_method", "")),
        "alphaThreshold": float(getattr(mat, "alpha_threshold", 0.5)),
        "backfaceCulling": bool(getattr(mat, "use_backface_culling", False)),
        "diffuseColor": to_vec4(mat.diffuse_color),
        "metallic": float(mat.metallic),
        "roughness": float(mat.roughness),
        "outputNode": "",
        "nodes": [],
        "links": [],
        "warnings": [],
    }
    if not data["useNodes"]:
        return data

    mat_warn = data["warnings"]
    tree = mat.node_tree
    out = find_material_output(tree)
    if out is None:
        mat_warn.append("Material has no Material Output node.")
        return data

    fg = FlatGraph(supported_set(cfg), mat_warn)
    fg.add_tree(tree)
    uv_names = uv_names_for(mat)
    data["outputNode"] = out.name

    visited = set()
    queue = [out.name]
    while queue:
        nid = queue.pop()
        if nid in visited or nid not in fg.real:
            continue
        visited.add(nid)
        node = fg.real[nid]
        nd = node_dict(node, nid, images, uv_names, mat_warn, mat.name)
        for sd in nd["inputs"]:
            sd["linked"] = False
            if node == out and sd["identifier"] not in ("Surface", "Displacement"):
                continue
            res = fg.resolve(nid, sd["identifier"])
            if res is None:
                continue
            sd["linked"] = True
            if res[0] == "link":
                data["links"].append({"fromNode": res[1], "fromSocket": res[2],
                                      "toNode": nid, "toSocket": sd["identifier"]})
                queue.append(res[1])
            else:
                cid = "%s::%s::const" % (nid, sd["identifier"])
                data["nodes"].append(const_node(cid, res[1], sd["type"]))
                data["links"].append({"fromNode": cid, "fromSocket": "Value",
                                      "toNode": nid, "toSocket": sd["identifier"]})
        for sd in nd["outputs"]:
            sd["linked"] = False
        data["nodes"].append(nd)

    linked_out = {(l["fromNode"], l["fromSocket"]) for l in data["links"]}
    for nd in data["nodes"]:
        for sd in nd["outputs"]:
            sd["linked"] = (nd["id"], sd["identifier"]) in linked_out
    return data


# --------------------------------------------------------------------------------------
# FBX
# --------------------------------------------------------------------------------------

def export_fbx(path, fbx_cfg):
    use_selection = OBJECT_FILTER is not None
    if use_selection:
        view_layer = bpy.context.view_layer
        for o in view_layer.objects:
            o.select_set(o.name in OBJECT_FILTER)
    kwargs = dict(
        filepath=path,
        use_selection=use_selection,
        global_scale=float(fbx_cfg.get("scale", 1.0)),
        apply_unit_scale=True,
        apply_scale_options="FBX_SCALE_UNITS",
        axis_forward="-Z",
        axis_up="Y",
        bake_space_transform=bool(fbx_cfg.get("applyTransform", False)),
        object_types=set(fbx_cfg.get("objectTypes", ["MESH", "ARMATURE", "EMPTY"])),
        use_mesh_modifiers=bool(fbx_cfg.get("applyModifiers", True)),
        mesh_smooth_type="FACE",
        use_tspace=True,
        add_leaf_bones=False,
        bake_anim=bool(fbx_cfg.get("bakeAnimation", False)),
        # Same options and defaults as Blender's FBX exporter "Bake Animation" panel.
        bake_anim_use_all_bones=bool(fbx_cfg.get("animKeyAllBones", True)),
        bake_anim_use_nla_strips=bool(fbx_cfg.get("animNlaStrips", True)),
        bake_anim_use_all_actions=bool(fbx_cfg.get("animAllActions", True)),
        bake_anim_force_startend_keying=bool(fbx_cfg.get("animForceStartEndKeying", True)),
        bake_anim_step=min(max(float(fbx_cfg.get("animSamplingRate", 1.0)), 0.01), 100.0),
        bake_anim_simplify_factor=min(max(float(fbx_cfg.get("animSimplify", 1.0)), 0.0), 100.0),
        path_mode="AUTO",
        embed_textures=False,
    )
    bpy.ops.export_scene.fbx(**kwargs)


# --------------------------------------------------------------------------------------
# Entry point
# --------------------------------------------------------------------------------------

def collect_materials(all_materials):
    mats = []
    seen = set()
    for o in scene_objects():
        if o.type not in {"MESH", "CURVE", "SURFACE", "META", "FONT"}:
            continue
        for slot in o.material_slots:
            m = slot.material
            if m is not None and m.name not in seen:
                seen.add(m.name)
                mats.append(m)
    if all_materials:
        for m in bpy.data.materials:
            if m.name not in seen and not m.is_grease_pencil:
                seen.add(m.name)
                mats.append(m)
    return mats


def run(cfg):
    """Runs a full export described by a job dict (see module docstring)."""
    global OBJECT_FILTER
    names = cfg.get("objects")
    OBJECT_FILTER = set(names) if names else None

    out_dir = os.path.abspath(cfg["outputDir"])
    os.makedirs(out_dir, exist_ok=True)
    warnings = []

    if bpy.context.object is not None and bpy.context.object.mode != "OBJECT":
        bpy.ops.object.mode_set(mode="OBJECT")

    mats = collect_materials(bool(cfg.get("allMaterials", False)))
    log("Found %d material(s)" % len(mats))

    baker = None
    if cfg.get("bakeUnsupported", True):
        baker = Baker(cfg, out_dir, warnings)
        for m in mats:
            if m.use_nodes and m.node_tree:
                try:
                    baker.bake_material(m)
                except Exception as exc:
                    warnings.append("[%s] Baking failed: %s" % (m.name, exc))

    images = ImageExporter(out_dir)
    result = {
        "version": FORMAT_VERSION,
        "blenderVersion": bpy.app.version_string,
        "blendFile": cfg.get("sourceBlend") or bpy.data.filepath,
        "fbxFile": "",
        "unity": cfg.get("unity", {}),
        "materials": [],
        "images": [],
        "warnings": warnings,
    }
    for m in mats:
        try:
            result["materials"].append(export_material(m, cfg, images, warnings))
        except Exception as exc:
            warnings.append("[%s] Export failed: %s" % (m.name, exc))
            traceback.print_exc()
    result["images"] = list(images.images.values())

    if cfg.get("exportFbx", True):
        fbx_name = cfg.get("fbxName") or (os.path.splitext(os.path.basename(result["blendFile"]))[0] + ".fbx")
        export_fbx(os.path.join(out_dir, fbx_name), cfg.get("fbx", {}))
        result["fbxFile"] = fbx_name
        log("Exported FBX " + fbx_name)

    if baker is not None:
        shutil.rmtree(baker.bake_dir, ignore_errors=True)

    manifest = cfg.get("manifestName") or "materials.json"
    with open(os.path.join(out_dir, manifest), "w", encoding="utf-8") as fh:
        json.dump(result, fh, indent=1)
    log("Done. %d material(s), %d image(s), %d warning(s)."
        % (len(result["materials"]), len(result["images"]), len(warnings)))
    return result


def main():
    argv = sys.argv[sys.argv.index("--") + 1:] if "--" in sys.argv else []
    if "--config" not in argv:
        raise SystemExit("Usage: blender -b file.blend --python b2u_export.py -- --config job.json")
    with open(argv[argv.index("--config") + 1], "r", encoding="utf-8") as fh:
        cfg = json.load(fh)
    run(cfg)


if __name__ == "__main__":
    try:
        main()
    except SystemExit:
        raise
    except Exception:
        traceback.print_exc()
        sys.exit(1)
