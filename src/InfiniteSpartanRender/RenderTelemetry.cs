using System.Diagnostics;
using System.Diagnostics.Metrics;

namespace InfiniteSpartanRender;

// One ActivitySource and Meter for the service; exported over OTLP when OTEL_EXPORTER_OTLP_ENDPOINT is set.
public static class RenderTelemetry
{
    public const string Name = "InfiniteSpartanRender";

    public static readonly ActivitySource Source = new(Name);
    public static readonly Meter Meter = new(Name);

    // outcome: done, failed, cached (answered from the cache), joined (the same loadout was already queued or running), rejected (queue full). caller: the name of the API key the request carried, or none.
    public static readonly Counter<long> Jobs = Meter.CreateCounter<long>("spartan_render.jobs", description: "Render requests by outcome");

    // phase: import, materials, pose, mesh, bake, export, scene, render (a bake is recorded once per mesh and texture).
    public static readonly Histogram<double> PhaseDuration = Meter.CreateHistogram<double>("spartan_render.phase_duration", unit: "s", description: "Time one stage of a render took, by phase and output");

    public static readonly Histogram<double> Duration = Meter.CreateHistogram<double>("spartan_render.duration", unit: "s", description: "Time a render took, by outcome");
}
