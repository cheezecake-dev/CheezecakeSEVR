# The SEVR API, for mods and plugins

Cheezecake's SE VR gives mods and other plugins a small API, so they can show themselves properly in a headset without the
VR plugin patching them. A mod that draws a HUD, such as Text HUD API or Rich HUD Framework, usually draws it on a plane a
few centimetres in front of the camera. On a flat screen that looks right. In a headset it is too close for the eyes to
fuse, and it does not line up with what it marks. With the API, the mod draws it where the VR HUD is.

The API only describes how things are shown: where the HUD is and whether it is visible. It says nothing about the game
that a player without a headset could not see.

## Getting it

The API comes over the game's mod messaging, the way Text HUD API hands over its own. A mod cannot reference a plugin, but
it can receive a dictionary of delegates.

- Channel: `6000296989824141617` ("SEVRAPI1" in ASCII, as a long).
- The plugin sends a `Dictionary<string, Delegate>` on the channel when a world starts.
- It sends the dictionary again whenever anyone sends the string `"SEVR.ApiRequest"` on the channel. A mod that loads after
  the plugin asks for it this way.
- Every member is called on the game thread, where mod code runs.
- Without the plugin, or without a headset, nothing arrives, or `IsActive` returns false. Draw as you always do.

## Version 1

| Name | Type | What it gives |
|---|---|---|
| `Version` | `Func<int>` | The API's version, 1 here. New members raise it; a member's meaning never changes. |
| `IsActive` | `Func<bool>` | The game is being shown in a headset now. |
| `HudVisible` | `Func<bool>` | The headset shows the HUD now. False while the helmet is open with the helmet view on, since the HUD is on the visor. Hide your HUD then too. |
| `HudDepth` | `Func<double>` | Metres from the eyes at which the HUD is seen. Draw your HUD this far out, not at the camera. |
| `HudTangents` | `Func<Vector2D>` | The tangents of half the HUD area's width and height, as seen from the eyes. |
| `HudToWorld` | `Func<Vector2D, Vector3D>` | A point of the HUD area to its world position. The point uses screen space with x right and y up, each from -1 to 1, as Text HUD API's does. The result is `HudDepth` ahead of the eyes, on the view ray through that point. |

## A mod using it

```csharp
using System;
using System.Collections.Generic;
using Sandbox.ModAPI;
using VRage.Game.Components;
using VRageMath;

[MySessionComponentDescriptor(MyUpdateOrder.NoUpdate)]
public class SevrClient : MySessionComponentBase
{
    const long Channel = 6000296989824141617L;

    public static Func<bool> IsActive, HudVisible;
    public static Func<Vector2D, Vector3D> HudToWorld;

    public override void LoadData()
    {
        MyAPIGateway.Utilities.RegisterMessageHandler(Channel, Received);
        MyAPIGateway.Utilities.SendModMessage(Channel, "SEVR.ApiRequest");
    }

    protected override void UnloadData()
    {
        MyAPIGateway.Utilities.UnregisterMessageHandler(Channel, Received);
        IsActive = HudVisible = null;
        HudToWorld = null;
    }

    void Received(object message)
    {
        var api = message as Dictionary<string, Delegate>;
        Delegate version;
        if (api == null || !api.TryGetValue("Version", out version) || ((Func<int>)version)() < 1)
            return;
        IsActive = (Func<bool>)api["IsActive"];
        HudVisible = (Func<bool>)api["HudVisible"];
        HudToWorld = (Func<Vector2D, Vector3D>)api["HudToWorld"];
    }

    /// Where a HUD point is drawn: on the VR HUD in a headset, where the mod always drew it otherwise.
    public static Vector3D Place(Vector2D hud, Func<Vector2D, Vector3D> flat)
    {
        return IsActive != null && IsActive() ? HudToWorld(hud) : flat(hud);
    }
}
```

## Mods that do not use it yet

The plugin does not patch other mods or read their private fields. Patches like that break without warning whenever the
other side updates, and they pile up as technical debt. For a mod that does not look right in VR yet:

1. Change it to call this API, usually in the one place it turns HUD coordinates into world positions.
2. Offer the change to the mod's author.
3. If players need it before the author takes it, publish an unlisted VR copy, and take it down once the author's version
   has the change.

Plugins are fewer, so for those we reach out to the author directly.

The first two candidates are **Text HUD API** (Draygo) and **Rich HUD Framework** (DarkStar). Each needs one change: the
point where a HUD-space position becomes a world position near the camera goes through `HudToWorld` while `IsActive` is true.
