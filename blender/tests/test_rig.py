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


def test_a_weapon_is_keyed_into_each_clip_and_scaled_to_nothing_where_it_is_not_held():
    gun, animation, tracks, keys = fake_object("gun")

    rig.key_clip(gun, "neutral", False, lambda name: f"action-{name}")
    rig.key_clip(gun, "stance", True, lambda name: f"action-{name}")

    assert [t.name for t in tracks] == ["neutral", "stance"]
    assert [t.strips.calls for t in tracks] == [[("new", ("neutral", 1, "action-gun-neutral"), {})], [("new", ("stance", 1, "action-gun-stance"), {})]]
    assert [(a, p, s) for a, p, _, s in keys if p == "scale"] == [("action-gun-neutral", "scale", (0, 0, 0)), ("action-gun-stance", "scale", (0.3, 0.3, 0.3))]
    assert {p for _, p, _, _ in keys} == {"location", "rotation_quaternion", "scale"}, "where it is in that pose, not only whether"
    assert animation.action is None
    assert tuple(gun.scale) == (0.3, 0.3, 0.3)


def test_a_baked_objects_clips_are_muted_and_unmuted():
    tracks = [SimpleNamespace(mute=False), SimpleNamespace(mute=False)]
    gun = SimpleNamespace(animation_data=SimpleNamespace(nla_tracks=tracks))

    rig.mute_clips([gun, SimpleNamespace(animation_data=None)], True)
    muted = [t.mute for t in tracks]
    rig.mute_clips([gun], False)

    assert muted == [True, True] and [t.mute for t in tracks] == [False, False]


def test_the_all_poses_clips_name_each_pose_and_its_weapon():
    poses = [{"pose": "Noble Command", "stance": 1, "weaponTag": -40348340}, {"pose": "Wave", "stance": 2, "weaponTag": None}, {"pose": "Odd", "stance": 3, "weaponTag": 9}]
    weapons = [{"name": "VK78 Commando Rifle", "tags": [-40348340]}]

    assert rig.pose_clips(poses, weapons) == [("Noble Command", 1, "VK78 Commando Rifle"), ("Wave", 2, None), ("Odd", 3, None)]


def test_each_pose_is_announced_while_it_is_the_rigs_action():
    armature, animation, tracks, _ = fake_armature()
    seen = []

    def load(path, name):
        animation.action = f"action-{name}"
        return animation.action

    rig.add_pose_clips(armature, [("neutral", "n"), ("stance", "s")], load, lambda name: seen.append((name, animation.action)))

    assert seen == [("neutral", "action-neutral"), ("stance", "action-stance")]


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


def test_a_held_weapon_goes_on_the_grip_bone_where_the_pose_puts_it_moved_by_its_offset():
    armature = SimpleNamespace(matrix_world=M("arm"), pose=SimpleNamespace(bones={rig.WEAPON_BONE: SimpleNamespace(matrix=M("grip"))}))

    hand = rig.hand_matrix(armature, M, M("marker"))

    assert hand.name == "[loc(arm*grip),rot(arm*grip),(1, 1, 1)]*marker", "unscaled grip, then the weapon's offset from it"


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
    turn = rig.off_hand_turn(grip=(0, 0, 0), foregrip=(0.3, 0, 0), off_grip=(0.25, 0.1, 0.05))

    aimed = apply(turn, (0.3, 0, 0))
    length = (0.25 ** 2 + 0.1 ** 2 + 0.05 ** 2) ** 0.5
    assert close(aimed, (0.3 * 0.25 / length, 0.3 * 0.1 / length, 0.3 * 0.05 / length))


def test_a_one_handed_hold_leaves_the_gun_alone():
    assert rig.off_hand_turn(grip=(0, 0, 0), foregrip=(0.3, 0, 0), off_grip=(-0.5, 0.4, -0.6)) is None


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


def test_a_material_whose_shader_was_not_extracted_is_reported_so_it_can_be_drawn_some_other_way(tmp_path):
    (tmp_path / "materials").mkdir()
    for name in ("21", "22"):
        (tmp_path / "materials" / f"{name}.json").write_text("{}")
    reads = {"21": {"shader_type": "Unknown"}, "22": {"shader_type": "Layered"}}
    unknown = set()

    rig.apply_materials([slot("21"), slot("22")], tmp_path, FakeMaterials(), lambda p: reads[p.stem], set(), set(), [], unknown)

    assert unknown == {"21"}


def test_no_turn_for_a_foregrip_at_the_grip_or_a_turn_too_large_to_be_a_hold():
    assert rig.off_hand_turn(grip=(0, 0, 0), foregrip=(0.02, 0, 0), off_grip=(0.03, 0.01, 0)) is None, "cupped pistol hands"
    assert rig.off_hand_turn(grip=(0, 0, 0), foregrip=(0.3, 0, 0), off_grip=(0, 0, 0)) is None, "off hand at the grip"
    assert rig.off_hand_turn(grip=(0, 0, 0), foregrip=(0.2, 0, 0), off_grip=(0.05, 0.2, 0), reach=1) is None, "a 76 degree swing"


def test_an_empty_marker_rotation_is_no_turn():
    t, q = rig.hand_offset({"right_hand_spartan": ((0, 0, 0), (0, 0, 0, 0))})

    assert close(q, rig._wxyz(rig.SPARTAN_WEAPON_MARKER[1]))


