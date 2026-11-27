# The rigged GLB's pose clips and export. Takes Blender's modules as arguments so tests run without Blender.

import hashlib
import json
import math
import re
from pathlib import Path

def find_stance(data, stance_id):
    return next(iter(sorted((Path(data) / "stances").glob(f"anim_{stance_id}_*.ekuranim"))), None)

def read_clip(anim_data, anim_import, armature, path, name):
    clip = anim_data.AnimationData()
    with open(path, "rb") as fh:
        clip.read(fh)
    clip.frames, clip.total_frames = clip.frames[:1], 1
    return anim_import.import_animation(armature, clip, name)

def add_pose_clips(armature, clips, load):
    # One NLA track per pose, so the glTF exporter writes each as its own animation; the rig is left at rest.
    animation = armature.animation_data_create()
    for name, path in clips:
        action = load(path, name)
        track = animation.nla_tracks.new()
        track.name = name
        track.strips.new(name, 1, action)
        animation.action = None
    for bone in armature.pose.bones:
        bone.location = (0, 0, 0)
        bone.rotation_quaternion = (1, 0, 0, 0)
        bone.rotation_euler = (0, 0, 0)
        bone.rotation_axis_angle = (0, 0, 1, 0)
        bone.scale = (1, 1, 1)

def show_armature_modifiers(objects):
    for obj in objects:
        for modifier in obj.modifiers:
            if modifier.type == "ARMATURE":
                modifier.show_viewport = True

def export_glb(gltf_export, path, objects, armature):
    for obj in objects:
        obj.select_set(True)
    armature.select_set(True)
    gltf_export(
        filepath=path,
        export_format="GLB",
        use_selection=True,
        export_apply=True,
        export_skins=True,
        export_animations=True,
        export_animation_mode="NLA_TRACKS",
        # A weapon's visibility clip is one constant key; the exporter drops constant object channels unless told to keep them.
        export_optimize_animation_keep_anim_object=True,
        export_image_format="JPEG",
        export_jpeg_quality=85,
    )


def texture_size(full):
    # 24 parts x 4 maps at 1024 is ~500 MB on the GPU, which kills a phone's tab.
    return 2048 if full else 512


def has_light(rgba):
    return any(any(rgba[channel::4]) for channel in range(3))


# A weapon's model origin is its grip; the game's rig moves this bone to where the right hand holds it.
WEAPON_BONE = "b_r_grip"
# A held weapon's right_hand_spartan marker sits on the Spartan model's primary_weapon marker, which is on that bone
# (spartan_armor): position in Halo units, rotation x, y, z, w as Halo stores it.
SPARTAN_WEAPON_MARKER = ((-0.004, 0.004, -0.001), (-0.02, 0.029, 0.769, 0.638))
# The weapon markers that meet it, best first.
WEAPON_HAND_MARKERS = ("right_hand_spartan", "right_hand")
# Metres per Halo unit, as Ekur imports models.
UNIT = 3.048


def add_visibility_clips(objects, shown, new_action):
    # Per object one NLA track per clip, named like the rig's, so the exporter merges them: scaled to nothing where hidden.
    for obj in objects:
        animation = obj.animation_data_create()
        scale = tuple(obj.scale)
        for name, visible in shown.items():
            animation.action = new_action(f"{obj.name}-{name}")
            obj.scale = scale if visible else (0, 0, 0)
            obj.keyframe_insert(data_path="scale", frame=1)
            track = animation.nla_tracks.new()
            track.name = name
            track.strips.new(name, 1, animation.action)
            animation.action = None
        obj.scale = scale


def apply_materials(slots, data, mo, read_material, done, missing, problems, unknown=None):
    # Ekur's material import of the selected objects, one material at a time: its own operator stops at the first missing JSON.
    for slot in slots:
        if slot.material and slot.material.node_tree:
            _apply_material(slot, Path(data), mo, read_material, done, missing, problems, unknown)


