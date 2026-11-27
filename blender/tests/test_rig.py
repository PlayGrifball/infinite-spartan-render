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

    assert selected == [("obj", True), ("armature", True)]
    assert exported[0]["filepath"] == "out.glb"
    assert exported[0]["export_skins"] is True
    assert exported[0]["export_animations"] is True
    assert exported[0]["export_animation_mode"] == "NLA_TRACKS"