def test_a_marker_is_placed_through_its_node():
    instance = SimpleNamespace(node_index=4, position=SimpleNamespace(vector=(0.1, 0, 0)), rotation=SimpleNamespace(vector=(0, 0, 0, 1)))
    model = SimpleNamespace(markers=[SimpleNamespace(name="left_hand_spartan", instances=[instance])])
    seen = []

    def place(node, position, rotation):
        seen.append(node)
        return (position[0] + 0.2, position[1], position[2]), rotation

    markers = rig.markers_of(model, place)

    assert seen == [4]
    assert close(markers["left_hand_spartan"][0], (0.3 * rig.UNIT, 0, 0))


class Vec(tuple):
    def __neg__(self):
        return Vec(-x for x in self)


class Mat:
    # A 4x4 stand-in for mathutils.Matrix, enough for both_hands
    def __init__(self, rows):
        self.rows = [list(r) for r in rows]

    def to_4x4(self):
        return Mat([r + [0] for r in self.rows] + [[0, 0, 0, 1]])

    @classmethod
    def Translation(cls, v):
        return cls([[1, 0, 0, v[0]], [0, 1, 0, v[1]], [0, 0, 1, v[2]], [0, 0, 0, 1]])

    def __matmul__(self, other):
        if isinstance(other, Mat):
            return Mat([[sum(self.rows[i][k] * other.rows[k][j] for k in range(4)) for j in range(4)] for i in range(4)])
        return Vec(sum(self.rows[i][k] * (list(other) + [1])[k] for k in range(4)) for i in range(3))


def test_both_hands_turns_the_gun_about_its_grip_point_onto_the_off_hand():
    hand = Mat.Translation((1, 0, 0))
    pivot = Vec((1, 0, 0))

    turned = rig.both_hands(hand, pivot, off_grip=(1.25, 0.1, 0), foregrip=(0.3, 0, 0), matrix=Mat)

    assert close(turned @ (0, 0, 0), (1, 0, 0)), "the grip stays put"
    aimed = turned @ (0.3, 0, 0)
    length = (0.25 ** 2 + 0.1 ** 2) ** 0.5
    assert close(aimed, (1 + 0.3 * 0.25 / length, 0.3 * 0.1 / length, 0))
    assert rig.both_hands(hand, pivot, (1.25, 0.1, 0), None, Mat) is hand


def test_a_hidden_weapon_gets_back_its_full_size_whatever_a_previous_clip_left_on_it():
    gun, _, _, keys = fake_object("gun", scale=(0, 0, 0))

    rig.key_clip(gun, "Wristshot", False, lambda name: name, full=(1, 1, 1))

    assert [s for _, p, _, s in keys if p == "scale"] == [(0, 0, 0)]
    assert tuple(gun.scale) == (1, 1, 1), "baked at its own size, not the scale a clip keyed"


def test_a_tag_listed_for_two_weapons_means_the_first():
    weapons = [{"name": "A", "tags": [5]}, {"name": "B", "tags": [5]}]

    assert rig.pose_clips([{"pose": "P", "stance": 1, "weaponTag": 5}], weapons) == [("P", 1, "A")]


def test_a_weapons_rest_is_its_own_transform_not_a_world_matrix_blender_has_not_updated():
    # Ekur gives each imported mesh its 3.048 scale on the object; its world matrix stays identity until the
    # view layer updates, so a rest read from it would hold the weapon at a third of its size
    class Matrix:
        def __init__(self, scale):
            self.scale = scale

        def copy(self):
            return Matrix(self.scale)

    part = SimpleNamespace(matrix_basis=Matrix(3.048), matrix_world=Matrix(1.0))

    rests = rig.weapon_rests([part])

    assert rests[part].scale == 3.048
    assert rests[part] is not part.matrix_basis


def piece(name):
    # A hashed region piece: <model>_<permutation>_<region>
    _, permutation, region = name.split("_")
    return SimpleNamespace(name=name, permutation=permutation, region=region)


def test_hashed_defaults_take_the_permutation_that_covers_most_regions_not_the_first_by_name():
    # Mark V [B]'s 2030886328 collection: a kit permutation sorts first but has only one of the three regions;
    # taking it would drop the neck pieces
    kit = piece("1035284876_-347764400_1250001557")
    chest, neck, collar = (piece(f"1035284876_649265451_{r}") for r in ("1250001557", "702769714", "1798246436"))
    other = piece("1035284876_1246348706_1250001557")

    chosen = rig.hashed_defaults([kit, other, chest, neck, collar], lambda x: x.permutation, lambda x: x.region)

    assert chosen == [chest, neck, collar]


def test_hashed_defaults_give_a_region_the_chosen_permutation_lacks_its_own_first_piece():
    a1, b1 = piece("1_10_1"), piece("1_10_2")
    c2, c3 = piece("1_30_3"), piece("1_20_3")

    chosen = rig.hashed_defaults([a1, b1, c2, c3], lambda x: x.permutation, lambda x: x.region)

    assert chosen == [a1, b1, c3]


def test_regions_without_a_mesh_are_the_ones_nothing_kept_covers():
    kept = [piece("1_10_1"), piece("1_10_2")]

    assert rig.regions_without_mesh({"1", "2", "3", "4"}, kept, lambda x: x.region) == ["3", "4"]
    assert rig.regions_without_mesh({"1"}, kept, lambda x: x.region) == []
