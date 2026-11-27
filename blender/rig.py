# The rigged GLB's pose clips and export. Takes Blender's modules as arguments so tests run without Blender.

import json
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


def apply_materials(slots, data, mo, read_material, done, missing, problems):
    # Ekur's material import of the selected objects, one material at a time: its own operator stops at the first missing JSON.
    for slot in slots:
        if slot.material and slot.material.node_tree:
            _apply_material(slot, Path(data), mo, read_material, done, missing, problems)


def _apply_material(slot, data, mo, read_material, done, missing, problems):
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
    try:
        mo.run_material(material, slot.material.node_tree)
    except Exception as e:
        problems.append(f"material {name}: {e!r}"[:200])


# A weapon model has a permutation per region for each build; the standard build is every region's "Default" permutation.
DEFAULT_PERMUTATION = 1120495519


def weapon_model(weapons_file, data, name):
    entry = next((w for w in json.loads(Path(weapons_file).read_text()) if w["name"] == name), None)
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


def hold(objects, armature, matrix, update):
    # The model's origin is its grip: parent it to the grip bone where the pose puts it, so it follows whatever pose plays.
    update()
    location, rotation, _ = (armature.matrix_world @ armature.pose.bones[WEAPON_BONE].matrix).decompose()
    hand = matrix.LocRotScale(location, rotation, (1, 1, 1))
    for x in objects:
        placed = hand @ x.matrix_world
        x.parent, x.parent_type, x.parent_bone = armature, "BONE", WEAPON_BONE
        update()
        x.matrix_world = placed
