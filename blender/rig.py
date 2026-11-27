# The rigged GLB's pose clips and export. Takes Blender's modules as arguments so tests run without Blender.


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
        export_image_format="JPEG",
        export_jpeg_quality=85,
    )