def _apply_material(slot, data, mo, read_material, done, missing, problems, unknown):
    name = slot.name.split(".")[0]
    if name in done or name in missing:
        return
    path = data / "materials" / f"{name}.json"
    if not path.exists():
        missing.add(name)
        if re.fullmatch(r"-?\d+", name):
            problems.append(f"material {name}: no JSON in the data folder")
        return
    mo.remove_nodes(slot.material.node_tree)
    material = read_material(path)
    done.add(name)
    if material is None:
        problems.append(f"material {name}: unreadable JSON")
        return
    # Ekur's dump has no shader for these (plasma blades, holograms): Ekur builds nothing, so the caller draws them
    if unknown is not None and material.get("shader_type") == "Unknown":
        unknown.add(name)
    try:
        mo.run_material(material, slot.material.node_tree)
    except Exception as e:
        problems.append(f"material {name}: {e!r}"[:200])


# A weapon model has a permutation per region for each build; the standard build is every region's "Default" permutation.
DEFAULT_PERMUTATION = 1120495519


def weapon_entry(weapons_file, name):
    return next((w for w in json.loads(Path(weapons_file).read_text()) if w["name"] == name), None)


def weapon_model(weapons_file, data, name):
    entry = weapon_entry(weapons_file, name)
    model = Path(data) / "models" / f"{entry['model']}.ekur" if entry else None
    return model if model and model.exists() else None


def keep_default_build(meshes, permutation_of, remove, link):
    chosen = [x for x in meshes if permutation_of(x) == DEFAULT_PERMUTATION] or list(meshes)
    for x in meshes:
        if x not in chosen:
            remove(x)
        elif not x.users_collection:
            link(x)
    return chosen


def select_only(objects, chosen):
    for x in objects:
        x.select_set(False)
    for x in chosen:
        x.select_set(True)


def hold(objects, armature, matrix, update, offset, adjust=None):
    # The model's origin is its grip: parent it to the grip bone where the pose puts it, turned by the hand's weapon marker,
    # so it follows whatever pose plays.
    update()
    location, rotation, _ = (armature.matrix_world @ armature.pose.bones[WEAPON_BONE].matrix).decompose()
    hand = matrix.LocRotScale(location, rotation, (1, 1, 1)) @ offset
    if adjust:
        hand = adjust(hand)
    placed = [hand @ x.matrix_world for x in objects]
    for x in objects:
        x.parent, x.parent_type, x.parent_bone = armature, "BONE", WEAPON_BONE
    update()
    for x, world in zip(objects, placed):
        x.matrix_world = world


def own_materials(objects, others):
    # Ekur reuses a material by name, so a weapon can share one with the armor: give the weapon its own copy to coat.
    theirs = {id(s.material) for o in others for s in o.material_slots if s.material}
    for obj in objects:
        for slot in obj.material_slots:
            if slot.material and id(slot.material) in theirs:
                slot.material = slot.material.copy()


def bake_key(cache, scripts, kind, parts):
    # Baked textures depend on what is baked and on the scripts that built its materials, not on the stance.
    digest = hashlib.sha256()
    for script in scripts:
        digest.update(Path(script).read_bytes())
    digest.update(json.dumps([kind, parts], sort_keys=True).encode())
    return Path(cache) / digest.hexdigest()[:32]


# The off hand's grip bone, which a two-handed pose puts where the gun's foregrip belongs.
OFF_HAND_BONE = "b_l_grip"
# The weapon marker a Spartan's off hand holds.
FOREGRIP_MARKER = "left_hand_spartan"


def markers_of(model, place=None):
    # A model's markers by name: (position in metres, rotation x, y, z, w), from each one's first instance. A marker is
    # relative to its node; place(node, position, rotation) puts it in the model's space (the node's rest transform).
    out = {}
    for marker in model.markers:
        if marker.instances:
            first = marker.instances[0]
            position, rotation = tuple(first.position.vector), tuple(first.rotation.vector)
            if place:
                position, rotation = place(first.node_index, position, rotation)
            out[str(marker.name)] = (tuple(v * UNIT for v in position), tuple(rotation))
    return out


def _qmul(a, b):
    aw, ax, ay, az = a
    bw, bx, by, bz = b
    return (aw * bw - ax * bx - ay * by - az * bz, aw * bx + ax * bw + ay * bz - az * by,
            aw * by - ax * bz + ay * bw + az * bx, aw * bz + ax * by - ay * bx + az * bw)


def _rotate(q, v):
    w, x, y, z = _qmul(_qmul(q, (0.0, *v)), (q[0], -q[1], -q[2], -q[3]))
    return (x, y, z)


