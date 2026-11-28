import json
import re
from pathlib import Path

WEAPONS = json.loads((Path(__file__).resolve().parent.parent / "weapons.json").read_text())


def test_each_weapon_has_a_name_a_model_and_a_halo_core_type_or_none():
    for w in WEAPONS:
        assert w["name"] and w["model"], w
        assert w["coreType"] is None or re.fullmatch(r"Weapon[A-Za-z0-9]+", w["coreType"]), w


def test_names_and_models_are_unique_and_a_pose_tag_means_one_weapon():
    tags = [t for w in WEAPONS for t in w["tags"]]
    assert len({w["name"] for w in WEAPONS}) == len(WEAPONS)
    assert len({w["model"] for w in WEAPONS}) == len(WEAPONS)
    assert all(isinstance(t, int) for t in tags)
    assert len(set(tags)) == len(tags)


def test_stylelists_are_ids():
    assert all(isinstance(s, int) and s != -1 for w in WEAPONS for s in w["stylelists"])
