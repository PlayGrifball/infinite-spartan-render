# Run by Blender: blender -b --python spartan.py -- loadout.json output-path
import importlib
import os
import json
import re
import shutil
import sys
from pathlib import Path

import addon_utils
import bpy
import mathutils

DATA = os.environ.get("ISR_DATA", "/data")
EKUR_ASSETS = "/opt/ekur/assets"
DEFAULT_STANCE = -1258174396
MOD = "bl_ext.user_default.ekur"
HASHED = re.compile(r"-?\d+_-?\d+_-?\d+(\.\d+)?")

loadout_path, out = sys.argv[sys.argv.index("--") + 1:][:2]
loadout = json.loads(Path(loadout_path).read_text())
core = loadout["Core"]
armor = loadout.get("Armor") or {}
coating = loadout.get("Coating") or ""
output = loadout.get("Output", "png")
full = loadout.get("Size") == "full"
stance_id = loadout.get("Stance")
if stance_id is None:
    stance_id = DEFAULT_STANCE
framing = loadout.get("Framing", "body")

addon_utils.enable(MOD, default_set=True)
bpy.context.preferences.addons[MOD].preferences.data_folder = DATA
ext = Path(bpy.utils.extension_path_user(MOD, create=True)) / "regions_and_permutations.json"
if not ext.exists():
    shutil.copy(f"{EKUR_ASSETS}/regions_and_permutations.json", ext)
sc = bpy.context.scene
o = sc.spartan_properties
o.import_specific_core = True
o.core = core
o.use_purp_rig = False
bpy.ops.ekur.importspartan()


def region_key(col):
    name = col.name.removeprefix("[REGION] ")
    return name.split("_", 1)[1] if "_" in name else name


PLACEHOLDER = re.compile(r"\d{3}-\d{3}-[a-z]+-[0-9a-f]{8}")


def default_permutation(objs, core):
    # Placeholder boxes (24 vertices, no material) sort before the real pieces; skip them.
    real = [x for x in objs if not HASHED.fullmatch(x.name) and not PLACEHOLDER.search(x.name)] or objs
    named = next((x for x in real if x.name.split(".")[0].lower().endswith("_" + core.lower())), None)
    return named or real[0]


keep = []
for col in bpy.data.collections:
    if not col.name.startswith("[REGION]"):
        continue
    objs = sorted((x for x in col.objects if x.type == "MESH"), key=lambda x: x.name)
    if not objs:
        continue
    if all(HASHED.fullmatch(x.name) for x in objs):
        first = objs[0].name.split("_")[1]
        keep += [x for x in objs if x.name.split("_")[1] == first]
        continue
    wanted = armor.get(region_key(col))
    chosen = next((x for x in objs if wanted and x.name.split(".")[0].endswith("_" + wanted)), None)
    if wanted and chosen is None:
        sys.exit(f"No permutation '{wanted}' in region '{region_key(col)}'")
    chosen = chosen or default_permutation(objs, core)
    keep.append(chosen)

unknown = set(armor) - {region_key(c) for c in bpy.data.collections if c.name.startswith("[REGION]")}
if unknown:
    sys.exit(f"Unknown regions: {sorted(unknown)}")

bts = sorted((c for c in bpy.data.collections if c.name.startswith("[BT]")), key=lambda c: c.name)
if bts:
    keep += [x for x in bts[0].objects if x.type == "MESH"]
keep = list(dict.fromkeys(keep))
for x in list(bpy.data.objects):
    if x.type == "MESH" and x not in keep:
        bpy.data.objects.remove(x, do_unlink=True)
for x in bpy.data.objects:
    x.select_set(False)
for x in keep:
    x.select_set(True)

mp = sc.material_properties
mp.selected_only = True
if coating:
    mp.use_default_coating = False
    mp.coating_id = coating