def _wxyz(xyzw):
    # Stored rounded, so made unit length again; an empty one is no turn
    x, y, z, w = xyzw
    n = (w * w + x * x + y * y + z * z) ** 0.5
    return (w / n, x / n, y / n, z / n) if n > 1e-9 else (1.0, 0.0, 0.0, 0.0)


def hand_marker(weapon_markers):
    # The weapon marker a Spartan's right hand holds: (position in metres, rotation x, y, z, w), at the origin when it has none.
    return next((weapon_markers[n] for n in WEAPON_HAND_MARKERS if n in weapon_markers), ((0, 0, 0), (0, 0, 0, 1)))


def hand_offset(weapon_markers):
    # Where the weapon sits relative to the grip bone: the Spartan's weapon marker, less the weapon's own hand marker.
    # Returns (translation, rotation w, x, y, z).
    position, rotation = SPARTAN_WEAPON_MARKER
    spartan_t, spartan_q = tuple(v * UNIT for v in position), _wxyz(rotation)
    hand = hand_marker(weapon_markers)
    hand_q = _wxyz(hand[1])
    q = _qmul(spartan_q, (hand_q[0], -hand_q[1], -hand_q[2], -hand_q[3]))
    moved = _rotate(q, hand[0])
    return tuple(a - b for a, b in zip(spartan_t, moved)), q


# How far the pose's off-hand grip may be from a gun's foregrip and still be holding it; further, the hold is one-handed.
TWO_HAND_REACH = 0.35
# A foregrip (or off hand) closer to the grip than this is no direction to turn by (a pistol's cupped hands).
MIN_ARM = 0.08
# A larger turn than this means the off hand is not on the foregrip, whatever the distance.
MAX_TURN_DEGREES = 60


def _sub(a, b):
    return tuple(x - y for x, y in zip(a, b))


def _unit(v):
    n = sum(x * x for x in v) ** 0.5
    return tuple(x / n for x in v)


def aim_rotation(source, target):
    # The rotation (rows of a 3x3) turning the direction of source onto that of target, about their common perpendicular.
    a, b = _unit(source), _unit(target)
    c = sum(x * y for x, y in zip(a, b))
    axis = (a[1] * b[2] - a[2] * b[1], a[2] * b[0] - a[0] * b[2], a[0] * b[1] - a[1] * b[0])
    s = sum(x * x for x in axis) ** 0.5
    if s < 1e-9:
        if c > 0:
            return ((1, 0, 0), (0, 1, 0), (0, 0, 1))
        # Opposite: half a turn about any axis perpendicular to them
        other = (0, 1, 0) if abs(a[0]) > 0.9 else (1, 0, 0)
        axis = _unit((a[1] * other[2] - a[2] * other[1], a[2] * other[0] - a[0] * other[2], a[0] * other[1] - a[1] * other[0]))
        s, c = 0.0, -1.0
    else:
        axis = tuple(x / s for x in axis)
    x, y, z = axis
    t = 1 - c
    return ((t * x * x + c, t * x * y - s * z, t * x * z + s * y),
            (t * x * y + s * z, t * y * y + c, t * y * z - s * x),
            (t * x * z - s * y, t * y * z + s * x, t * z * z + c))


def _length(v):
    return sum(x * x for x in v) ** 0.5


def off_hand_turn(grip, foregrip, off_grip, reach=TWO_HAND_REACH):
    # A two-handed hold: the turn about the grip that points the gun's foregrip at the pose's off-hand grip. None when one-handed.
    arm, target = _sub(foregrip, grip), _sub(off_grip, grip)
    if _length(_sub(foregrip, off_grip)) > reach or _length(arm) < MIN_ARM or _length(target) < MIN_ARM:
        return None
    cos = sum(a * b for a, b in zip(_unit(arm), _unit(target)))
    if cos < math.cos(math.radians(MAX_TURN_DEGREES)):
        return None
    return aim_rotation(arm, target)


def both_hands(hand, pivot, off_grip, foregrip, matrix):
    # The gun turned about its own grip point (pivot) so its foregrip (in the gun's space) points at the off hand's grip.
    if foregrip is None:
        return hand
    turn = off_hand_turn(tuple(pivot), tuple(hand @ foregrip), tuple(off_grip))
    if turn is None:
        return hand
    return matrix.Translation(pivot) @ matrix(turn).to_4x4() @ matrix.Translation(-pivot) @ hand
