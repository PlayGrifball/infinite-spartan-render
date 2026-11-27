# `PlayGrifball.InfiniteSpartanRender.Client`

.NET client for the [Infinite Spartan Render](https://github.com/PlayGrifball/infinite-spartan-render) API, which renders a
Halo Infinite Spartan to a PNG or exports it as a GLB.

## Install

```shell
dotnet add package PlayGrifball.InfiniteSpartanRender.Client
```

```csharp
using PlayGrifball.InfiniteSpartanRender.Client;

builder.Services.AddInfiniteSpartanRenderClient(o => o.BaseAddress = new Uri("http://infinite-spartan-render:8080/"));
// or from configuration: builder.Services.AddInfiniteSpartanRenderClient(builder.Configuration.GetSection("SpartanRender"));
```

## Use

A render takes minutes, so asking returns a job. An identical earlier request is answered at once from the API's cache.

```csharp
// One file
var png = await client.RenderAsync(new SpartanLoadout { Core = "Mark VII", Armor = { ["helmet"] = "Mk 7" } });
// A piece with no name (some kits) by region id and permutation id: Parts = { new SpartanPart(-592012529, 1704860283) }

// The pieces of the flow
var job = await client.SubmitAsync(new SpartanLoadout { Core = "Yoroi", Output = "glb" });
job = await client.WaitAsync(job);
var glb = await client.GetFileAsync(job.Id);

// A whole Spartan in a stance and in the neutral one: five files (body and head in each pose, one rigged GLB)
var set = await client.SubmitSetAsync(new SpartanSetRequest { Core = "Mark VII", Stance = 39388885 });
set = await client.WaitAsync(set);
foreach (var item in set.Items) File.WriteAllBytes($"{item.Name}.{item.Output}", await client.GetFileAsync(item));
```

`SpartanRenderException` carries the status code: 400 (invalid loadout, with the API's message), 429 (rate limit) or 503
(queue full), with `RetryAfter` when the API gave one. Jobs and sets are held in the API's memory, so after an API restart
`GetJobAsync` and `GetSetAsync` return null and `WaitAsync` throws: submit again.
