import json
from pathlib import Path

import pose_sheet

POSES = json.loads((Path(__file__).resolve().parent.parent / "weapon-poses.json").read_text())
WEAPONS = {w["name"] for w in json.loads((Path(__file__).resolve().parent.parent / "weapons.json").read_text())}


def test_every_listed_pose_holds_a_weapon_from_the_list():
    assert POSES
    assert all(p["weapon"] in WEAPONS and isinstance(p["stance"], int) and p["pose"] for p in POSES)


def test_a_pose_is_rendered_as_a_mark_vii_body_holding_its_weapon():
    loadout = next(pose_sheet.loadouts([{"weapon": "Needler", "stance": 5, "pose": "x"}]))

    assert loadout == {"Core": "Mark VII", "Stance": 5, "Framing": "body", "Output": "png", "Size": "small", "Weapon": "Needler"}


def test_each_render_runs_the_render_script_in_the_background():
    cmd = pose_sheet.command("/opt/blender/blender", Path("a.json"), Path("a.png"))

    assert cmd[:3] == ["/opt/blender/blender", "-b", "--factory-startup"]
    assert cmd[4].endswith("spartan.py") and cmd[-2:] == ["a.json", "a.png"]


def test_renders_fill_the_sheet_row_by_row():
    cells, size = pose_sheet.layout(8, columns=6)

    assert cells[:2] == [(0, 0), (1, 0)] and cells[6:] == [(0, 1), (1, 1)]
    assert size == (6, 2)
    assert pose_sheet.layout(3)[1] == (3, 1)


def test_the_legend_numbers_the_poses_in_sheet_order():
    assert pose_sheet.legend([{"weapon": "A", "pose": "P"}, {"weapon": "B", "pose": "Q"}]) == "1. A in P\n2. B in Q\n"


def test_the_model_holds_the_commando_in_noble_command_with_every_pose_as_a_clip():
    loadout = pose_sheet.model_loadout([{"pose": "A", "stance": 1, "weaponTag": None}, {"pose": "B", "stance": 2, "weaponTag": 5}])

    assert (loadout["Output"], loadout["Weapon"], loadout["Stance"]) == ("glb", "VK78 Commando Rifle", 1756613951)
    assert loadout["ExtraPoses"] == [1, 2]
