from types import SimpleNamespace

import rig


class Recorder:
    def __init__(self):
        self.calls = []

    def __getattr__(self, name):
        return lambda *a, **k: self.calls.append((name, a, k))


def fake_armature(bone_count=2):
    tracks = []

    def new_track():
        track = SimpleNamespace(name=None, strips=Recorder())
        tracks.append(track)
        return track

    animation = SimpleNamespace(action="x", nla_tracks=SimpleNamespace(new=new_track))
    bones = [SimpleNamespace(location=(1, 2, 3), rotation_quaternion=(0, 1, 0, 0), scale=(2, 2, 2)) for _ in range(bone_count)]
    armature = SimpleNamespace(animation_data_create=lambda: animation, pose=SimpleNamespace(bones=bones))
    return armature, animation, tracks, bones


def test_each_pose_becomes_a_named_track_and_the_rig_is_left_at_rest():
    armature, animation, tracks, bones = fake_armature()
    loaded = []

    def load(path, name):
        loaded.append((path, name))
        return f"action-{name}"

    rig.add_pose_clips(armature, [("neutral", "n.anim"), ("stance", "s.anim")], load)

    assert loaded == [("n.anim", "neutral"), ("s.anim", "stance")]
    assert [t.name for t in tracks] == ["neutral", "stance"]
    assert [t.strips.calls for t in tracks] == [[("new", ("neutral", 1, "action-neutral"), {})], [("new", ("stance", 1, "action-stance"), {})]]
    assert animation.action is None
    assert all(b.location == (0, 0, 0) and b.rotation_quaternion == (1, 0, 0, 0) and b.scale == (1, 1, 1) for b in bones)


def test_read_clip_keeps_only_the_first_frame(tmp_path):
    path = tmp_path / "pose.ekuranim"
    path.write_bytes(b"data")
    seen = {}

    class Clip:
        def read(self, fh):
            seen["bytes"] = fh.read()
            self.frames, self.total_frames = ["a", "b", "c"], 3

    imported = []
    anim_data = SimpleNamespace(AnimationData=Clip)
    anim_import = SimpleNamespace(import_animation=lambda armature, clip, name: imported.append((armature, clip.frames, clip.total_frames, name)) or "action")

    assert rig.read_clip(anim_data, anim_import, "arm", str(path), "stance") == "action"
    assert seen["bytes"] == b"data"
    assert imported == [("arm", ["a"], 1, "stance")]


def test_only_armature_modifiers_are_shown():
    armature_modifier = SimpleNamespace(type="ARMATURE", show_viewport=False)
    other = SimpleNamespace(type="SUBSURF", show_viewport=False)

    rig.show_armature_modifiers([SimpleNamespace(modifiers=[armature_modifier, other])])

    assert armature_modifier.show_viewport is True
    assert other.show_viewport is False


def test_export_selects_everything_and_writes_a_rigged_glb_with_nla_clips():
    exported = []
    selected = []
    obj = SimpleNamespace(select_set=lambda v: selected.append(("obj", v)))
    armature = SimpleNamespace(select_set=lambda v: selected.append(("armature", v)))

    rig.export_glb(lambda **k: exported.append(k), "out.glb", [obj], armature)
    (options,) = exported

    assert selected == [("obj", True), ("armature", True)]
    assert options["filepath"] == "out.glb"
    assert options["export_skins"] is True
    assert options["export_animations"] is True
    assert options["export_animation_mode"] == "NLA_TRACKS"
    assert options["export_optimize_animation_keep_anim_object"] is True, "a weapon's one-key visibility clips are constant, and dropped otherwise"


def test_find_stance_returns_the_first_matching_file_and_none_when_there_is_none(tmp_path):
    stances = tmp_path / "stances"
    stances.mkdir()
    (stances / "anim_7_b.ekuranim").write_bytes(b"")
    (stances / "anim_7_a.ekuranim").write_bytes(b"")

    assert rig.find_stance(tmp_path, 7) == stances / "anim_7_a.ekuranim"
    assert rig.find_stance(tmp_path, 8) is None


def test_the_rest_pose_also_clears_euler_and_axis_angle_rotation():
    armature, _, _, bones = fake_armature(1)
    bones[0].rotation_euler = (1, 2, 3)
    bones[0].rotation_axis_angle = (1, 0, 0, 1)

    rig.add_pose_clips(armature, [], lambda path, name: None)

    assert bones[0].rotation_euler == (0, 0, 0)
    assert bones[0].rotation_axis_angle == (0, 0, 1, 0)


def test_glb_textures_are_small_enough_for_a_phone_unless_full():
    assert rig.texture_size(False) == 512
    assert rig.texture_size(True) == 2048


def test_an_emission_map_with_no_light_in_it_is_skipped():
    black = [0.0, 0.0, 0.0, 1.0] * 4
    glowing = [0.0, 0.0, 0.0, 1.0] * 3 + [0.0, 0.2, 0.0, 1.0]
    assert not rig.has_light(black)
    assert rig.has_light(glowing)


def fake_object(name, scale=(0.3, 0.3, 0.3)):
    tracks = []

    def new_track():
        track = SimpleNamespace(name=None, strips=Recorder())
        tracks.append(track)
        return track

    keys = []
    animation = SimpleNamespace(action=None, nla_tracks=SimpleNamespace(new=new_track))
    obj = SimpleNamespace(name=name, scale=scale, animation_data_create=lambda: animation)
    obj.keyframe_insert = lambda data_path, frame: keys.append((animation.action, data_path, frame, tuple(obj.scale)))
    return obj, animation, tracks, keys


def test_a_weapon_is_scaled_to_nothing_in_the_clips_it_is_hidden_in_and_keeps_its_scale():
    gun, animation, tracks, keys = fake_object("gun")

    rig.add_visibility_clips([gun], {"neutral": False, "stance": True}, lambda name: f"action-{name}")

    assert [t.name for t in tracks] == ["neutral", "stance"]
    assert [t.strips.calls for t in tracks] == [[("new", ("neutral", 1, "action-gun-neutral"), {})], [("new", ("stance", 1, "action-gun-stance"), {})]]
    assert keys == [("action-gun-neutral", "scale", 1, (0, 0, 0)), ("action-gun-stance", "scale", 1, (0.3, 0.3, 0.3))]
    assert animation.action is None
    assert tuple(gun.scale) == (0.3, 0.3, 0.3)


def test_a_weapon_is_held_on_the_right_hand_grip():
    assert rig.WEAPON_BONE == "b_r_grip"
