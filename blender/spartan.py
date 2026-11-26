# Run by Blender: blender -b --python spartan.py -- loadout.json output-path
import hashlib
import importlib
import os
import json
import re
import shutil
import sys
import time
from pathlib import Path

import addon_utils
import bpy
import mathutils

DATA = os.environ.get("ISR_DATA", "/data")
EKUR_ASSETS = "/opt/ekur/assets"
DEFAULT_STANCE = -1258174396
MOD = "bl_ext.user_default.ekur"
HASHED = re.compile(r"-?\d+_-?\d+_-?\d+(\.\d+)?")

_t0 = _last = time.monotonic()


def phase(name):
    global _last
    now = time.monotonic()
    print(f"ISR_PHASE {name} {now - _last:.2f}", flush=True)
    _last = now


loadout_path, out = sys.argv[sys.argv.index("--") + 1:][:2]
loadout = json.loads(Path(loadout_path).read_text())
core = loadout["Core"]
armor = loadout.get("Armor") or {}
coating = loadout.get("Coating") or ""
parts = {int(p["Region"]): int(p["Permutation"]) for p in (loadout.get("Parts") or [])}
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


def tool_version():
    manifest = Path(importlib.import_module(MOD).__file__).parent / "blender_manifest.toml"
    return bpy.app.version_string + "\n" + (manifest.read_text() if manifest.exists() else "")


def cached_import():
    cache = os.environ.get("ISR_IMPORT_CACHE")
    if not cache:
        return None
    key = hashlib.sha256("\n".join([core, tool_version()]).encode()).hexdigest()[:16]
    return Path(cache) / f"{re.sub(r'[^a-z0-9]+', '-', core.lower()).strip('-')}-{key}.blend"


def import_core():
    # Importing a core takes minutes and does not depend on the loadout or the stance: keep the imported scene.
    cached = cached_import()
    if cached:
        if cached.exists():
            bpy.ops.wm.open_mainfile(filepath=str(cached), load_ui=False)
            os.utime(cached)
            print("IMPORT reused", cached.name, flush=True)
            return
    o = bpy.context.scene.spartan_properties
    o.import_specific_core = True
    o.core = core
    o.use_purp_rig = False
    bpy.ops.ekur.importspartan()
    if cached:
        cached.parent.mkdir(parents=True, exist_ok=True)
        part = cached.with_name(f"{cached.name}.{os.getpid()}.part")
        bpy.ops.wm.save_as_mainfile(filepath=str(part), copy=True)
        os.replace(part, cached)
        print("IMPORT saved", cached.name, flush=True)


# A warm only makes sure the import is saved: when it is, touch it (so the sweep keeps it) and stop.
if output == "warm":
    saved = cached_import()
    if saved and saved.exists():
        os.utime(saved)
        phase("import_cached")
        Path(out).write_text("warm")
        sys.exit(0)
import_core()
sc = bpy.context.scene
phase("import")
if output == "warm":
    Path(out).write_text("warm")
    sys.exit(0)

def region_key(col):
    name = col.name.removeprefix("[REGION] ")
    return name.split("_", 1)[1] if "_" in name else name


PLACEHOLDER = re.compile(r"\d{3}-\d{3}-[a-z]+-[0-9a-f]{8}")


def default_permutation(objs, core):
    # Placeholder boxes (24 vertices, no material) sort before the real pieces; skip them.
    real = [x for x in objs if not HASHED.fullmatch(x.name) and not PLACEHOLDER.search(x.name)] or objs
    named = next((x for x in real if x.name.split(".")[0].lower().endswith("_" + core.lower())), None)
    return named or real[0]


def as_id(value):
    try:
        return int(value)
    except (TypeError, ValueError):
        return None


def listing():
    # Every mesh in the region and kit collections with its ids and the display name of its permutation.
    rp = json.loads(ext.read_text())
    display = {}
    for rid, region in rp.items():
        for pid, perm in (region.get("permutations") or {}).items():
            display[(str(rid), str(pid))] = perm.get("name")
    rows = []
    for col in bpy.data.collections:
        if not col.name.startswith(("[REGION]", "[KIT]")):
            continue
        kind, _, rest = col.name[1:].partition("] ")
        for x in col.objects:
            if x.type != "MESH":
                continue
            pid = x.get("permutation_name")
            want = display.get((str(x.get("region_name")), str(pid)))
            base = x.name.split(".")[0] if re.search(r"\.\d{3}$", x.name) else x.name
            rows.append({
                "collection": col.name, "kind": kind, "key": rest.split("_", 1)[1] if "_" in rest else rest,
                "object": x.name, "region": x.get("region_name"), "permutation": pid,
                "display": want, "hashed": bool(HASHED.fullmatch(x.name)),
                "nameMatches": bool(want) and base.endswith("_" + want),
                "truncated": len(x.name) >= 63,
            })
    return rows


if output == "names":
    Path(out).write_text(json.dumps(listing()))
    sys.exit(0)

