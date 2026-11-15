"""
Blender to Unity Shader Exporter (companion of the Unity package "Blender to Unity Shader Converter").

Exports the scene (or the selection) to FBX together with its textures and a <name>.b2u.json
description of every material's shader node tree, straight into a Unity project's Assets folder.
The Unity package detects the .b2u.json on import and generates Shader Graph or HLSL shaders,
materials, and assigns them to the FBX.

The export runs in a background Blender process on a temporary copy of the current file, so the
open scene is never modified (baking unsupported nodes rewires node trees).
"""

bl_info = {
    "name": "Blender to Unity Shader Exporter",
    "author": "B2U",
    "version": (0, 1, 0),
    "blender": (3, 6, 0),
    "location": "File > Export > Unity (B2U); Shader Editor > Sidebar > Unity",
    "description": "Export FBX + shader node trees for conversion to Unity Shader Graph / HLSL",
    "category": "Import-Export",
}

import json
import os
import queue
import shutil
import subprocess
import tempfile
import threading

import bpy
from bpy.props import BoolProperty, EnumProperty, FloatProperty, IntProperty, StringProperty
from bpy_extras.io_utils import ExportHelper

from . import b2u_export as core

MANIFEST_SUFFIX = ".b2u.json"


# --------------------------------------------------------------------------------------
# Helpers
# --------------------------------------------------------------------------------------

def addon_prefs(context):
    addon = context.preferences.addons.get(__package__)
    return addon.preferences if addon else None


def find_unity_assets_dir(path):
    """Returns the enclosing Unity 'Assets' folder of path, or None."""
    d = os.path.abspath(path)
    while True:
        if os.path.basename(d) == "Assets" and os.path.isdir(os.path.join(os.path.dirname(d), "ProjectSettings")):
            return d
        parent = os.path.dirname(d)
        if parent == d:
            return None
        d = parent


def classify_node(node, supported, memo):
    """'ok' | 'approx' | 'bake' | None (structural node)."""
    t = node.bl_idname
    if node.mute or t in core.PASS_THROUGH_TYPES:
        return None
    shader_out = any(s.type == "SHADER" and s.is_linked for s in node.outputs)
    if t == "ShaderNodeGroup":
        if node.node_tree is None or not core.tree_has_unsupported(node.node_tree, supported, memo):
            return "ok"
        return "approx" if shader_out else "bake"
    if t in core.APPROXIMATED:
        return "approx"
    if t in supported:
        return "ok"
    return "approx" if shader_out else "bake"


STATUS_INFO = {
    "ok": ("CHECKMARK", "Converted"),
    "approx": ("ERROR", "Approximated"),
    "bake": ("RENDER_STILL", "Baked to texture"),
}


# --------------------------------------------------------------------------------------
# Preferences
# --------------------------------------------------------------------------------------

class B2U_AddonPreferences(bpy.types.AddonPreferences):
    bl_idname = __package__

    unity_project: StringProperty(
        name="Unity Project",
        description="Default Unity project folder (the one containing Assets/). Exports go to Assets/<Export Folder>",
        subtype="DIR_PATH",
    )
    export_folder: StringProperty(
        name="Export Folder",
        description="Folder inside Assets/ used by default",
        default="BlenderExports",
    )

    def draw(self, context):
        col = self.layout.column()
        col.prop(self, "unity_project")
        col.prop(self, "export_folder")
        if self.unity_project and not os.path.isdir(os.path.join(bpy.path.abspath(self.unity_project), "Assets")):
            col.label(text="No Assets folder found in this directory.", icon="ERROR")
        col.label(text="The Unity project needs the 'Blender to Unity Shader Converter' package installed.", icon="INFO")


# --------------------------------------------------------------------------------------
# Export operator
# --------------------------------------------------------------------------------------

