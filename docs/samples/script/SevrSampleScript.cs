// A sample of Cheezecake's SE VR's API for a programmable block script (docs/MOD_API.md, "Programmable block scripts").
// Paste this file into a programmable block's code. It reads the block's hidden "SevrApi" property and writes what it finds
// with Echo (the block's detailed info, in its terminal), every 100 ticks. Every line starts "SevrSample: script:".

IReadOnlyDictionary<string, Delegate> api;
int runs;

public Program()
{
    Runtime.UpdateFrequency = UpdateFrequency.Update100;
}

public void Main(string argument, UpdateType updateSource)
{
    runs++;
    if (api == null)
    {
        // Ask for the interface: the script compiler swaps a Dictionary a script names for its own kind.
        var property = Me.GetProperty("SevrApi");
        var typed = property == null ? null : property.As<IReadOnlyDictionary<string, Delegate>>();
        api = typed == null ? null : typed.GetValue(Me);
    }
    if (api == null)
    {
        // Not in this game, or not added yet (a script saved with the world can start first): look again next run.
        Echo("SevrSample: script: no SEVR API (run " + runs + ")");
        return;
    }

    var version = Get<Func<int>>("Version");
    var isActive = Get<Func<bool>>("IsActive");
    var hudVisible = Get<Func<bool>>("HudVisible");
    var hudDepth = Get<Func<double>>("HudDepth");
    var hudTangents = Get<Func<Vector2D>>("HudTangents");
    Vector2D tangents = hudTangents != null ? hudTangents() : Vector2D.Zero;

    Echo("SevrSample: script: Version " + (version != null ? version().ToString() : "none"));
    Echo("SevrSample: script: IsActive " + (isActive != null && isActive()));
    Echo("SevrSample: script: HudVisible " + (hudVisible != null && hudVisible()));
    Echo("SevrSample: script: HudDepth " + (hudDepth != null ? hudDepth().ToString("0.00") : "none"));
    Echo("SevrSample: script: HudTangents " + tangents.X.ToString("0.000") + " x " + tangents.Y.ToString("0.000"));
    Echo("SevrSample: script: HudToWorld offered " + api.ContainsKey("HudToWorld") + " (run " + runs + ")");
}

T Get<T>(string name) where T : class
{
    Delegate member;
    return api.TryGetValue(name, out member) ? member as T : null;
}
