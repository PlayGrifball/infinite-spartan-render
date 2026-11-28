# Run by Blender: blender -b --python pose_sheet.py (writes poses/ at the repository root)
# Renders each weapon of weapon-poses.json in a pose that holds it, then lays the renders out in one sheet (sheet.png)
# with a numbered legend (sheet.md), to check by eye that every weapon sits in the hands; then one model (all-poses.glb) with
# every pose of poses.json as a clip, to scrub through in a glTF viewer.
import json
import os
import subprocess
from pathlib import Path

HERE = Path(__file__).resolve().parent
OUT = HERE.parent / "poses"
COLUMNS = 6
CELL = (350, 500)


def loadouts(poses):
    for pose in poses:
        yield {"Core": "Mark VII", "Stance": pose["stance"], "Framing": "body", "Output": "png", "Size": "small", "Weapon": pose["weapon"]}


def model_loadout(all_poses):
    # The Commando in Noble Command, and every listed pose as a clip with its own weapon
    return {"Core": "Mark VII", "Stance": 1756613951, "Output": "glb", "Size": "small", "Weapon": "VK78 Commando Rifle",
            "ExtraPoses": [p["stance"] for p in all_poses]}


def command(blender, loadout_path, png):
    # With the system env, as the service runs it: PYTHONPYCACHEPREFIX keeps bytecode off a read-only root
    return [blender, "-b", "--factory-startup", "--python-use-system-env", "--python", str(HERE / "spartan.py"), "--",
            str(loadout_path), str(png)]


def layout(count, columns=COLUMNS):
    # Each render's cell (column, row from the top), and the sheet's size in cells
    return [(i % columns, i // columns) for i in range(count)], (min(count, columns), (count + columns - 1) // columns)


def legend(poses):
    return "\n".join(f"{i + 1}. {pose['weapon']} in {pose['pose']}" for i, pose in enumerate(poses)) + "\n"


def compose(pngs, out):
    import bpy
    import numpy as np

    cells, (columns, rows) = layout(len(pngs))
    w, h = CELL
    canvas = np.zeros((rows * h, columns * w, 4), dtype=np.float32)
    canvas[..., :3], canvas[..., 3] = 0.18, 1
    for png, (column, row) in zip(pngs, cells):
        if not png.exists():
            continue
        image = bpy.data.images.load(str(png))
        image.scale(w, h)
        pixels = np.array(image.pixels[:], dtype=np.float32).reshape(h, w, 4)
        cell = canvas[(rows - 1 - row) * h:(rows - row) * h, column * w:(column + 1) * w]
        alpha = pixels[..., 3:4]
        cell[..., :3] = pixels[..., :3] * alpha + cell[..., :3] * (1 - alpha)
    sheet = bpy.data.images.new("sheet", columns * w, rows * h, alpha=True)
    sheet.pixels = canvas.ravel().tolist()
    sheet.filepath_raw, sheet.file_format = str(out), "PNG"
    sheet.save()


def report(results):
    # One line per render (label, exit code, stdout, stderr), with Blender's own reason for a failure; 1 when any failed
    lines = [f"{label}: ok" if code == 0 else f"{label}: failed: {(err or out).strip()[-300:]}" for label, code, out, err in results]
    return lines, int(any(code != 0 for _, code, _, _ in results))


def main():
    import bpy

    out = OUT
    out.mkdir(parents=True, exist_ok=True)
    poses = json.loads((HERE / "weapon-poses.json").read_text())
    results, pngs = [], []

    def run(label, loadout, path):
        loadout_path = path.with_suffix(".json")
        loadout_path.write_text(json.dumps(loadout))
        done = subprocess.run(command(bpy.app.binary_path, loadout_path, path), env=os.environ, capture_output=True, text=True)
        results.append((label, done.returncode, done.stdout, done.stderr))
        print(report(results[-1:])[0][0], flush=True)

    for i, loadout in enumerate(loadouts(poses)):
        png = out / f"{i + 1:02d}.png"
        run(f"{i + 1:02d} {loadout['Weapon']}", loadout, png)
        pngs.append(png)
    compose(pngs, out / "sheet.png")
    (out / "sheet.md").write_text(legend(poses))
    run("all poses", model_loadout(json.loads((HERE / "poses.json").read_text())), out / "all-poses.glb")
    return report(results)[1]


if __name__ == "__main__":
    raise SystemExit(main())
