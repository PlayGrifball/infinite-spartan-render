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
  "parts": [ { "region": -592012529, "permutation": 1704860283 } ],   // optional: pieces by the game's ids
  "coating": "1120495519",
  "visor": "2024 FaZE Launch",
  "output": "png",     // png | glb
  "size": "small",     // small | full
  "framing": "body",   // body | head (head frames the helmet)
  "stance": 39388885,  // optional StanceId of a player's action pose; left out = the default relaxed stance
  "weapon": "VK78 Commando Rifle",   // optional, a name from the catalog's `weapons`, held in the right hand
  "weaponCoating": "-976095822"      // optional, one of that weapon's `coatings`; left out = its default coating
}
-> 202 { "id": "...", "state": "queued", "output": "png" }

GET /spartans/{id}        -> { "id", "state": queued|running|done|failed, "error" }
GET /spartans/{id}/file   -> the PNG or GLB (404 until done)
```

A whole Spartan, in a chosen stance and in the neutral one, is one request:

```
POST /spartan-sets
{ "core": "Mark VII", "armor": {}, "coating": "1120495519", "stance": 39388885, "weapon": "VK78 Commando Rifle", "size": "small" }   // stance is required
-> 202 { "id", "state", "items": [ { "name", "state", "output", "url" } ] }

GET /spartan-sets/{id}    -> the same; state is the worst of the five: failed, else done when all are, else running or queued
```

The five items are `glb`, `stance-body`, `stance-head`, `neutral-body` and `neutral-head`; the GLB is one skinned model with the game rig and two animation clips, `neutral` and `stance`, so anyone can pose it or add their own animations; each
`url` is a `/spartans/{id}/file`. They are ordinary jobs (the same cache, queue and limits), so a set whose pieces were
rendered before costs nothing, and a GLB takes minutes. Sets, like jobs, do not survive a restart: post again.

`GET /catalog` lists what a loadout may name:

```
{ "cores": [ { "name": "Mark VII", "regions": [ { "kind": "REGION", "key": "helmet", "region": -592012529,
                "pieces": [ { "permutation": 123, "name": "Mark VII" }, { "permutation": 456, "name": null } ] } ] } ],
  "coatings": [ "-1973444192", "..." ],   // style ids, the `coating` field
  "visors": [ "...titles..." ],
  "weapons": [ { "name": "VK78 Commando Rifle", "coreType": "WeaponCommando", "tags": [ -40348340 ], "coatings": [ "..." ] } ],
  "poses": [ { "name": "Captain's Pride", "stance": -1420523120, "weapon": "Energy Sword" } ],
  "pendingCores": [ "Chimera" ] }          // still importing; they join when their import is done
```

A piece of a REGION with a `name` goes in `armor` under its region `key` (a KIT region's pieces go in `parts`); a piece with `name` null (some kits) goes in `parts` by
`region` and `permutation`. `kind` is REGION, or KIT for a kit's own regions. A core's entry is written by the start-up
warm-up from its saved import, so right after a start some cores are in `pendingCores`.

The id is a hash of the loadout and of the Blender script, so asking again returns the cached file, and a changed script never serves files an older one made. `parts` addresses a piece by the game's model region id and permutation id, for pieces that have no name (some kits). One
entry per region; it wins over `armor` for that region, and a part that matches no mesh fails the render. A region left out of `armor` gets the core's
default piece; `coating` left out gets the default coating; `visor` left out gets the core's default visor, and a title not in the catalog's `visors` is refused.

A stance holds a weapon: the action pose item's `weaponTagId` is one of a catalog weapon's `tags`, and that weapon's `coreType` names the
player's Halo weapon core whose equipped theme gives its coating (null for weapons with no customization). `weapon` puts it in the right hand;
in a set it is in the stance renders and the GLB's `stance` clip only, never in the neutral pose. The list is `blender/weapons.json`;
a weapon is in the catalog once its model is in the data folder.

A `glb` (or a set's GLB) can carry more clips: `extraPoses` lists stance ids from the catalog's `poses` (`blender/poses.json`), and each
clip is named after its pose and holds that pose's weapon, in the coating `weaponCoatings` (weapon name to coating id) gives it, else its
default. Each weapon is baked once per coating and reused, so a few extra poses cost seconds once their weapons are baked.

| size  | png                    | glb                       |
|-------|------------------------|---------------------------|
| small | 700x1000, 48 samples   | textures at most 1024 px  |
| full  | 2100x3000, 128 samples | original textures         |

A PNG has a transparent background (RGBA); the scene is still lit by the grey world.

## Settings (`Render:` section / `Render__X` environment)

`BlenderPath`, `ScriptPath`, `CacheDirectory` (`/cache`), `WorkDirectory` (`/tmp/render`), `MaxConcurrentJobs` (1; a
render needs several GB of RAM), `MaxQueued` (20; more gets 503), `SubmitsPerMinute` (60 per address; more gets 429),
`CacheDays` (30; older results are deleted), `TimeoutSeconds` (1800), `WarmImports` (true), `Cores`.

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
- **Telemetry**: metrics and traces go out over OTLP only, configured by the standard `OTEL_*` variables and on when `OTEL_EXPORTER_OTLP_ENDPOINT` is set. Metrics: `spartan_render.jobs` (by `outcome`: done, failed, cancelled, cached, rejected, joined), `spartan_render.duration` (seconds), `spartan_render.queue_depth`, `spartan_render.phase_duration` (seconds, by `phase`: import, materials, pose, mesh, bake, export, scene, render, and `output`; a bake is one sample per mesh and texture, so sum it); the same stages are events on the `render` span; each render is one `render` span.
- **Client address**: behind a proxy, set `ForwardedHeaders:KnownIPNetworks` (or `KnownProxies`, `ForwardedForHeaderName`) so the submit rate limit counts real clients.
- **Helm chart**: `charts/infinite-spartan-render` (one replica, Recreate, read-only root, the game data PVC mounted read-only at `/data`, a cache PVC, NetworkPolicy, Traefik IngressRoute, PrometheusRule). CI lints, unit-tests and schema-checks it.

## .NET client

`PlayGrifball.InfiniteSpartanRender.Client` (`src/PlayGrifball.InfiniteSpartanRender.Client`, see its README) is a typed
`HttpClient` for this API. It is published to nuget.org and GitHub Packages with each release whose commit changed the
client, at the release's version (`publish.yml`, called by `helm-chart.yml`).

Importing a core takes minutes and does not depend on the loadout or stance, so the imported scene is saved as
`<cache>/imports/<core>-<key>.blend` (the key covers the core, Blender and the Ekur manifest) and later jobs for that
core open it. A GLB's baked textures do not depend on the stance either, so they are kept per loadout in
`<cache>/bakes/<key>/` (the key adds the loadout and the Blender script) and a GLB in another stance of the same armor
loads them instead of baking again. The service warms every core in `Cores` at startup (one at a time, in a job slot) and daily, so the first request
for a core does not wait for its import (a core already saved is only touched, with no import or open); `WarmImports` (default true) turns that off. A changed game data folder needs `imports/` and `bakes/` cleared; files unused for
`CacheDays` are swept.

The stance is a Halo animation: a player's `actionPosePath` item (gateway `gamecms/item`) carries a `StanceId`, and
the file `anim_<StanceId>_*.ekuranim` must be in `<data folder>/stances/`. A GLB is the posed mesh baked
to a static model (no skeleton).
