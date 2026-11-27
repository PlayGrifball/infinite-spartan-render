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


def test_the_spartans_weapon_marker_is_a_unit_quaternion_in_halo_order():
    x, y, z, w = rig.SPARTAN_WEAPON_MARKER[1]
    assert abs(w * w + x * x + y * y + z * z - 1) < 0.01


def test_a_weapon_whose_hand_marker_matches_the_spartans_sits_on_the_grip():
    position, rotation = rig.SPARTAN_WEAPON_MARKER
    same = {"right_hand_spartan": (tuple(v * rig.UNIT for v in position), rotation)}

    t, q = rig.hand_offset(same)

    assert close(t, (0, 0, 0)) and close(q, (1, 0, 0, 0))


def test_the_hand_offset_turns_by_the_spartans_marker_and_back_by_the_weapons(monkeypatch):
    quarter_z = (0, 0, 2 ** -0.5, 2 ** -0.5)
    monkeypatch.setattr(rig, "SPARTAN_WEAPON_MARKER", ((0, 0, 0), quarter_z))
    weapon = {"right_hand": ((1.0, 0, 0), (0, 0, 0, 1)), "other": ((9, 9, 9), (0, 0, 0, 1))}

    t, q = rig.hand_offset(weapon)

    assert close(q, (2 ** -0.5, 0, 0, 2 ** -0.5))
    assert close(t, (0, -1.0, 0)), "the weapon's hand marker, turned a quarter, is brought back onto the grip"
    assert close(rig.hand_offset({})[1], (2 ** -0.5, 0, 0, 2 ** -0.5)), "no hand marker: the Spartan's marker alone"


class FakeMaterials:
    def __init__(self, fail=()):
        self.removed, self.ran, self.fail = [], [], fail

    def remove_nodes(self, tree):
        self.removed.append(tree)

    def run_material(self, material, tree):
        if material in self.fail:
            raise ValueError("bad")
        self.ran.append((material, tree))


def slot(name, tree="tree"):
    return SimpleNamespace(name=name, material=SimpleNamespace(node_tree=tree) if tree else None)


def test_materials_are_applied_once_each_and_every_problem_is_named(tmp_path):
    (tmp_path / "materials").mkdir()
    for name in ("11", "12", "13"):
        (tmp_path / "materials" / f"{name}.json").write_text("{}")
    mo = FakeMaterials(fail=("m13",))
    done, missing, problems = set(), set(), []
    reads = {"11": "m11", "12": None, "13": "m13"}

    rig.apply_materials(
        [slot("11"), slot("11.001"), slot("12"), slot("13"), slot("99"), slot("Material"), slot("14", tree=None)],
        tmp_path, mo, lambda p: reads[p.stem], done, missing, problems)

    assert mo.ran == [("m11", "tree")]
    assert done == {"11", "12", "13"}
    assert missing == {"99", "Material"}
    assert problems == ["material 12: unreadable JSON", "material 13: ValueError('bad')", "material 99: no JSON in the data folder"]


def test_a_weapon_is_found_by_name_only_when_its_model_is_in_the_data(tmp_path):
    weapons = tmp_path / "weapons.json"
    weapons.write_text('[{"name": "VK78 Commando Rifle", "model": "commando"}, {"name": "Energy Sword", "model": "energy_sword"}]')
    (tmp_path / "models").mkdir()
    (tmp_path / "models" / "commando.ekur").write_bytes(b"x")

    assert rig.weapon_model(weapons, tmp_path, "VK78 Commando Rifle") == tmp_path / "models" / "commando.ekur"
    assert rig.weapon_model(weapons, tmp_path, "Energy Sword") is None
    assert rig.weapon_model(weapons, tmp_path, "Nope") is None


def mesh(name, permutation, linked=True):
    return SimpleNamespace(name=name, permutation=permutation, users_collection=["c"] if linked else [])


def test_the_default_build_is_kept_and_linked_and_the_other_builds_removed():
    default, other, loose = mesh("a", rig.DEFAULT_PERMUTATION), mesh("b", 5), mesh("c", rig.DEFAULT_PERMUTATION, linked=False)
    removed, linked = [], []

    kept = rig.keep_default_build([default, other, loose], lambda x: x.permutation, removed.append, linked.append)

    assert kept == [default, loose]
    assert removed == [other]
    assert linked == [loose]


def test_a_model_without_a_default_build_keeps_every_mesh():
    meshes = [mesh("a", 5), mesh("b", 6)]

    assert rig.keep_default_build(meshes, lambda x: x.permutation, lambda x: None, lambda x: None) == meshes


def test_select_only_selects_just_the_chosen():
    seen = {}
    objects = [SimpleNamespace(name=n, select_set=lambda v, n=n: seen.__setitem__(n, v)) for n in "abc"]

    rig.select_only(objects, objects[1:2])

    assert seen == {"a": False, "b": True, "c": False}


class M:
    # A stand-in for mathutils.Matrix: composes names, so the test reads the order of the products.
    def __init__(self, name):
        self.name = name

    def __matmul__(self, other):
        return M(f"{self.name}*{other.name}")

    def decompose(self):
        return (f"loc({self.name})", f"rot({self.name})", "scale")

    @staticmethod
    def LocRotScale(location, rotation, scale):
        return M(f"[{location},{rotation},{scale}]")


