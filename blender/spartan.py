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

sys.path.insert(0, str(Path(sys.argv[sys.argv.index("--python") + 1]).resolve().parent))
import rig  # noqa: E402

DATA = os.environ.get("ISR_DATA", "/data")
WEAPONS = Path(os.environ.get("ISR_WEAPONS") or Path(sys.argv[sys.argv.index("--python") + 1]).resolve().parent / "weapons.json")
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
visor = loadout.get("Visor") or ""
disable_damage = bool(loadout.get("DisableDamage"))
parts = {int(p["Region"]): int(p["Permutation"]) for p in (loadout.get("Parts") or [])}
output = loadout.get("Output", "png")
full = loadout.get("Size") == "full"
stance_id = loadout.get("Stance")
if stance_id is None:
    stance_id = DEFAULT_STANCE
framing = loadout.get("Framing", "body")
weapon = loadout.get("Weapon") or ""
weapon_coating = loadout.get("WeaponCoating") or ""

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
rig.select_only(bpy.data.objects, keep)

mp = sc.material_properties
mp.selected_only = True
mp.disable_damage = disable_damage
if coating:
    mp.use_default_coating = False
    mp.coating_id = coating
if visor:
    # Ekur lists visors from a file in its extension folder (read-only here), so fill the list it caches from the same names
    options = importlib.import_module(MOD + ".src.ui.material_options")
    names = sorted(json.loads(Path(f"{EKUR_ASSETS}/all_visors.json").read_text()), key=options.natural_sort_key)
    options.visor_cache = [(n, n, "") for n in names]
    mp.override_visor = True
    mp.visor = visor
    # The material code reads the same file from the extension folder too and, when it is missing, silently keeps the coating's own visor
    visor_shader = importlib.import_module(MOD + ".src.material_types.layered_shader")
    read_ekur_json = visor_shader.read_json_file
    visor_shader.read_json_file = lambda path, T: read_ekur_json(
        Path(f"{EKUR_ASSETS}/all_visors.json") if Path(path).name == "all_visors.json" else path, T)

mo = importlib.import_module(MOD + ".src.operators.material_operator")
ju = importlib.import_module(MOD + ".src.utils")
jd = importlib.import_module(MOD + ".src.json_definitions")
done = set()
missing = set()
problems = []


def apply_materials(applied, unknown=None):
    rig.apply_materials(mo.get_materials(), DATA, mo, lambda p: ju.read_json_file(p, jd.CommonMaterial), applied, missing, problems, unknown)


def glow(material):
    # A shader Ekur's dump lacks (energy blades, holograms) would render as an empty black material: draw it as a pale plasma glow
    tree = material.node_tree
    tree.nodes.clear()
    out = tree.nodes.new("ShaderNodeOutputMaterial")
    emit = tree.nodes.new("ShaderNodeEmission")
    emit.inputs["Color"].default_value = (0.45, 0.75, 1.0, 1.0)
    emit.inputs["Strength"].default_value = 1.0
    tree.links.new(emit.outputs["Emission"], out.inputs["Surface"])


apply_materials(done)

weapon_objs = []
foregrip = None
weapon_markers = {}
if weapon:
    model = rig.weapon_model(WEAPONS, DATA, weapon)
    if model is None:
        sys.exit(f"No model for weapon '{weapon}' in the data folder")
    importer = importlib.import_module(MOD + ".src.model.importer.model_importer").ModelImporter()
    meshes = [x for x in importer.start_import(str(model), bones=False) if x.type == "MESH"]
    nodes = importlib.import_module(MOD + ".src.model.importer.bone").get_bone_transforms(importer.model) if importer.model.bones else []

    def place(node, position, rotation):
        # A marker is relative to its node (a pump or a hammer head may hold the foregrip): put it in the model's space
        if node >= len(nodes):
            return position, rotation
        x, y, z, w = rotation
        local = mathutils.Matrix.LocRotScale(position, mathutils.Quaternion((w, x, y, z)), (1, 1, 1))
        loc, rot, _ = (nodes[node] @ local).decompose()
        return tuple(loc), (rot.x, rot.y, rot.z, rot.w)

    weapon_markers = rig.markers_of(importer.model, place)
    foregrip = weapon_markers.get(rig.FOREGRIP_MARKER, (None,))[0]
    weapon_objs = rig.keep_default_build(meshes, lambda x: prop_id(x, "permutation_name"), lambda x: bpy.data.objects.remove(x, do_unlink=True), sc.collection.objects.link)
    rig.own_materials(weapon_objs, keep)
    rig.select_only(bpy.data.objects, weapon_objs)
    mp.use_default_coating = not weapon_coating
    mp.coating_id = weapon_coating
    mp.override_visor = False
    weapon_done, weapon_unknown = set(), set()
    apply_materials(weapon_done, weapon_unknown)
    for material in {s.material for x in weapon_objs for s in x.material_slots if s.material and s.material.name.split(".")[0] in weapon_unknown}:
        glow(material)
    done |= weapon_done
    print("WEAPON", weapon, model.name, len(weapon_objs), "meshes", flush=True)