mo = importlib.import_module(MOD + ".src.operators.material_operator")
ju = importlib.import_module(MOD + ".src.utils")
jd = importlib.import_module(MOD + ".src.json_definitions")
done = set()
missing = set()
problems = []
for slot in mo.get_materials():
    if not slot.material or not slot.material.node_tree:
        continue
    n = slot.name.split(".")[0]
    if n in done or n in missing:
        continue
    p = Path(f"{DATA}/materials/{n}.json")
    if not p.exists():
        missing.add(n)
        if re.fullmatch(r"-?\d+", n):
            problems.append(f"material {n}: no JSON in the data folder")
        continue
    mo.remove_nodes(slot.material.node_tree)
    m = ju.read_json_file(p, jd.CommonMaterial)
    done.add(n)
    if m is None:
        problems.append(f"material {n}: unreadable JSON")
        continue
    try:
        mo.run_material(m, slot.material.node_tree)
    except Exception as e:
        problems.append(f"material {n}: {e!r}"[:200])
print("MATS", len(done), "missing", sorted(missing))
if problems:
    sys.exit("Materials failed, not rendering a degraded Spartan: " + "; ".join(problems))

stance_path = next(iter(sorted((Path(DATA) / "stances").glob(f"anim_{stance_id}_*.ekuranim"))), None)
armature = next((o for o in bpy.data.objects if o.type == "ARMATURE"), None)
if armature is None or stance_path is None:
    sys.exit(f"The armature or stance {stance_id} is missing from the data folder")
# Ekur leaves the pauldrons' Armature modifier without an armature, so they would never follow the pose.
for x in keep:
    for m in x.modifiers:
        if m.type == "ARMATURE" and m.object is None:
            m.object = armature
anim_data = importlib.import_module(MOD + ".src.model.importer.animation.anim_data")
anim_import = importlib.import_module(MOD + ".src.model.importer.animation.importer")
animation = anim_data.AnimationData()
with open(stance_path, "rb") as fh:
    animation.read(fh)
animation.frames, animation.total_frames = animation.frames[:1], 1
anim_import.import_animation(armature, animation, "stance")
sc.frame_set(1)


def bake_to_plain_materials(size):
    # Ekur's shader graph is not glTF-exportable: bake what it shows into plain textures on a Principled BSDF.
    sc.render.engine = "CYCLES"
    sc.cycles.device = "CPU"
    sc.cycles.samples = 1
    sc.cycles.use_denoising = False
    passes = (("color", "DIFFUSE", "sRGB"), ("rough", "ROUGHNESS", "Non-Color"), ("normal", "NORMAL", "Non-Color"), ("emit", "EMIT", "sRGB"))
    for obj in keep:
        for x in bpy.data.objects:
            x.select_set(False)
        obj.select_set(True)
        bpy.context.view_layer.objects.active = obj
        obj.data.uv_layers.active_index = 0
        mats = [s.material for s in obj.material_slots if s.material]
        images = {}
        for key, bake_type, space in passes:
            img = bpy.data.images.new(f"{obj.name}_{key}", size, size, alpha=False)
            img.colorspace_settings.name = space
            images[key] = img
            temp = []
            for m in mats:
                node = m.node_tree.nodes.new("ShaderNodeTexImage")
                node.image = img
                temp.append((m, node))
                m.node_tree.nodes.active = node
            kwargs = {"pass_filter": {"COLOR"}} if bake_type == "DIFFUSE" else {}
            bpy.ops.object.bake(type=bake_type, margin=8, margin_type="EXTEND", **kwargs)
            for m, node in temp:
                m.node_tree.nodes.remove(node)
        plain = bpy.data.materials.new(obj.name)
        plain.use_nodes = True
        nt = plain.node_tree
        nt.nodes.clear()
        out_node = nt.nodes.new("ShaderNodeOutputMaterial")
        bsdf = nt.nodes.new("ShaderNodeBsdfPrincipled")
        bsdf.inputs["Metallic"].default_value = 0.0
        nt.links.new(bsdf.outputs["BSDF"], out_node.inputs["Surface"])
        tex = {}
        for key, img in images.items():
            t = nt.nodes.new("ShaderNodeTexImage")
            t.image = img
            tex[key] = t
        nt.links.new(tex["color"].outputs["Color"], bsdf.inputs["Base Color"])
        split = nt.nodes.new("ShaderNodeSeparateColor")
        nt.links.new(tex["rough"].outputs["Color"], split.inputs["Color"])
        nt.links.new(split.outputs["Green"], bsdf.inputs["Roughness"])
        nmap = nt.nodes.new("ShaderNodeNormalMap")
        nmap.uv_map = obj.data.uv_layers[0].name
        nt.links.new(tex["normal"].outputs["Color"], nmap.inputs["Color"])
        nt.links.new(nmap.outputs["Normal"], bsdf.inputs["Normal"])
        nt.links.new(tex["emit"].outputs["Color"], bsdf.inputs["Emission Color"])
        bsdf.inputs["Emission Strength"].default_value = 1.0
        obj.data.materials.clear()
        obj.data.materials.append(plain)
        obj.data.polygons.foreach_set("material_index", [0] * len(obj.data.polygons))
        print("BAKED", obj.name, flush=True)


