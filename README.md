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
  "size": "small",     // small | full
  "framing": "body",   // body | head (head frames the helmet)
  "stance": 39388885   // optional StanceId of a player's action pose; left out = the default relaxed stance
}
-> 202 { "id": "...", "state": "queued", "output": "png" }

GET /spartans/{id}        -> { "id", "state": queued|running|done|failed, "error" }
GET /spartans/{id}/file   -> the PNG or GLB (404 until done)
```

A whole Spartan, in a chosen stance and in the neutral one, is one request:

```
POST /spartan-sets
{ "core": "Mark VII", "armor": {}, "coating": "1120495519", "stance": 39388885, "size": "small" }   // stance is required
-> 202 { "id", "state", "items": [ { "name", "state", "output", "url" } ] }

GET /spartan-sets/{id}    -> the same; state is the worst of the six: failed, else done when all are, else running or queued
```

The six items are `stance-glb`, `stance-body`, `stance-head`, `neutral-glb`, `neutral-body` and `neutral-head`; each
`url` is a `/spartans/{id}/file`. They are ordinary jobs (the same cache, queue and limits), so a set whose pieces were
rendered before costs nothing, and a GLB takes minutes. Sets, like jobs, do not survive a restart: post again.

The id is a hash of the loadout and of the Blender script, so asking again returns the cached file, and a changed script never serves files an older one made. A region left out of `armor` gets the core's
default piece; `coating` left out gets the default coating.

| size  | png                    | glb                       |
|-------|------------------------|---------------------------|
| small | 700x1000, 48 samples   | textures at most 1024 px  |
| full  | 2100x3000, 128 samples | original textures         |

## Settings (`Render:` section / `Render__X` environment)

`BlenderPath`, `ScriptPath`, `CacheDirectory` (`/cache`), `WorkDirectory` (`/tmp/render`), `MaxConcurrentJobs` (1; a
render needs several GB of RAM), `MaxQueued` (20; more gets 503), `SubmitsPerMinute` (60 per address; more gets 429),
`CacheDays` (30; older results are deleted), `TimeoutSeconds` (1800), `Cores`.

Jobs are held in memory: after a restart an unfinished job answers 404 and the client posts the loadout again (finished
results are still in the cache). A render that cannot load all its materials fails rather than being cached degraded.

## Not done yet

- GLB output is untested against real data: Blender's glTF exporter only reads Principled shaders, and Ekur's layered
  materials may need baking first.
- No listing of the pieces and coatings a core has, and no gamertag input.
- Only the default pose and camera.

Licensed MIT; Ekur is GPL-3.0 and is fetched at image build time.

## Operations

- **Swagger**: `/swagger` (OpenAPI at `/swagger/v1/swagger.json`).
- **Health**: `/health/live` runs no checks; `/health/ready` checks that Blender, the mounted data (`Render:DataDirectory`) and a writable cache are there.
- **Telemetry**: metrics and traces go out over OTLP only, configured by the standard `OTEL_*` variables and on when `OTEL_EXPORTER_OTLP_ENDPOINT` is set. Metrics: `spartan_render.jobs` (by `outcome`: done, failed, cancelled, cached, rejected), `spartan_render.duration` (seconds), `spartan_render.queue_depth`, `spartan_render.phase_duration` (seconds, by `phase`: import, materials, pose, mesh, bake, export, scene, render, and `output`; a bake is one sample per mesh and texture, so sum it); the same stages are events on the `render` span; each render is one `render` span.
- **Client address**: behind a proxy, set `ForwardedHeaders:KnownIPNetworks` (or `KnownProxies`, `ForwardedForHeaderName`) so the submit rate limit counts real clients.
- **Helm chart**: `charts/infinite-spartan-render` (one replica, Recreate, read-only root, the game data PVC mounted read-only at `/data`, a cache PVC, NetworkPolicy, Traefik IngressRoute, PrometheusRule). CI lints, unit-tests and schema-checks it.

The stance is a Halo animation: a player's `actionPosePath` item (gateway `gamecms/item`) carries a `StanceId`, and
the file `anim_<StanceId>_*.ekuranim` must be in `<data folder>/stances/`. A GLB is the posed mesh baked
to a static model (no skeleton).