def test_a_weapon_is_parented_to_the_grip_bone_where_the_pose_puts_the_hand():
    updates = []
    armature = SimpleNamespace(matrix_world=M("arm"), pose=SimpleNamespace(bones={rig.WEAPON_BONE: SimpleNamespace(matrix=M("grip"))}))
    gun = SimpleNamespace(matrix_world=M("gun"), parent=None, parent_type=None, parent_bone=None)
    scope = SimpleNamespace(matrix_world=M("scope"), parent=None, parent_type=None, parent_bone=None)

    rig.hold([gun, scope], armature, M, lambda: updates.append(1), M("marker"))

    assert (gun.parent, gun.parent_type, gun.parent_bone) == (armature, "BONE", "b_r_grip")
    assert gun.matrix_world.name == "[loc(arm*grip),rot(arm*grip),(1, 1, 1)]*marker*gun", "the gun turned by the hand's weapon marker"
    assert scope.matrix_world.name == "[loc(arm*grip),rot(arm*grip),(1, 1, 1)]*marker*scope"
    assert len(updates) == 2, "once to read the hand, once after parenting every mesh"


class Material:
    def __init__(self, name):
        self.name = name

    def copy(self):
        return Material(self.name + ".001")


def test_a_weapon_gets_its_own_copy_of_a_material_the_armor_also_uses():
    shared, own = Material("7"), Material("8")
    armor = SimpleNamespace(material_slots=[SimpleNamespace(material=shared)])
    gun = SimpleNamespace(material_slots=[SimpleNamespace(material=shared), SimpleNamespace(material=own), SimpleNamespace(material=None)])

    rig.own_materials([gun], [armor])

    assert [s.material.name if s.material else None for s in gun.material_slots] == ["7.001", "8", None]
    assert armor.material_slots[0].material is shared


def test_a_bake_key_changes_with_the_script_files_and_with_what_is_baked(tmp_path):
    script, helpers = tmp_path / "spartan.py", tmp_path / "rig.py"
    script.write_text("a")
    helpers.write_text("b")
    key = rig.bake_key(tmp_path / "bakes", [script, helpers], "armor", ["Mark VII"])

    helpers.write_text("c")
    changed_helpers = rig.bake_key(tmp_path / "bakes", [script, helpers], "armor", ["Mark VII"])
    other_parts = rig.bake_key(tmp_path / "bakes", [script, helpers], "armor", ["Yoroi"])
    weapon = rig.bake_key(tmp_path / "bakes", [script, helpers], "weapon", ["Mark VII"])

    assert key.parent == tmp_path / "bakes"
    assert len({key, changed_helpers, other_parts, weapon}) == 4


def test_a_weapons_entry_is_found_by_name(tmp_path):
    weapons = tmp_path / "weapons.json"
    weapons.write_text('[{"name": "A", "model": "a"}]')

    assert rig.weapon_entry(weapons, "A") == {"name": "A", "model": "a"}
    assert rig.weapon_entry(weapons, "B") is None


def apply(rows, v):
    return tuple(sum(r * x for r, x in zip(row, v)) for row in rows)


def close(a, b):
    return all(abs(x - y) < 1e-6 for x, y in zip(a, b))


def test_aim_rotation_turns_one_direction_onto_another():
    quarter = rig.aim_rotation((1, 0, 0), (0, 2, 0))
    assert close(apply(quarter, (1, 0, 0)), (0, 1, 0))
    assert close(apply(quarter, (0, 0, 1)), (0, 0, 1)), "about their common perpendicular, so nothing else turns"
    assert close(apply(rig.aim_rotation((0, 0, 3), (0, 0, 1)), (5, 6, 7)), (5, 6, 7))
    assert close(apply(rig.aim_rotation((1, 0, 0), (-1, 0, 0)), (1, 0, 0)), (-1, 0, 0))


def test_a_two_handed_hold_turns_the_gun_so_its_foregrip_points_at_the_off_hand():
    turn = rig.off_hand_turn(grip=(0, 0, 0), foregrip=(0.3, 0, 0), off_grip=(0, 0.3, 0.05), reach=0.5)

    aimed = apply(turn, (0.3, 0, 0))
    length = (0.3 ** 2 + 0.05 ** 2) ** 0.5
    assert close(aimed, (0, 0.3 * 0.3 / length, 0.3 * 0.05 / length))


def test_a_one_handed_hold_leaves_the_gun_alone():
    assert rig.off_hand_turn(grip=(0, 0, 0), foregrip=(0.3, 0, 0), off_grip=(-0.5, 0.4, -0.6)) is None


def test_hold_lets_the_caller_adjust_the_hand_before_parenting():
    armature = SimpleNamespace(matrix_world=M("arm"), pose=SimpleNamespace(bones={rig.WEAPON_BONE: SimpleNamespace(matrix=M("grip"))}))
    gun = SimpleNamespace(matrix_world=M("gun"), parent=None, parent_type=None, parent_bone=None)

    rig.hold([gun], armature, M, lambda: None, M("marker"), adjust=lambda hand: M(f"turned({hand.name})"))

    assert gun.matrix_world.name == "turned([loc(arm*grip),rot(arm*grip),(1, 1, 1)]*marker)*gun"


def test_a_models_markers_are_read_in_metres_from_their_first_instance():
    def instance(p, r):
        return SimpleNamespace(position=SimpleNamespace(vector=p), rotation=SimpleNamespace(vector=r))
    model = SimpleNamespace(markers=[
        SimpleNamespace(name="left_hand_spartan", instances=[instance((0.1, 0, 0), (0, 0, 0, 1)), instance((5, 5, 5), (0, 0, 0, 1))]),
        SimpleNamespace(name="empty", instances=[]),
    ])

    markers = rig.markers_of(model)

    assert list(markers) == ["left_hand_spartan"]
    assert close(markers["left_hand_spartan"][0], (0.1 * rig.UNIT, 0, 0))