if output == "glb":
    for x in keep:
        for m in x.modifiers:
            if m.type == "ARMATURE":
                m.show_viewport = True
    bpy.context.view_layer.update()
    graph = bpy.context.evaluated_depsgraph_get()
    for x in keep:
        posed = bpy.data.meshes.new_from_object(x.evaluated_get(graph))
        for m in [m for m in x.modifiers if m.type == "ARMATURE"]:
            x.modifiers.remove(m)
        x.data = posed
    bake_to_plain_materials(2048 if full else 1024)
    for x in keep:
        x.select_set(True)
    bpy.ops.export_scene.gltf(filepath=out, export_format="GLB", use_selection=True, export_apply=True, export_image_format="JPEG", export_jpeg_quality=85)
    sys.exit(0)

depsgraph = bpy.context.evaluated_depsgraph_get()
head = [x for x in keep if any(col.name.endswith("_helmet") for col in x.users_collection)]
if framing == "head" and not head:
    sys.exit("Headshot requested but this core has no helmet region")
pts = [x.matrix_world @ mathutils.Vector(c) for x in (head if framing == "head" else keep) for c in x.evaluated_get(depsgraph).bound_box]
lo = mathutils.Vector([min(p[i] for p in pts) for i in range(3)])
hi = mathutils.Vector([max(p[i] for p in pts) for i in range(3)])
c = (lo + hi) / 2
height = hi.z - lo.z
cd = bpy.data.cameras.new("cam")
cd.lens = 85
cam = bpy.data.objects.new("cam", cd)
sc.collection.objects.link(cam)
cam.location = (c.x + (max(hi - lo) * 3.4 if framing == "head" else height * 2.6), c.y, c.z)
cam.rotation_euler = (1.5708, 0, 1.5708)
sc.camera = cam
for rot, energy in (((0.8, 0, 2.2), 4), ((1.0, 0, 0.6), 2)):
    s = bpy.data.objects.new("sun", bpy.data.lights.new("sun", "SUN"))
    s.data.energy = energy
    s.rotation_euler = rot
    sc.collection.objects.link(s)
w = bpy.data.worlds.new("w")
w.use_nodes = True
w.node_tree.nodes["Background"].inputs[0].default_value = (0.3, 0.3, 0.32, 1)
sc.world = w
sc.render.engine = "CYCLES"
sc.cycles.device = "CPU"
sc.cycles.samples = 128 if full else 48
sc.cycles.use_denoising = True
scale = 3 if full else 1
sc.render.resolution_x = 700 * scale
sc.render.resolution_y = (700 if framing == "head" else 1000) * scale
sc.render.image_settings.file_format = "PNG"
sc.render.use_file_extension = False
sc.render.filepath = out
bpy.ops.render.render(write_still=True)
