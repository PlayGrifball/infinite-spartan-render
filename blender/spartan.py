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
MOD = "bl_ext.user_default.ekur"
HASHED = re.compile(r"-?\d+_-?\d+_-?\d+(\.\d+)?")

loadout_path, out = sys.argv[sys.argv.index("--") + 1:][:2]
loadout = json.loads(Path(loadout_path).read_text())
core = loadout["Core"]
armor = loadout.get("Armor") or {}
coating = loadout.get("Coating") or ""
output = loadout.get("Output", "png")
full = loadout.get("Size") == "full"

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
    keep.append(chosen or objs[0])

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

if output == "glb":
    if not full:
        for img in bpy.data.images:
            if img.size[0] > 1024 or img.size[1] > 1024:
                factor = 1024 / max(img.size[0], img.size[1])
                img.scale(max(1, round(img.size[0] * factor)), max(1, round(img.size[1] * factor)))
    bpy.ops.export_scene.gltf(filepath=out, export_format="GLB", use_selection=True, export_apply=True)
    sys.exit(0)

pts = [x.matrix_world @ mathutils.Vector(c) for x in keep for c in x.bound_box]
lo = mathutils.Vector([min(p[i] for p in pts) for i in range(3)])
hi = mathutils.Vector([max(p[i] for p in pts) for i in range(3)])
c = (lo + hi) / 2
height = hi.z - lo.z
cd = bpy.data.cameras.new("cam")
cd.lens = 85
cam = bpy.data.objects.new("cam", cd)
sc.collection.objects.link(cam)
cam.location = (c.x + height * 2.6, c.y, c.z)
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
sc.render.resolution_y = 1000 * scale
sc.render.image_settings.file_format = "PNG"
sc.render.use_file_extension = False
sc.render.filepath = out
bpy.ops.render.render(write_still=True)
