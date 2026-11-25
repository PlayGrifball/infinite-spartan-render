# Infinite Spartan Render

A small web API that renders a Halo Infinite Spartan, or exports it as a 3D model, from a loadout. It runs
[Ekur](https://github.com/TheHaloArchive/ekur) inside headless Blender 5.0.1 (Cycles, CPU) and caches every result.

The game data (models, materials, bitmaps) is not in this repository or image: mount an Ekur data folder read-only at
`/data`, and a writable volume at `/cache`.

## API

A render takes minutes, so requests are jobs.

```
POST /spartans
{
  "core": "Mark VII",
  "armor": { "helmet": "<permutation name>", "<region>": "<permutation name>" },
  "coating": "1120495519",
  "output": "png",     // png | glb
  "size": "small"      // small | full
}
-> 202 { "id": "...", "state": "queued", "output": "png" }

GET /spartans/{id}        -> { "id", "state": queued|running|done|failed, "error" }
GET /spartans/{id}/file   -> the PNG or GLB (404 until done)
```

The id is a hash of the loadout, so asking again returns the cached file. A region left out of `armor` gets the core's
default piece; `coating` left out gets the default coating.

| size  | png                           | glb                  |
|-------|-------------------------------|----------------------|
| small | 700x1000, 48 samples          | textures at most 1024 |
| full  | 2100x3000, 128 samples        | original textures    |

## Settings (`Render:` section / `Render__X` environment)

`BlenderPath`, `ScriptPath`, `CacheDirectory` (`/cache`), `WorkDirectory` (`/tmp/render`), `MaxConcurrentJobs` (1; a
render needs several GB of RAM), `TimeoutMinutes` (15).

## Not done yet

- GLB output is untested against real data: Blender's glTF exporter only reads Principled shaders, and Ekur's layered
  materials may need baking first.
- No listing of the pieces and coatings a core has, and no gamertag input.
- Only the default pose and camera.

Licensed MIT; Ekur is GPL-3.0 and is fetched at image build time.