class EXPORT_SCENE_OT_b2u_unity(bpy.types.Operator, ExportHelper):
    """Export FBX, textures and shader node data for Unity (converted by the B2U Unity package)"""
    bl_idname = "export_scene.b2u_unity"
    bl_label = "Export to Unity"
    bl_options = {"PRESET"}

    filename_ext = ".fbx"
    filter_glob: StringProperty(default="*.fbx", options={"HIDDEN"})

    # Unity conversion
    shader_output: EnumProperty(
        name="Shader Output",
        items=[("SHADER_GRAPH", "Shader Graph", "Editable .shadergraph assets"),
               ("HLSL", "HLSL", "Hand-written .shader files")],
        default="SHADER_GRAPH",
    )
    pipeline: EnumProperty(
        name="Render Pipeline",
        items=[("URP", "URP", "Universal Render Pipeline"),
               ("BUILTIN", "Built-in", "Built-in Render Pipeline")],
        default="URP",
    )
    use_subfolder: BoolProperty(
        name="Own Folder",
        description="Put the export into a folder named after the file",
        default=True,
    )

    # Scope
    use_selection: BoolProperty(name="Selected Objects", description="Export only selected objects", default=False)
    all_materials: BoolProperty(name="Unused Materials", description="Also export materials not assigned to objects", default=False)

    # Materials
    bake_unsupported: BoolProperty(
        name="Bake Unsupported Nodes",
        description="Bake procedural textures and other nodes without a Unity equivalent to textures (Cycles)",
        default=True,
    )
    bake_size: EnumProperty(
        name="Bake Size",
        items=[(s, s, "") for s in ("256", "512", "1024", "2048", "4096")],
        default="1024",
    )
    bake_samples: IntProperty(name="Bake Samples", default=16, min=1, max=4096)

    # FBX
    global_scale: FloatProperty(name="Scale", default=1.0, min=0.001, max=1000.0)
    apply_modifiers: BoolProperty(name="Apply Modifiers", default=True)
    # Animation (mirrors Blender's FBX exporter "Bake Animation" panel)
    bake_animation: BoolProperty(
        name="Bake Animation",
        description="Export baked keyframe animation",
        default=False,
    )
    anim_key_all_bones: BoolProperty(
        name="Key All Bones",
        description="Force exporting at least one key of animation for all bones "
                    "(needed with some target applications, like UE4)",
        default=True,
    )
    anim_nla_strips: BoolProperty(
        name="NLA Strips",
        description="Export each non-muted NLA strip as a separated FBX's AnimStack, if any, "
                    "instead of global scene animation",
        default=True,
    )
    anim_all_actions: BoolProperty(
        name="All Actions",
        description="Export each action as a separated FBX's AnimStack, instead of global scene animation "
                    "(note that animated objects will get all actions compatible with them, "
                    "others will get no animation at all)",
        default=True,
    )
    anim_force_start_end_keying: BoolProperty(
        name="Force Start/End Keying",
        description="Always add a keyframe at start and end of actions for animated channels",
        default=True,
    )
    anim_sampling_rate: FloatProperty(
        name="Sampling Rate",
        description="How often to evaluate animated values (in frames)",
        default=1.0, min=0.01, max=100.0, soft_min=0.1, soft_max=10.0,
    )
    anim_simplify: FloatProperty(
        name="Simplify",
        description="How much to simplify baked values (0.0 to disable, the higher the more simplified)",
        default=1.0, min=0.0, max=100.0, soft_min=0.0, soft_max=10.0,
    )
    apply_transform: BoolProperty(name="Apply Transform", description="FBX exporter's experimental Apply Transform", default=False)
    object_types: EnumProperty(
        name="Object Types",
        options={"ENUM_FLAG"},
        items=[("MESH", "Mesh", ""), ("ARMATURE", "Armature", ""), ("EMPTY", "Empty", ""),
               ("LIGHT", "Light", ""), ("CAMERA", "Camera", ""), ("OTHER", "Other", "")],
        default={"MESH", "ARMATURE", "EMPTY", "OTHER"},
    )

    # Runtime state of the modal export
    _proc = None
    _timer = None
    _lines = None
    _last = ""
    _tmpdir = ""
    _manifest = ""

    def invoke(self, context, event):
        prefs = addon_prefs(context)
        stem = bpy.path.display_name_from_filepath(bpy.data.filepath) or "untitled"
        if prefs and prefs.unity_project:
            assets = os.path.join(bpy.path.abspath(prefs.unity_project), "Assets")
            if os.path.isdir(assets):
                folder = os.path.join(assets, prefs.export_folder or "")
                os.makedirs(folder, exist_ok=True)
                self.filepath = os.path.join(folder, stem + self.filename_ext)
        if not self.filepath:
            self.filepath = stem + self.filename_ext
        context.window_manager.fileselect_add(self)
        return {"RUNNING_MODAL"}

    def draw(self, context):
        layout = self.layout
        layout.use_property_split = True
        layout.use_property_decorate = False

        box = layout.box()
        box.label(text="Unity", icon="SHADING_RENDERED")
        box.prop(self, "shader_output")
        box.prop(self, "pipeline")
        box.prop(self, "use_subfolder")
        if find_unity_assets_dir(os.path.dirname(self.filepath)) is None:
            box.label(text="Not inside a Unity project's Assets folder", icon="ERROR")

        box = layout.box()
        box.label(text="Include", icon="OBJECT_DATA")
        box.prop(self, "use_selection")
        box.prop(self, "all_materials")
        box.column().prop(self, "object_types")

        box = layout.box()
        box.label(text="Materials", icon="MATERIAL")
        box.prop(self, "bake_unsupported")
        sub = box.column()
        sub.enabled = self.bake_unsupported
        sub.prop(self, "bake_size")
        sub.prop(self, "bake_samples")

        box = layout.box()
        box.label(text="FBX", icon="EXPORT")
        box.prop(self, "global_scale")
        box.prop(self, "apply_modifiers")
        box.prop(self, "apply_transform")

        box = layout.box()
        header = box.row()
        header.prop(self, "bake_animation")
        col = box.column()
        col.enabled = self.bake_animation
        col.prop(self, "anim_key_all_bones")
        col.prop(self, "anim_nla_strips")
        col.prop(self, "anim_all_actions")
        col.prop(self, "anim_force_start_end_keying")
        col.prop(self, "anim_sampling_rate")
        col.prop(self, "anim_simplify")

    # ------------------------------------------------------------------

    def _build_job(self, context, out_dir, stem):
        objects = [o.name for o in context.selected_objects] if self.use_selection else []
        return {
            "outputDir": out_dir,
            "fbxName": stem + ".fbx",
            "manifestName": stem + MANIFEST_SUFFIX,
            "sourceBlend": bpy.data.filepath or (stem + ".blend"),
            "exportFbx": True,
            "objects": objects,
            "fbx": {
                "scale": self.global_scale,
                "applyModifiers": self.apply_modifiers,
                "bakeAnimation": self.bake_animation,
                "animKeyAllBones": self.anim_key_all_bones,
                "animNlaStrips": self.anim_nla_strips,
                "animAllActions": self.anim_all_actions,
                "animForceStartEndKeying": self.anim_force_start_end_keying,
                "animSamplingRate": self.anim_sampling_rate,
                "animSimplify": self.anim_simplify,
                "objectTypes": sorted(self.object_types),
                "applyTransform": self.apply_transform,
            },
            "allMaterials": self.all_materials,
            "bakeUnsupported": self.bake_unsupported,
            "bakeSize": int(self.bake_size),
            "bakeSamples": self.bake_samples,
            "supportedNodes": sorted(core.DEFAULT_SUPPORTED),
            "unity": {
                "shaderOutput": "ShaderGraph" if self.shader_output == "SHADER_GRAPH" else "HLSL",
                "pipeline": "URP" if self.pipeline == "URP" else "BuiltIn",
                "bakeAnimation": self.bake_animation,
            },
        }

    def execute(self, context):
        if self.use_selection and not context.selected_objects:
            self.report({"ERROR"}, "Nothing selected")
            return {"CANCELLED"}

        target = bpy.path.ensure_ext(bpy.path.abspath(self.filepath), self.filename_ext)
        stem = os.path.splitext(os.path.basename(target))[0]
        out_dir = os.path.dirname(target)
        if self.use_subfolder and os.path.basename(out_dir) != stem:
            out_dir = os.path.join(out_dir, stem)
        os.makedirs(out_dir, exist_ok=True)
        if find_unity_assets_dir(out_dir) is None:
            self.report({"WARNING"}, "Export folder is not inside a Unity project's Assets folder; "
                                     "copy it into Assets/ manually.")

        if context.object is not None and context.object.mode != "OBJECT":
            bpy.ops.object.mode_set(mode="OBJECT")

        # Snapshot the current (possibly unsaved) state; the background process works on it.
        self._tmpdir = tempfile.mkdtemp(prefix="b2u_")
        tmp_blend = os.path.join(self._tmpdir, "source.blend")
        bpy.ops.wm.save_as_mainfile(filepath=tmp_blend, copy=True, check_existing=False)

        job = self._build_job(context, out_dir, stem)
        job_path = os.path.join(self._tmpdir, "job.json")
        with open(job_path, "w", encoding="utf-8") as fh:
            json.dump(job, fh, indent=1)
        self._manifest = os.path.join(out_dir, job["manifestName"])

        cmd = [bpy.app.binary_path, "-b", tmp_blend, "--python-exit-code", "1",
               "--python", os.path.abspath(core.__file__), "--", "--config", job_path]
        try:
            self._proc = subprocess.Popen(cmd, stdout=subprocess.PIPE, stderr=subprocess.STDOUT,
                                          text=True, encoding="utf-8", errors="replace", bufsize=1)
        except OSError as exc:
            self._cleanup(context)
            self.report({"ERROR"}, "Could not start Blender: %s" % exc)
            return {"CANCELLED"}

        self._lines = queue.Queue()
        threading.Thread(target=self._reader, args=(self._proc, self._lines), daemon=True).start()
        self._last = "Starting..."

        if context.window is None:  # called from a script without UI: block
            self._proc.wait()
            return self._finish(context)

        wm = context.window_manager
        self._timer = wm.event_timer_add(0.25, window=context.window)
        wm.modal_handler_add(self)
        wm.progress_begin(0, 100)
        return {"RUNNING_MODAL"}

    @staticmethod
    def _reader(proc, q):
        for line in proc.stdout:
            q.put(line.rstrip("\n"))
        q.put(None)

    def _drain(self):
        log = []
        while True:
            try:
                line = self._lines.get_nowait()
            except queue.Empty:
                break
            if line is None:
                continue
            log.append(line)
            print(line)
            if line.startswith("[B2U]"):
                self._last = line[6:]
        return log

    def modal(self, context, event):
        if event.type == "ESC":
            self._proc.terminate()
            self._cleanup(context)
            self.report({"WARNING"}, "Unity export cancelled")
            return {"CANCELLED"}
        if event.type != "TIMER":
            return {"PASS_THROUGH"}
        self._drain()
        if context.workspace:
            context.workspace.status_text_set("Exporting to Unity: %s  (Esc to cancel)" % self._last)
        if self._proc.poll() is None:
            return {"PASS_THROUGH"}
        return self._finish(context)

    def _finish(self, context):
        self._drain()
        code = self._proc.returncode
        self._cleanup(context)
        if code != 0 or not os.path.isfile(self._manifest):
            self.report({"ERROR"}, "Unity export failed (exit code %s). See the system console." % code)
            return {"CANCELLED"}
        try:
            with open(self._manifest, "r", encoding="utf-8") as fh:
                data = json.load(fh)
            warnings = list(data.get("warnings", []))
            for m in data.get("materials", []):
                warnings.extend("[%s] %s" % (m["name"], w) for w in m.get("warnings", []))
            for w in warnings:
                print("[B2U] Warning: " + w)
            self.report({"WARNING"} if warnings else {"INFO"},
                        "Exported %d material(s) to %s%s" % (
                            len(data.get("materials", [])), os.path.dirname(self._manifest),
                            (" with %d warning(s) (see console)" % len(warnings)) if warnings else ""))
        except Exception as exc:
            self.report({"WARNING"}, "Exported, but could not read the manifest: %s" % exc)
        return {"FINISHED"}

    def _cleanup(self, context):
        wm = context.window_manager
        if self._timer is not None:
            wm.event_timer_remove(self._timer)
            self._timer = None
            wm.progress_end()
        if context.workspace:
            context.workspace.status_text_set(None)
        if self._tmpdir:
            shutil.rmtree(self._tmpdir, ignore_errors=True)
            self._tmpdir = ""