print("MATS", len(done), "missing", sorted(missing))
phase("materials")
if problems:
    sys.exit("Materials failed, not rendering a degraded Spartan: " + "; ".join(problems))

stance_path = rig.find_stance(DATA, stance_id)
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


def load_clip(path, name):
    return rig.read_clip(anim_data, anim_import, armature, path, name)


if output == "glb":
    neutral_path = rig.find_stance(DATA, DEFAULT_STANCE)
    if neutral_path is None:
        sys.exit(f"The default stance {DEFAULT_STANCE} is missing from the data folder")
    rig.add_pose_clips(armature, (("neutral", neutral_path), ("stance", stance_path)), load_clip)
else:
    load_clip(stance_path, "stance")
sc.frame_set(1)


def both_hands(hand):
    # Turned about the weapon's own grip point, so a two-handed pose puts the foregrip in the off hand
    pivot = hand @ mathutils.Vector(rig.hand_marker(weapon_markers)[0])
    off_grip = armature.matrix_world @ armature.pose.bones[rig.OFF_HAND_BONE].head
    return rig.both_hands(hand, pivot, off_grip, mathutils.Vector(foregrip) if foregrip else None, mathutils.Matrix)


if weapon_objs:
    offset, turn = rig.hand_offset(weapon_markers)
    rig.hold(weapon_objs, armature, mathutils.Matrix, bpy.context.view_layer.update,
             mathutils.Matrix.LocRotScale(offset, mathutils.Quaternion(turn), (1, 1, 1)), both_hands)
    if output == "glb":
        rig.add_visibility_clips(weapon_objs, {"neutral": False, "stance": True}, bpy.data.actions.new)
    keep += weapon_objs
phase("pose")


SCRIPTS = [Path(sys.argv[sys.argv.index("--python") + 1]).resolve(), Path(sys.argv[sys.argv.index("--python") + 1]).resolve().parent / "rig.py"]


def bake_folder(size, held):
    # The baked textures depend on the loadout, not the stance (a pose moves vertices, not UVs): the armor's per armor, a held weapon's per weapon.
    cache = os.environ.get("ISR_BAKE_CACHE")
    if not cache:
        return None
    if held:
        return rig.bake_key(cache, SCRIPTS, "weapon", [tool_version(), rig.weapon_entry(WEAPONS, weapon), weapon_coating, disable_damage, size])
    return rig.bake_key(cache, SCRIPTS, "armor", [tool_version(), core, sorted(armor.items()), sorted(parts.items()), coating, visor, disable_damage, size])


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
    for obj in keep:
        folder = bake_folder(size, obj in weapon_objs)
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
        pixels = [0.0] * len(images["emit"].pixels)
        images["emit"].pixels.foreach_get(pixels)
        if rig.has_light(pixels):
            nt.links.new(tex["emit"].outputs["Color"], bsdf.inputs["Emission Color"])
            bsdf.inputs["Emission Strength"].default_value = 1.0
        else:
            nt.nodes.remove(tex["emit"])
        obj.data.materials.clear()
        obj.data.materials.append(plain)
        obj.data.polygons.foreach_set("material_index", [0] * len(obj.data.polygons))
        print("BAKED", obj.name, flush=True)


if output == "glb":
    rig.show_armature_modifiers(keep)
    bpy.context.view_layer.update()
    phase("mesh")
    bake_to_plain_materials(rig.texture_size(full))
    rig.export_glb(bpy.ops.export_scene.gltf, out, keep, armature)
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