if output == "catalog":
    # What this core can wear: per region (or kit region) the pieces, by the game's ids and, when it has one, a name.
    regions = {}
    for r in listing():
        region, permutation = as_id(r["region"]), as_id(r["permutation"])
        if region is None or permutation is None:
            continue
        g = regions.setdefault((r["kind"], r["key"]), {"kind": r["kind"], "key": r["key"], "region": region, "pieces": {}})
        g["pieces"].setdefault(permutation, {"permutation": permutation, "name": r["display"]})
    doc = {"core": core, "regions": [
        {**g, "pieces": sorted(g["pieces"].values(), key=lambda p: (p["name"] is None, p["name"] or "", p["permutation"]))}
        for g in sorted(regions.values(), key=lambda g: (g["kind"], g["key"]))]}
    Path(out).write_text(json.dumps(doc))
    sys.exit(0)


def prop_id(obj, name):
    return as_id(obj.get(name))


keep = []
matched_parts = set()
# Pieces by id: a kit's meshes can sit in a [KIT] collection as well as in a [REGION] one.
for col in bpy.data.collections:
    if not col.name.startswith(("[REGION]", "[KIT]")):
        continue
    for x in col.objects:
        r = prop_id(x, "region_name")
        if x.type == "MESH" and r in parts and prop_id(x, "permutation_name") == parts[r]:
            keep.append(x)
            matched_parts.add(r)
for col in bpy.data.collections:
    if not col.name.startswith("[REGION]"):
        continue
    # A part replaces the default piece of its own region only: other regions sharing the collection keep theirs.
    objs = sorted((x for x in col.objects if x.type == "MESH" and prop_id(x, "region_name") not in matched_parts), key=lambda x: x.name)
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

missing_parts = {r: p for r, p in parts.items() if r not in matched_parts}
if missing_parts:
    sys.exit(f"No mesh for parts (region: permutation): {missing_parts}")

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
phase("materials")
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
phase("pose")


def bake_folder(size):
    # The baked textures depend on the loadout, not the stance (a pose moves vertices, not UVs): keep them per loadout.
    cache = os.environ.get("ISR_BAKE_CACHE")
    if not cache:
        return None
    renderer = hashlib.sha256(Path(sys.argv[sys.argv.index("--python") + 1]).read_bytes()).hexdigest()
    ident = json.dumps([renderer, tool_version(), core, sorted(armor.items()), coating, size], sort_keys=True)
    return Path(cache) / hashlib.sha256(ident.encode()).hexdigest()[:32]


def bake_object(obj, passes, size, images):
    for x in bpy.data.objects:
        x.select_set(False)
    obj.select_set(True)
    bpy.context.view_layer.objects.active = obj
    obj.data.uv_layers.active_index = 0
    mats = [s.material for s in obj.material_slots if s.material]
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
        phase("bake")
        for m, node in temp:
            m.node_tree.nodes.remove(node)


def bake_to_plain_materials(size):
    # Ekur's shader graph is not glTF-exportable: bake what it shows into plain textures on a Principled BSDF.
    sc.render.engine = "CYCLES"
    sc.cycles.device = "CPU"
    sc.cycles.samples = 1
    sc.cycles.use_denoising = False
    passes = (("color", "DIFFUSE", "sRGB"), ("rough", "ROUGHNESS", "Non-Color"), ("normal", "NORMAL", "Non-Color"), ("emit", "EMIT", "sRGB"))
    folder = bake_folder(size)
    for obj in keep:
        files = {key: folder / f"{re.sub(r'[^A-Za-z0-9._-]+', '-', obj.name)}_{key}.png" for key, _, _ in passes} if folder else {}
        images = {}
        if files and all(f.exists() for f in files.values()):
            for key, _, space in passes:
                img = bpy.data.images.load(str(files[key]))
                img.colorspace_settings.name = space
                img.pack()
                images[key] = img
                os.utime(files[key])
            phase("bake_reused")
        else:
            bake_object(obj, passes, size, images)
            for key, img in images.items() if folder else ():
                folder.mkdir(parents=True, exist_ok=True)
                part = files[key].with_name(f"{files[key].name}.{os.getpid()}.part")
                img.filepath_raw = str(part)
                img.file_format = "PNG"
                img.save()
                os.replace(part, files[key])
                img.filepath_raw = str(files[key])
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
    phase("mesh")
    bake_to_plain_materials(2048 if full else 1024)
    for x in keep:
        x.select_set(True)
    bpy.ops.export_scene.gltf(filepath=out, export_format="GLB", use_selection=True, export_apply=True, export_image_format="JPEG", export_jpeg_quality=85)
    phase("export")
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
sc.render.film_transparent = True
sc.render.image_settings.file_format = "PNG"
sc.render.image_settings.color_mode = "RGBA"
sc.render.use_file_extension = False
sc.render.filepath = out
phase("scene")
bpy.ops.render.render(write_still=True)
phase("render")
