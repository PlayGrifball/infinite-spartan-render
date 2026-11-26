namespace PlayGrifball.InfiniteSpartanRender.Client;

/// <summary>What to render: a core, its armor pieces and coating, and how.</summary>
public sealed record SpartanLoadout
{
    /// <summary>The armor core, e.g. <c>Mark VII</c>.</summary>
    public required string Core { get; init; }

    /// <summary>Region (e.g. <c>helmet</c>) to permutation name; a region left out gets the core's default piece.</summary>
    public IDictionary<string, string> Armor { get; init; } = new Dictionary<string, string>();

    /// <summary>A coating id; null or empty is the default coating.</summary>
    public string? Coating { get; init; }

    /// <summary><c>png</c> or <c>glb</c>.</summary>
    public string Output { get; init; } = "png";

    /// <summary><c>small</c> or <c>full</c>.</summary>
    public string Size { get; init; } = "small";

    /// <summary><c>body</c> or <c>head</c> (the head frames the helmet).</summary>
    public string Framing { get; init; } = "body";

    /// <summary>A StanceId from a player's action pose item; null is the default relaxed stance.</summary>
    public int? Stance { get; init; }
}

/// <summary>A render job: <see cref="State"/> is queued, running, done or failed.</summary>
public sealed record SpartanJob(string Id, string State, string? Output = null, string? Error = null)
{
    public bool IsDone => State == "done";
    public bool IsFailed => State == "failed";
}

/// <summary>A Spartan in a chosen stance and in the neutral one: a GLB, a body PNG and a headshot of each.</summary>
public sealed record SpartanSetRequest
{
    public required string Core { get; init; }
    public IDictionary<string, string> Armor { get; init; } = new Dictionary<string, string>();
    public string? Coating { get; init; }

    /// <summary>A StanceId from a player's action pose item (required by the API).</summary>
    public required int Stance { get; init; }

    public string Size { get; init; } = "small";
}

/// <summary>One of the six files of a set: <see cref="Name"/> is stance-glb, stance-body, stance-head, neutral-glb, neutral-body or neutral-head.</summary>
public sealed record SpartanSetItem(string Name, string State, string Output, string Url);

/// <summary>A set's state is the worst of its six items: failed, else done when all are, else running or queued.</summary>
public sealed record SpartanSet(string Id, string State, IReadOnlyList<SpartanSetItem> Items)
{
    public bool IsDone => State == "done";
    public bool IsFailed => State == "failed";
}