# --------------------------------------------------------------------------------------
# Shader Editor panel: shows how each node of the active material will convert
# --------------------------------------------------------------------------------------

class NODE_OT_b2u_select_node(bpy.types.Operator):
    """Select and focus this node"""
    bl_idname = "node.b2u_select_node"
    bl_label = "Select Node"
    bl_options = {"REGISTER", "UNDO"}

    node_name: StringProperty()

    def execute(self, context):
        space = context.space_data
        tree = getattr(space, "edit_tree", None)
        if tree is None or self.node_name not in tree.nodes:
            return {"CANCELLED"}
        for n in tree.nodes:
            n.select = False
        node = tree.nodes[self.node_name]
        node.select = True
        tree.nodes.active = node
        try:
            bpy.ops.node.view_selected()
        except RuntimeError:
            pass
        return {"FINISHED"}


class NODE_PT_b2u_unity(bpy.types.Panel):
    bl_space_type = "NODE_EDITOR"
    bl_region_type = "UI"
    bl_category = "Unity"
    bl_label = "Unity Export"

    @classmethod
    def poll(cls, context):
        space = context.space_data
        return space is not None and space.tree_type == "ShaderNodeTree"

    def draw(self, context):
        layout = self.layout
        layout.operator(EXPORT_SCENE_OT_b2u_unity.bl_idname, text="Export to Unity...", icon="EXPORT")

        mat = context.material
        if mat is None:
            layout.label(text="No active material")
            return
        if not mat.use_nodes or mat.node_tree is None:
            layout.label(text="Not using nodes: viewport color is exported", icon="INFO")
            return
        tree = mat.node_tree
        out = core.find_material_output(tree)
        if out is None:
            layout.label(text="No Material Output node", icon="ERROR")
            return

        supported = core.DEFAULT_SUPPORTED
        memo = {}
        reachable = core.upstream_nodes(tree, out)
        rows = []
        for node in tree.nodes:
            if node.name not in reachable:
                continue
            status = classify_node(node, supported, memo)
            if status is not None:
                rows.append((status, node))
        if out.inputs.get("Displacement") is not None and out.inputs["Displacement"].is_linked:
            rows.append(("approx", out))

        counts = {k: sum(1 for s, _ in rows if s == k) for k in STATUS_INFO}
        summary = layout.box().column(align=True)
        for key, (icon, label) in STATUS_INFO.items():
            summary.label(text="%s: %d" % (label, counts[key]), icon=icon)

        order = {"bake": 0, "approx": 1, "ok": 2}
        col = layout.column(align=True)
        for status, node in sorted(rows, key=lambda r: (order[r[0]], r[1].name)):
            icon, label = STATUS_INFO[status]
            row = col.row(align=True)
            op = row.operator(NODE_OT_b2u_select_node.bl_idname, text=node.label or node.name, icon=icon, emboss=False)
            op.node_name = node.name
        if counts["bake"]:
            layout.label(text="Baked nodes need a mesh with UVs.", icon="INFO")


# --------------------------------------------------------------------------------------
# Registration
# --------------------------------------------------------------------------------------

def menu_func_export(self, context):
    self.layout.operator(EXPORT_SCENE_OT_b2u_unity.bl_idname, text="Unity (B2U) (.fbx + shaders)")


CLASSES = (
    B2U_AddonPreferences,
    EXPORT_SCENE_OT_b2u_unity,
    NODE_OT_b2u_select_node,
    NODE_PT_b2u_unity,
)


def register():
    for cls in CLASSES:
        bpy.utils.register_class(cls)
    bpy.types.TOPBAR_MT_file_export.append(menu_func_export)


def unregister():
    bpy.types.TOPBAR_MT_file_export.remove(menu_func_export)
    for cls in reversed(CLASSES):
        bpy.utils.unregister_class(cls)
