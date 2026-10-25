# The SEVR API (version 2), for plugins, mods and scripts

Cheezecake's SE VR is a framework plugin: it loads first, and other plugins, mods and programmable block scripts can use
its API to work properly for a player in a headset, without the plugin patching them and without a word with its author.
This page is the whole reference. With it you can:

- find out whether the game is shown in a headset, and where the VR HUD is, so a HUD reads there;
- add **controller actions** the player binds on the bindings page, and be told when they are used;
- put **items on the actions wheel and the wrist panel**, which the player points at and uses;
- show **your own GUI screens** on the VR panel, at the off hand if you like;
- read where the player's **hands and head** are, and what the **trigger, grip and stick** are doing;
- **buzz the controllers**;
- be told when VR comes on or goes off, and when the main hand, the place the player is in, the HUD or the bindings change.

Contents:

- [At a glance](#at-a-glance)
- [Who can use it](#who-can-use-it)
- [Finding the API](#finding-the-api): [plugins: SevrClient.cs](#plugins-sevrclientcs) or [by reflection](#plugins-reflection), [mods](#mods-the-handshake), [scripts](#programmable-block-scripts-a-hidden-property)
- [Rules for every member](#rules-for-every-member)
- [Versions](#versions)
- [All members](#all-members)
- [HUD members (version 1)](#hud-members-version-1)
- [Controller actions](#controller-actions)
- [Menu items](#menu-items)
- [Panel screens](#panel-screens)
- [State, poses and haptics](#state-poses-and-haptics)
- [Events](#events)
- [Lifetime](#lifetime)
- [When your code throws: the guard](#when-your-code-throws-the-guard)
- [Fairness, and what is not offered](#fairness-and-what-is-not-offered)
- [Multiplayer and servers](#multiplayer-and-servers)
- [Examples](#examples)
- [Mods that do not use the API](#mods-that-do-not-use-the-api)
- [Troubleshooting](#troubleshooting)
- [Reference](#reference)

## At a glance

| | |
|---|---|
| Version | 2 (read it from the API, never from a constant) |
| Plugins get it | Copy [SevrClient.cs](samples/plugin/Source/SevrClient.cs) into the plugin: typed members, nothing else to write. Underneath, `SpaceEngineersVR.Api.SevrApi.GetApi()` by reflection |
| Mods get it | Over the game's mod messaging, channel `6000296989824141617`, by asking with `"SEVR.ApiRequest"` |
| Scripts get it | From the programmable block's hidden terminal property `"SevrApi"`: the HUD members only |
| What it is | A `Dictionary<string, Delegate>`: member name to a `Func<>` or `Action<>` |
| Thread | The game thread: every member, and every callback you give |
| Who has it | Players with the plugin, in their own game. A dedicated server has none |

## Who can use it

| Consumer | How | What it gets |
|---|---|---|
| A plugin | [SevrClient.cs](#plugins-sevrclientcs), or [reflection](#plugins-reflection) by hand | Every member. Registrations last the run. (A plugin may use the mod channel too, but what it registers through the channel then ends with the world, as a mod's does) |
| A mod, in a player's game | [The handshake](#mods-the-handshake) | Every member. Registrations end with the world |
| A programmable block script | [The hidden property](#programmable-block-scripts-a-hidden-property) | `Version`, `IsActive`, `HudVisible`, `HudDepth`, `HudTangents` |
| A mod's code on a dedicated server | Nothing arrives | The plugin runs only in players' games |

A plugin or mod works whether or not the plugin is installed: if the API is not there, do what you always did.

## Finding the API

The plugin being **loaded** is not the same as VR being **active**. The plugin can be loaded with no headset (it then plays
flat, and a headset may come on later in the run). Check `IsActive` before you change how something looks or works for VR.
Registering actions and items needs no headset: do it at start-up, and they work whenever the player is in VR. To ask only
whether the plugin is loaded, look for the type `SpaceEngineersVR.Plugin`.

### Plugins: SevrClient.cs

The quickest way for a plugin. [samples/plugin/Source/SevrClient.cs](samples/plugin/Source/SevrClient.cs) is one file to copy
into your plugin's source, as it is. It has no reference to this plugin, and it does the finding, the version check and the
delegate types for you, so every member below is an ordinary typed method. Every member does nothing when this plugin is
not installed (a `Register...` returns false, a query its "no VR" value), so the same build of your plugin works for every
player. It is for plugins only: it uses reflection, which mods may not.

```csharp
using System.IO;
using SevrIntegration;                       // the file's namespace; change it to yours if you like
using VRage.Plugins;

public class MyPlugin : IPlugin
{
    public void Init(object gameInstance)
    {
        if (!SevrClient.Connect("MyPlugin", "My plugin"))
            return;                          // no VR plugin, or one older than version 2: carry on as you always do
        SevrClient.RegisterAction("scan", "Scan around", SevrContext.OnFoot | SevrContext.Jetpack,
            "Left.ButtonUpper.Hold@0.8", on => { if (on) Scan(); });
        SevrClient.RegisterMenuItem(SevrMenu.ActionsWheel, "scan", "Scan around",
            Path.Combine("Textures", "GUI", "Icons", "Cubes", "Battery.dds"), Scan);
        SevrClient.RegisterPanelScreen(typeof(MyPickerScreen), SevrPanel.AtHand | SevrPanel.Cursor);
    }

    public void Update() { }
    public void Dispose() { }
    void Scan() { /* ... */ }
}
```

**When to call `Connect`.** From your plugin's `Init` or later (`Update`), on the game thread. The game makes every plugin
first and only then calls their `Init` (`MySandboxGame`: the plugins are made, `MySandboxGame.Static` is set, then each
`Init`), so by `Init` this plugin has been loaded whatever the order. From your constructor it is too early: `Connect`
returns false, writes "Connect was called before the game started its plugins" to the game's log, and can be called again
from `Init`. After that, `Connect` gives the same answer every time; it looks only once.

**Owner.** The id you pass to `Connect` is the owner of everything you register (the rules are in
[Rules for every member](#rules-for-every-member)). `Connect` checks it by the same rule and returns false, with a line in the
game's log, for one that breaks it; a second `Connect` with another id is ignored, logged. The client adds it to every call, and makes each action's full id
(`owner.id`) itself, so you pass the short id everywhere. The name is the heading of your actions on the bindings page;
`SetOwnerName` changes it later.

**Newer and older versions.** The client takes each member the API has, as the type this page gives. A member the API does
not have (an older version 2) or has as another type is left out, and the client's methods that use it do nothing; the
connect line in the game's log ends "; without" and lists them. A member a later version adds needs a newer copy of the file:
the file's first lines give its version.

| SevrClient | API member | Without VR, or the member missing |
|---|---|---|
| `Connect(ownerId, displayName)` | `GetApi`, `Version`, `SetOwnerName` | `false` |
| `Connected`, `Owner`, `Version` | | `false`, the id or null, `0` |
| `IsActive`, `HudVisible`, `HudDepth`, `HudTangents`, `HudToWorld(point)` | the [HUD members](#hud-members-version-1) | `false`, `false`, `0`, zero, zero |
| `SetOwnerName(name)` | [`SetOwnerName`](#setownername) | `false` |
| `RegisterAction(id, label, contexts, suggested, changed, available, followsHand)` | [`RegisterAction`](#registeraction); `changed` gets `true` for on, `false` for off | `false` |
| `UnregisterAction(id)` | `UnregisterId` | `false` |
| `ActionHeld(id)`, `ActionStarted(id)`, `ActionEnded(id)`, `ActionHoldProgress(id)`, `ActionLabel(id)` | [Polling and labels](#polling-and-labels) | `false`, `false`, `false`, `-1`, `""` (`ActionLabel` without the VR plugin; with it, see [Polling and labels](#polling-and-labels)) |
| `RegisterMenuItem(menu, id, label, icon, run, canUse, state, parent, stayOpen)`, `UnregisterMenuItem(menu, id)` | [Menu items](#menu-items); `stayOpen` is the `StaysOpen` option | `false` |
| `RegisterPanelScreen(type, options)`, `UnregisterPanelScreen(type)` | [Panel screens](#panel-screens), by the type's `FullName` | `false` |
| `Subscribe(event, handler)`, `Unsubscribe(event)` | [Events](#events) | `false` |
| `Unregister()`, `OwnerEnabled` | [Lifetime](#lifetime), [the guard](#when-your-code-throws-the-guard) | `false` |
| `DominantHand`, `Context` | [State](#state-poses-and-haptics) | `SevrHand.Right`, `SevrContext.None` |
| `ControllerProfile(hand)`, `HandTracked(hand)`, `HandGripPose(hand)`, `HandAimPose(hand)`, `HeadPose`, `HandTrigger(hand)`, `HandSqueeze(hand)`, `HandStick(hand)` | [State, poses](#state-poses-and-haptics) | `""`, `false`, `MatrixD.Zero`, `MatrixD.Zero`, `MatrixD.Zero`, `0`, `0`, zero |
| `Pulse(hand, amplitude, seconds)` | [`Pulse`](#pulse) | `false` |

The names the API takes as text are enums in the client:

- `SevrContext`, flags: the [contexts](#contexts), combined with `|`. `SevrContext.Gameplay` is the shorthand. `Context`
  returns the places the player is in as flags; a place a later version adds, which the enum does not have, is left out.
- `SevrMenu`: `ActionsWheel`, `WristPanel`.
- `SevrPanel`, flags: `Cursor`, `AtHand`, `WalkBeside`.
- `SevrEvent`: `ActiveChanged`, `DominantHandChanged`, `ContextChanged`, `HudChanged`, `BindingsChanged`.
- `SevrHand`: `Left` (0), `Right` (1).

Suggested bindings stay text ([Binding text](#binding-text)): `"Right.ButtonUpper.Press"`, `"Left.Grip.Hold@0.8"`.

Everything else on this page holds as written: the thread rule, refusals and their log lines, the guard, lifetimes. The
client adds lines of its own to the game's log (`SpaceEngineers*.log`), each starting `SevrClient: {owner}:`; see
[Troubleshooting](#troubleshooting). The sample plugin ([samples/plugin](samples/plugin/)) is written on it and uses every
kind of member.

### Plugins: reflection

What `SevrClient.cs` does, for a plugin that would rather write it itself. `SevrApi.GetApi()` is `public static`, takes no arguments, never returns null, and returns a new
`Dictionary<string, Delegate>` each call, holding the same delegates every time: yours to keep or change. It works from the
plugin's start, with or without a world, and from any thread (the members are made once, under a lock, so two callers at
once both get them whole).

- Find the class by its full name, `SpaceEngineersVR.Api.SevrApi`, among the loaded assemblies. Never by the assembly's name:
  Pulsar compiles plugins from source and may load this one under a generated name.
- Call it from your plugin's `Init` or later (`Update`), not from its constructor: the game makes plugins one by one, and
  this one may not be made yet, but it has made them all before it calls any plugin's `Init`. Not finding the class means the plugin is not installed.
- Call the members it returns on the game thread.
- Keep the delegates for the whole run.

This compiles in a plugin, and has no reference to the VR plugin. It finds the API, checks the version, registers an
action and a wheel item, learns when the player rebinds, and buzzes a controller:

```csharp
using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;

public static class SevrLink
{
    public const string Owner = "ExamplePlugin";   // your id; see "Rules for every member"

    static Dictionary<string, Delegate> api;
    static Func<bool> isActive;
    static Func<int, double, double, bool> pulse;
    static Func<string, string> actionLabel;

    /// <summary>Finds the API and registers. False when the plugin is not installed or is too old.</summary>
    public static bool Connect(Action<int> onScan, Action onBindingsChanged)
    {
        api = Find();
        if (api == null)
            return false;                                              // Cheezecake's SE VR is not installed
        Func<int> version = Get<Func<int>>("Version");
        if (version == null || version() < 2)
            return false;                                              // version 1 has no actions

        isActive = Get<Func<bool>>("IsActive");
        pulse = Get<Func<int, double, double, bool>>("Pulse");
        actionLabel = Get<Func<string, string>>("ActionLabel");

        var setOwnerName = Get<Func<string, string, bool>>("SetOwnerName");
        var registerAction = Get<Func<string, string, string, string, string, bool, Func<bool>, Action<int>, bool>>("RegisterAction");
        var registerItem = Get<Func<string, string, string, string, string, string, Action, Func<bool>, Func<string>, string, bool>>("RegisterMenuItem");
        var subscribe = Get<Func<string, string, Action, bool>>("Subscribe");
        if (setOwnerName == null || registerAction == null || registerItem == null || subscribe == null)
            return false;

        setOwnerName(Owner, "Example plugin");
        // Where it applies: on foot and on the jetpack. Suggested: hold the left Y for 0.8 s (given only if nothing else
        // uses it). Not hand-following. Always available. onScan(1) when it comes on, onScan(0) when it goes off.
        bool ok = registerAction(Owner, "scan", "Scan around", "OnFoot,Jetpack", "Left.ButtonUpper.Hold@0.8", false, null, onScan);

        // An item on the actions wheel with an icon from the game's own Content folder; it does what the action does.
        string icon = Path.Combine("Textures", "GUI", "Icons", "Cubes", "Battery.dds");
        ok &= registerItem(Owner, "ActionsWheel", "scan", null, "Scan around", icon, () => onScan(1), null, null, null);

        subscribe(Owner, "BindingsChanged", onBindingsChanged);       // ActionLabel may say something else now
        return ok;
    }

    /// <summary>The game is shown in a headset now.</summary>
    public static bool VrActive { get { return isActive != null && isActive(); } }

    /// <summary>What the player presses for the scan, as they have it bound now ("" when nothing).</summary>
    public static string ScanLabel { get { return actionLabel == null ? "" : actionLabel(Owner + ".scan"); } }

    /// <summary>A short buzz in the right controller. False when VR is not active.</summary>
    public static bool Buzz() { return pulse != null && pulse(1, 0.5, 0.1); }

    static T Get<T>(string name) where T : class
    {
        Delegate member;
        return api.TryGetValue(name, out member) ? member as T : null;
    }

    static Dictionary<string, Delegate> Find()
    {
        foreach (Assembly assembly in AppDomain.CurrentDomain.GetAssemblies())
        {
            Type type = assembly.GetType("SpaceEngineersVR.Api.SevrApi", false);
            MethodInfo get = type == null ? null : type.GetMethod("GetApi", BindingFlags.Public | BindingFlags.Static);
            if (get != null)
                return get.Invoke(null, null) as Dictionary<string, Delegate>;
        }
        return null;
    }
}
```

### Mods: the handshake

A mod cannot reference a plugin, but it can receive delegates over the game's mod messaging, as with Text HUD API.

- **Channel**: `6000296989824141617`, "SEVRAPI1" in ASCII as a `long`. One channel carries both directions.
- **The API**: a `Dictionary<string, Delegate>`, member name to delegate. The delegate types are ones the mod whitelist
  allows (`Func<>` and `Action<>` of primitives, strings and VRageMath types).
- **The request**: the string `"SEVR.ApiRequest"`, sent on the channel.

What happens:

1. When a world has finished loading, the plugin starts listening on the channel and sends the API there once.
2. Whenever anyone sends `"SEVR.ApiRequest"` on the channel while the world runs, the plugin sends the API again.
3. When the world unloads, the registrations the mods made in it are taken away. The next world starts again at step 1.

So a mod registers its handler in `LoadData` and sends the request at once; that covers both orders (the plugin first or the
mod first). Also:

- Mod messages go to **every** handler on the channel, the sender's own included. You receive your own request string, other
  mods' requests, and the API once for each request anyone sends. Each send is a new dictionary holding the same delegates;
  use the first and ignore the rest.
- **Copy out what you use when it arrives**, and never change the dictionary. The game hands one sent object to every
  handler on the channel in turn (`SendModMessage` calls each handler with the same object), so all the mods of one send
  share that dictionary: a mod that removes or replaces an entry changes it for the handlers after it. The plugin cannot
  give each mod its own; it sends a new one each time, so such a change never reaches a later send or another world. The
  samples below copy it on receipt.
- In `UnloadData`, unregister the handler, call `Unregister` for your owner, and drop your delegates: they belong to the
  world that is ending.

This compiles in a mod and is a complete session component. It gets the API, checks the version, and registers an action and
a wheel item:

```csharp
using System;
using System.Collections.Generic;
using System.IO;
using Sandbox.ModAPI;
using VRage.Game.Components;

namespace ExampleMod
{
    [MySessionComponentDescriptor(MyUpdateOrder.NoUpdate)]
    public class SevrSetup : MySessionComponentBase
    {
        const long Channel = 6000296989824141617L;
        const string Request = "SEVR.ApiRequest";
        const string Owner = "ExampleMod";            // your id; see "Rules for every member"

        Dictionary<string, Delegate> api;

        public override void LoadData()
        {
            MyAPIGateway.Utilities.RegisterMessageHandler(Channel, Received);
            MyAPIGateway.Utilities.SendModMessage(Channel, Request);
        }

        protected override void UnloadData()
        {
            MyAPIGateway.Utilities.UnregisterMessageHandler(Channel, Received);
            var unregister = Get<Func<string, bool>>("Unregister");
            if (unregister != null)
                unregister(Owner);
            api = null;
        }

        void Received(object message)
        {
            var offered = message as Dictionary<string, Delegate>;
            if (offered == null || api != null)
                return;                                // a request (ours or another mod's), or the API again
            api = new Dictionary<string, Delegate>(offered);   // our own copy: the one sent is shared with every mod

            var version = Get<Func<int>>("Version");
            if (version == null || version() < 2)
                return;                                // an older plugin: no actions or items
            var setOwnerName = Get<Func<string, string, bool>>("SetOwnerName");
            var registerAction = Get<Func<string, string, string, string, string, bool, Func<bool>, Action<int>, bool>>("RegisterAction");
            var registerItem = Get<Func<string, string, string, string, string, string, Action, Func<bool>, Func<string>, string, bool>>("RegisterMenuItem");
            if (setOwnerName == null || registerAction == null || registerItem == null)
            {
                api = null;                            // another mod changed this send: forget it and take the next one
                return;
            }

            setOwnerName(Owner, "Example mod");
            // Anywhere the player plays; no suggested binding, so the player binds it on the bindings page.
            registerAction(Owner, "hello", "Say hello", "Gameplay", null, false, null, on =>
            {
                if (on == 1)
                    MyAPIGateway.Utilities.ShowNotification("Hello from the controller", 1500);
            });
            // An item on the actions wheel, with a texture from the game's own Content folder.
            string icon = Path.Combine("Textures", "GUI", "Icons", "Cubes", "Battery.dds");
            registerItem(Owner, "ActionsWheel", "hello", null, "Say hello", icon,
                () => MyAPIGateway.Utilities.ShowNotification("Hello from the wheel", 1500), null, null, null);
        }

        T Get<T>(string name) where T : class
        {
            Delegate member;
            return api != null && api.TryGetValue(name, out member) ? member as T : null;
        }
    }
}
```

### Programmable block scripts: a hidden property

A script cannot use mod messaging or reflection, but it can read its own block's terminal properties. The plugin adds one to
every programmable block, as Multigrid Projector hands scripts its API:

- **Name**: `"SevrApi"`. It is hidden: not on the block's terminal screen, and it has no action.
- **Type**: `IReadOnlyDictionary<string, Delegate>`. Ask for that, never for a `Dictionary<string, Delegate>`: the game's
  script compiler swaps every `Dictionary` a script names for its own memory-safe kind, so in a script
  `As<Dictionary<string, Delegate>>()` always returns null, whatever the property holds. Interfaces are not swapped, which is
  why the property is typed by one. The same goes for `List`, `HashSet`, `Queue`, `Stack`, `LinkedList`, `StringBuilder` and
  the sorted collections.
- **Members**: `Version`, `IsActive`, `HudVisible`, `HudDepth` and `HudTangents`. Not `HudToWorld`, and none of version 2's
  (`Version` still says 2): see [Fairness](#fairness-and-what-is-not-offered). A script asks for the member it uses.

```csharp
var property = Me.GetProperty("SevrApi");
var typed = property == null ? null : property.As<IReadOnlyDictionary<string, Delegate>>();
IReadOnlyDictionary<string, Delegate> api = typed == null ? null : typed.GetValue(Me);
// null: the plugin is not in this game, or has not added the property yet.
```

When it is there:

- The plugin adds it once a world has loaded and the game has made the programmable block's terminal controls, which the
  game does when the first programmable block is created. In a world with programmable blocks that is as the world loads;
  in a world without one, it is when the first is built.
- A script made as the world loads, such as one saved with the world, can start before that. Look again until you find it.
  The [script agent](#a-script-agent) looks on every call until it has found it, so a script can make one in `Program()` and
  use it from the first `Main`. Once found, it stays good for the rest of the world.
- Scripts run where the grid is simulated. In single player that is the player's game. In a game a player hosts it is the
  host's, so a script sees the **host's** headset, whoever owns or built the block. On a dedicated server there is no plugin
  and no property.
- The world must allow scripts (its "In-game scripts" setting).

## Rules for every member

These hold for every member of version 2. (The version 1 HUD members only read, and do not check the thread.)

**Threads.** Call every member on the game thread, and expect every callback you give on it. A version 2 member called from
another thread does nothing: it returns `false`, `0`, an empty string, `MatrixD.Zero` or `-1` (each member below says which;
`DominantHand` returns `1`, right-handed),
and logs the refusal once. Calls are never queued: a queue would hide an ordering mistake. Callbacks have no time limit (one
on the game thread cannot be cut off safely); one that is slow slows the frame, and one slower than 1 ms is logged: see
[When your code throws](#when-your-code-throws-the-guard).

**Owners.** Every member that registers something takes an `owner` first: a string of 1 to 64 ASCII letters (`A`-`Z`,
`a`-`z`), digits, `_`, `.` or
`-` naming you ("MyMod", "com.example.tools"), the same on every call. Everything you register is filed under it, the
player's bindings for your actions are saved under it, and `Unregister(owner)` takes it all away. Owner ids are not
authenticated: any caller using an id acts as that owner. Pick one nobody else will, and do not share one between your own
plugins and mods unless you mean to.

**Invalid input.** A call with input it cannot use (a malformed id, an unknown name, a null handler, a label that is empty
or too long, an icon that is not there) is **refused**: the member returns `false` (or the "nothing" value), and the reason
is written to the plugin's log once per owner, member and reason. Nothing throws into your code. Always check the result of
a `Register...` and `Subscribe` call. After 256 different refusals, later ones are not written.

**Strings.** Labels and names are one line: line breaks and other control characters are refused. Lengths are in
characters. Ids are matched exactly (capitals count), and so are the names of contexts, menus and events. Option lists
(`"StaysOpen"`, `"Cursor, AtHand"`) are not case sensitive.

**Callbacks and exceptions.** The callbacks you give run under a guard: see
[When your code throws](#when-your-code-throws-the-guard). A callback that throws never reaches the plugin or the game.

**Replacing.** Registering the same owner and id again replaces the definition in place and keeps what the player did with
it (their binding, the item's place). This is what a reloaded plugin does.

**Limits.**

| What | Limit |
|---|---|
| Owner id | 1 to 64 ASCII letters, digits, `_`, `.`, `-` |
| Action id, menu item id | 1 to 64 ASCII letters, digits, `_`, `-` (no `.`) |
| Actions per owner | 64. Label: 80 characters. Suggested bindings: 4 |
| Owner name (`SetOwnerName`) | 48 characters |
| Menu items per owner | 64 over all menus; 4 on a menu's top ring; 15 on a page; pages 3 rings deep |
| Menu item label / state text / icon path | 40 / 60 / 260 characters |
| Panel screens per owner | 32. Type name: 512 characters |

## Versions

- `Version` is an `int`, now 2. Version 1 had the HUD members; version 2 added actions, hand hints, menu items, panel
  screens, events, state, poses and haptics.
- A member's name, type and meaning never change. Members are only ever added: a member present in version N is present in
  every version after it. A member whose meaning had to change would be a new member under a new name.
- Code against it by **asking for the member** you use: `TryGetValue`, then `as` the delegate type you expect. If it is
  missing, do without. Then a newer plugin never breaks you, and an older one lacks only what it does not have. Reading
  `Version` first (as the examples do) is a quick way to decide whether to try at all.
- Read `Version` from the dictionary. `SevrApi.Version` is a constant, and a constant is compiled into the caller.
- Scripts report the same `Version` but get only the HUD members, so a script must ask for the member, not trust the version.

## All members

Hands are numbered `0` for the left and `1` for the right; any other number is refused. Every name is a key of the
dictionary and the type is the delegate to cast it to. "P" is plugins, "M" mods, "S" scripts. `Vector2D`, `Vector3D` and
`MatrixD` are VRageMath's. The parameter names below are for reading only: a `Func<>` has none.

| Name | Delegate type | Section | Routes |
|---|---|---|---|
| `Version` | `Func<int>` | [HUD](#hud-members-version-1) | P M S |
| `IsActive` | `Func<bool>` | [HUD](#hud-members-version-1) | P M S |
| `HudVisible` | `Func<bool>` | [HUD](#hud-members-version-1) | P M S |
| `HudDepth` | `Func<double>` | [HUD](#hud-members-version-1) | P M S |
| `HudTangents` | `Func<Vector2D>` | [HUD](#hud-members-version-1) | P M S |
| `HudToWorld` | `Func<Vector2D, Vector3D>` | [HUD](#hud-members-version-1) | P M |
| `SetOwnerName` | `Func<string owner, string name, bool>` | [Actions](#setownername) | P M |
| `RegisterAction` | `Func<string owner, string id, string label, string contexts, string suggested, bool followsHand, Func<bool> available, Action<int> callback, bool>` | [Actions](#registeraction) | P M |
| `ActionHeld` | `Func<string fullId, bool>` | [Polling](#polling-and-labels) | P M |
| `ActionStarted` | `Func<string fullId, bool>` | [Polling](#polling-and-labels) | P M |
| `ActionEnded` | `Func<string fullId, bool>` | [Polling](#polling-and-labels) | P M |
| `ActionHoldProgress` | `Func<string fullId, double>` | [Polling](#polling-and-labels) | P M |
| `ActionLabel` | `Func<string fullId, string>` | [Polling](#polling-and-labels) | P M |
| `RegisterMenuItem` | `Func<string owner, string menu, string id, string parent, string label, string icon, Action run, Func<bool> canUse, Func<string> state, string options, bool>` | [Menu items](#registermenuitem) | P M |
| `UnregisterMenuItem` | `Func<string owner, string menu, string id, bool>` | [Menu items](#unregistermenuitem) | P M |
| `RegisterPanelScreen` | `Func<string owner, string screenType, string options, bool>` | [Panel screens](#registerpanelscreen) | P M |
| `UnregisterPanelScreen` | `Func<string owner, string screenType, bool>` | [Panel screens](#unregisterpanelscreen) | P M |
| `Subscribe` | `Func<string owner, string eventName, Action handler, bool>` | [Events](#events) | P M |
| `Unsubscribe` | `Func<string owner, string eventName, bool>` | [Events](#events) | P M |
| `Unregister` | `Func<string owner, bool>` | [Lifetime](#lifetime) | P M |
| `UnregisterId` | `Func<string owner, string id, bool>` | [Lifetime](#lifetime) | P M |
| `OwnerEnabled` | `Func<string owner, bool>` | [The guard](#when-your-code-throws-the-guard) | P M |
| `DominantHand` | `Func<int>` | [State](#state-poses-and-haptics) | P M |
| `Context` | `Func<string>` | [State](#state-poses-and-haptics) | P M |
| `ControllerProfile` | `Func<int hand, string>` | [State](#state-poses-and-haptics) | P M |
| `HandTracked` | `Func<int hand, bool>` | [State](#state-poses-and-haptics) | P M |
| `HandGripPose` | `Func<int hand, MatrixD>` | [State](#state-poses-and-haptics) | P M |
| `HandAimPose` | `Func<int hand, MatrixD>` | [State](#state-poses-and-haptics) | P M |
| `HeadPose` | `Func<MatrixD>` | [State](#state-poses-and-haptics) | P M |
| `HandTrigger` | `Func<int hand, double>` | [State](#state-poses-and-haptics) | P M |
| `HandSqueeze` | `Func<int hand, double>` | [State](#state-poses-and-haptics) | P M |
| `HandStick` | `Func<int hand, Vector2D>` | [State](#state-poses-and-haptics) | P M |
| `Pulse` | `Func<int hand, double amplitude, double seconds, bool>` | [Haptics](#pulse) | P M |

## HUD members (version 1)

These only read. They take no locks and allocate nothing, so they are cheap to call every frame, and the first five are all
a script gets.

| Member | Returns | Without VR |
|---|---|---|
| `Version` | The API's version, 2. | (the API is missing) |
| `IsActive` | True while the game is shown in a headset: the plugin has turned the picture over to VR and the headset's session is running. False at startup before a headset is found, while the plugin plays flat, and while the session is stopped (the headset asleep, or the runtime closing it). It can change at any time: read it each frame. | `false` |
| `HudVisible` | True while the headset shows the HUD, which in VR is on the inside of the helmet's visor. False whenever `IsActive` is false, and while the helmet is open with the player's helmet view on: there is no visor to show it on. Hide your HUD when this is false, as the game hides its own. It does not follow the suit's power: when the suit's battery runs out and the visor's HUD goes dark, `HudVisible` stays true. The game's own toggle to hide the HUD is the game's: check `MyAPIGateway.Session.Config.HudState` as on a flat screen. | `false` |
| `HudDepth` | Metres from the eyes at which the HUD is seen: the player's "HUD distance" setting, 1.5 by default, 0.5 to 5. Draw a HUD this far out, not at the camera. | do not use |
| `HudTangents` | The HUD area's size as tangents from the eyes' line of sight: `X` to the right edge, `Y` to the top edge. At `HudDepth` the area is `2 * X * HudDepth` metres wide and `2 * Y * HudDepth` high. The width is the player's "HUD width" (55 degrees by default, 20 to 120); the height keeps the game's 16:9: about 0.521 and 0.293 by default. | do not use |
| `HudToWorld(Vector2D)` | The world position of a point of the HUD area, on the view ray through it, `HudDepth` ahead of the eyes. The argument is in the HUD's screen space as Text HUD API and Rich HUD Framework use: `X` -1 (left) to 1 (right), `Y` -1 (bottom) to 1 (top); points outside -1..1 lie beyond the edges on the same plane. `Vector3D.Zero` when there is no camera. Not for scripts. | do not use |

`HudDepth`, `HudTangents` and `HudToWorld` return numbers whether or not VR is active, worked out from the player's HUD
settings, but they only mean something while `IsActive` is true; otherwise draw where you always do. With `X` and `Y` the
tangents and `head` the game camera's matrix:

```
world = head.Translation + HudDepth * (head.Forward + x * X * head.Right + y * Y * head.Up)
```

A HUD placed with `HudToWorld` is part of the scene, `HudDepth` ahead of the eyes, so geometry nearer than that (a cockpit
console close to the face) is drawn in front of it. To show over near geometry, draw as the game does: as
`BlendTypeEnum.PostPP` billboards (see [Drawing a HUD with it](#drawing-a-hud-with-it)). Place your billboards in the same
frame you call `HudToWorld`, and do not keep results from frame to frame: the head moves and the player can change the HUD's
distance and width at any time.

## Controller actions

An action is a thing the player can do with a controller button that your code is told about. It is a real action of the
plugin's binding system: the player binds it, changes it or removes it on **Options > VR > Controller** (the bindings
page), where it is listed under your owner name; it is saved with the player's bindings; it is checked against clashes; it
is mirrored for left-handed players; and it can appear on the labels beside the hands. A bound action is also listed on the
wrist panel's Controls page, after the plugin's own on that hand, in each tab for a place it applies to: On foot for
`OnFoot`; Jetpack for `Jetpack`; Building for `Build`, `OnFoot` and `Jetpack`; Seat for `Seat`; Turret for `Turret` and
`CameraView`; Tools wheel for `ToolsWheel`; Menus for `Menus`. `Spectator`, `Paint`, `Placing`, `Container`, `PingAim`,
`ActionsWheel` and `WristPanel` have no tab, so an action only of those is not listed. An unbound action is not listed. When the
page runs out of room its rows shrink to a readable size, then a last row reads "N more on the bindings page (Options > VR >
Controller)".

### SetOwnerName

`Func<string owner, string name, bool>`. The heading your actions are listed under on the bindings page, and the name shown
for your wrist panel tiles. 1 to 48 characters, one line. Until you give one, the owner id is shown. True when set; false
(refused) for an empty, too long or multi-line name. It may be called before or after your actions are registered.

### RegisterAction

`Func<string owner, string id, string label, string contexts, string suggested, bool followsHand, Func<bool> available, Action<int> callback, bool>`

Registers (or replaces) the action `id` of `owner`. Returns true when registered, false when refused. Its **full id** is
`owner.id` (`"ExampleMod.hello"`): that is what the player's binding is saved under, and what you pass to the polling
members.

| Argument | Meaning |
|---|---|
| `owner` | See [Rules for every member](#rules-for-every-member). |
| `id` | 1 to 64 ASCII letters, digits, `_` or `-` (no `.`), unique within the owner. |
| `label` | What the action does, as the bindings page lists it and the hand labels say it ("Scan around"): 1 to 80 characters, one line. |
| `contexts` | Where it applies: comma-separated [context names](#contexts). At least one. |
| `suggested` | `null` or empty for none, or up to 4 [bindings](#binding-text) separated by `;`. |
| `followsHand` | True for an action of the player's main hand, moved to the other hand for a left-handed player. |
| `available` | `null` for always. Else asked once a frame, only while the player is in one of the action's contexts. While it returns `false` the action is off, and its controls do what they would if it were not bound. |
| `callback` | `null` for none (poll instead). Else called on the game thread with `1` when the action comes on and `0` when it goes off. |

**When it is on.** A binding of kind `Press` is on while the control is down; `Tap`, for the one frame after the control is
let go before the hold time; `Hold`, from the hold time until the control is let go. When input stops counting (the headset
taken off, a screen that stops the hands), an action that is on goes off and its `0` is sent, so you never see a start
without its end. Unregistering an action that is on sends no `0`: polling it says off from then on.

**Leaving its places.** An action is on only while the player is in one of its contexts, checked every frame. When the
player moves to a place the action does not list, an action that is on goes off that frame (its `0` is sent), even with the
control still held. The usual case: opening a GUI screen moves the player to `Menus` (see [Contexts](#contexts)). So an
action held to keep your own screen open (a selector shown while a button is held) must list `Menus` as well as the place
it starts in, and should use `available` so that it does nothing in the game's other menus, for example
`available: () => MyScreenIsOpen() || BlockGroupInHand()`.

**Callback or polling.** Use the callback, polling, or both; they see the same frame. The callback is the better way to
catch an edge. The binding system runs once a frame, in the game's input update; a poll made every frame misses nothing.

#### Contexts

The place the player is in, by stable name. Names are case sensitive. An action applies in the places you list; two actions
clash only when they share a place.

| Name | The player is |
|---|---|
| `OnFoot` | Walking |
| `Jetpack` | Flying with the jetpack |
| `Seat` | In a cockpit, rover seat, remote control or passenger seat |
| `Turret` | At a turret |
| `CameraView` | Looking through a camera block |
| `Spectator` | Using the free spectator camera |
| `Build` | With a block in hand |
| `Paint` | With the colour tool in hand |
| `Placing` | With a paste or a voxel brush in hand |
| `Container` | Within reach of a container, for the quick inventory |
| `PingAim` | Aiming a ping started from the actions wheel |
| `ToolsWheel` | In the tools wheel |
| `ActionsWheel` | In the actions wheel |
| `WristPanel` | In the wrist panel |
| `Menus` | In any other screen (the game menu, the terminal, the G menu, a message box) |
| `Gameplay` | (a shorthand you may list, never reported) all of `OnFoot`, `Jetpack`, `Seat`, `Turret`, `CameraView`, `Spectator` |

`Build`, `Paint`, `Placing`, `Container` and `PingAim` come **in addition** to the place under them: a player building on
foot is in `OnFoot` and `Build`. `CameraView` comes with `Seat` or `Turret` when the player looks through a camera from a
seat or turret (`"Seat,CameraView"`). The wheels, the wrist panel and screens are places **of their own**: with the wrist
panel up the player is in `WristPanel` alone, not `OnFoot`, so an action that applies in `OnFoot` only is not on there. List
`WristPanel` as well if you want it there. While any GUI screen other than the plugin's own wheels and wrist panel has the
focus (a game menu, the terminal, a message box, your own screen, registered as a [panel screen](#panel-screens) or not,
`AtHand` and `WalkBeside` included) the player is in `Menus`. [`Context`](#state-poses-and-haptics) reports these names.

#### Binding text

A suggested binding is written for the Meta Quest Touch controllers:

```
Hand.Control.Kind[+Hand.Control][@seconds]
```

- **Hand**: `Left` or `Right`.
- **Control**: `Trigger`, `Grip`, `ThumbstickClick`, `ButtonLower` (A on the right, X on the left), `ButtonUpper` (B on the
  right, Y on the left) or `Menu` (the left controller's). `Thumbstick` (the stick pushed any way) and other control names
  parse, but are never given, see below.
- **Kind**: `Press` (while down), `Tap` (let go before the hold time) or `Hold` (held for the hold time). `Touch` is refused.
- **`+Hand.Control`**: a control held with it (a chord): `Right.ButtonLower.Press+Left.Grip` is A with the left grip held.
- **`@seconds`**: the hold time, over 0 and up to 10; 0.5 when left out. `Tap` and `Hold` use it: a tap is a release before
  it, a hold is the control held until it. `Press` ignores it.
- Case counts. Several, separated by `;`, at most 4: `Left.ButtonUpper.Hold@0.8;Right.ButtonUpper.Hold@0.8`. The same
  binding twice is refused, and so is a control held with itself.

Examples: `Right.ButtonLower.Tap`, `Left.Trigger.Press+Left.Grip`, `Left.ThumbstickClick.Hold@1`,
`Right.ThumbstickClick.Press+Right.Grip`.

#### Suggested bindings

A suggestion is a default the player can take away. It is written for a Quest and put on each controller's matching control
(a Valve Index, a PICO or a Vive wand gets the control that plays the same part), and left out on a controller that has no
such control. It is **given** only where it clashes with nothing:

- not with any binding of the plugin's own, or of another plugin or mod, in a place they share;
- not as a chord whose held control (`+Hand.Control`) has a `Press` binding in a place they share, and not as a `Press` on a
  control some chord there holds: a held control is a modifier, and a press on it would act every time it is held for the
  chord. On the tools wheel the grip that holds it open and the trigger that takes a tool are such presses, on either hand
  once mirrored for left-handed players, so a chord there that holds a grip or a trigger is not given;
- not with anything the player has bound;
- not as the bindings work right-handed **or** left-handed, so a suggestion does not depend on the handedness setting;
- never on a stick's direction (`Thumbstick`, either hand, as the control or held with it), on any controller: the sticks
  always move, turn, fly or pick on a wheel, which the clash check cannot see. The stick's click is given.

Otherwise the action **starts unbound**, and the player binds it on the bindings page. Registration still returns true. In
practice most buttons are taken on foot, so expect an on-foot action to start unbound: say in your own documentation that
the player binds it under Options, VR, Controller, and check `ActionLabel` (empty when unbound) rather than assuming.

To find a free control, open the wrist panel's Controls page at the tab for your place: it lists the plugin's own controls
there, each by its name on the controller in hand. To learn what was given, read `ActionLabel` in a `BindingsChanged` handler.

Once the player changes or removes a suggested binding, theirs stands. Two actions that suggest one control: the first by
full id (ordinal order) is given it, so plugin load order never decides it. A suggestion given today can therefore go to a
newly installed action that sorts before yours; what the player has bound is never touched.

#### Left-handed players

With `followsHand` true, a left-handed player's binding for the action is moved to the other hand's matching control by the
binding system, as the plugin's own tool actions are; you do nothing in your callback. Use it for an action of the hand that
holds the tool (a primary or secondary action, for example). Leave it false for an action the player should find on the same
physical control whichever hand is main (a menu or a mode switch).

#### Polling and labels

All take the **full id**, `owner.id`, and give the "nothing" value for an action that is not registered. Game thread only; off
it they give the "nothing" value too.

| Member | Returns |
|---|---|
| `ActionHeld(fullId)` | True when the action is on this frame. |
| `ActionStarted(fullId)` | True when it came on this frame. |
| `ActionEnded(fullId)` | True when it went off this frame. |
| `ActionHoldProgress(fullId)` | How far a hold bound to the action has got, 0 to 1; `-1` with none under way (to draw it filling). |
| `ActionLabel(fullId)` | What the player presses for the action, named for the controller in hand: `"Y"`, `"Hold Y"`, `"Right stick click"`, `"Left grip + A"`; empty when it is not bound or not registered. With several bindings, the first. |

`ActionLabel` is for your own UI ("Press Hold Y to scan"). It changes when the player rebinds or changes controller (raised as
`BindingsChanged`) and, for a `followsHand` action, when they switch hands (raised as `DominantHandChanged`, not
`BindingsChanged`): subscribe to both to refresh it. Straight after `RegisterAction` it is still empty: the binding system
takes a new action in on its next frame, gives the suggestion then, and raises `BindingsChanged`. Read the label in that
handler (subscribe before you register), not on the line after registering.

The binding system runs only while VR input does. An action registered while the plugin plays flat (no headset yet, or the
headset's session stopped) is taken in, given its suggestion and announced by `BindingsChanged` on the first frame VR input
runs. So `ActionLabel` is empty until VR has run in this game; after VR goes off it keeps naming the bindings it last had.

#### The labels beside the hands

Where the action applies and is bound, the labels beside the hands (Options > VR > Interface > Prompts: Off, Until learned,
Always) name it as they name the plugin's own controls ("Hold: boost", "Left grip held: boost"). Your lines come after the
plugin's own, in the order of the full ids, one per hand it is bound on, and only while `available` does not return false and
your owner is not switched off. A line goes once the player has used the control three times (the "Until learned" setting).
A line that does not fit the plate's eight rows is left off it; on foot the left hand's plate is often full. If the control
must be shown wherever the player looks, draw it yourself from `ActionLabel`.

## Menu items

`RegisterMenuItem` puts an item on one of the plugin's menus, drawn and used as its own items are. Using it calls your `run`.

### RegisterMenuItem

`Func<string owner, string menu, string id, string parent, string label, string icon, Action run, Func<bool> canUse, Func<string> state, string options, bool>`

Registers (or replaces, in its place) an item. Returns true when registered, false when refused.

| Argument | Meaning |
|---|---|
| `menu` | `"ActionsWheel"` or `"WristPanel"`, see below. Case sensitive. |
| `id` | 1 to 64 ASCII letters, digits, `_` or `-`; unique within the owner and menu. |
| `parent` | `null` or empty for the menu's top; or the `id` of one of your **pages** on the same menu, registered first. |
| `label` | The item's name, 1 to 40 characters, one line. |
| `icon` | A `.dds` or `.png` texture the game can load. Required: the ring shows icons. A path under the game's Content folder (`Path.Combine("Textures", "GUI", "Icons", "Cubes", "Battery.dds")`), or a full path: a mod `Path.Combine(ModContext.ModPath, @"Textures\MyIcon.dds")`; a plugin the path Pulsar gives it for a file it ships ([a plugin's own files](#a-plugins-own-files)). A file that is not found is refused. |
| `run` | What using it does, on the game thread. The menu then closes (see `options`). `null` makes the item a **page**: choosing it opens a ring of the items whose `parent` is its id (on the wrist panel, a page of tiles). |
| `canUse` | `null` for always. Else asked while the item is shown: while it returns false the item is greyed out and choosing it does nothing (a page does not open). |
| `state` | `null` for none. Else asked while the item is pointed at: a status line for it ("On", "3 ready"), at most 60 characters shown, one line. |
| `options` | `null` or empty for none, or names, comma-separated, any case: `StaysOpen`. |

**`StaysOpen`**: using the item leaves the wheel or the panel up for the next use, as the plugin's own switches do. For an
item that runs; a page with it is refused. It holds for an item used with the trigger; on a wheel, letting the grip go over
an item uses it and closes the wheel, as it does for the plugin's own.

**The actions wheel** (`"ActionsWheel"`, reached by clicking the left stick on the tools wheel): your items come after the
plugin's own on its top ring, owners in the order of their ids, each owner's items in the order it first registered them.
When more than six outside items are on the top ring, from all owners together, they all go onto one page, "More", which the
plugin owns. That page has no limit of its own (the 15 per page below is per owner), and it adds a ring: an owner's items on
the top ring are then one ring down, and their pages one ring deeper.

**The wrist panel** (`"WristPanel"`, opened with Y on foot and in the jetpack, and in a seat from the actions wheel's "Quick
actions" entry, as Y is the seat's): while any item is registered for it, an
"Add-ons" tile in the row across its top opens a page of tiles, Back first, eleven to a page and then a "More" tile. The
panel's foot names the owner (`SetOwnerName`) of the tile pointed at.

**Limits and shape.** Pages go three rings deep: the top ring, and two pages under it. At most 4 of an owner's items on a
menu's top (more go on a page of your own), 15 on a page (Back comes with them), and 64 per owner on all menus. A page must
be registered before its items; taking a page away takes the items under it.

#### A plugin's own files

Pulsar compiles a plugin from its source, so the plugin has no folder of its own to look in. To ship a file, such as an icon,
name it as an asset in the plugin's Pulsar data file, with its path from the data file's folder (a GitHub plugin's: the
repository's root):

```xml
<Asset Name="icon" Path="Assets/MyIcon.dds" />
```

Right after making the plugin, Pulsar calls the plugin class's `LoadAssets` with each asset's name and the full path of
the file where Pulsar keeps it (for a local folder plugin, the file in that folder). Use the path as given. Declare the method on the class that implements `IPlugin` itself (Pulsar looks for it there
by name and parameter type, not on a base class):

```csharp
public void LoadAssets(IReadOnlyDictionary<string, string> assets)
{
    string icon;
    if (assets.TryGetValue("icon", out icon))
        iconPath = icon;    // give this to RegisterMenuItem
}
```

The sample plugin (docs/samples/plugin) does this for its actions-wheel item and wrist tile.

### UnregisterMenuItem

`Func<string owner, string menu, string id, bool>`. Takes the owner's item away, and for a page the items under it. True when
it was registered.

An item does only what your code does when run. Fairness is yours as well: do not put on a menu what the same player could
not do on a flat screen.

## Panel screens

Any GUI screen that takes the focus (the game's or yours) is **already** shown on the VR panel, in the world in front of the
player, and one that shows the game's mouse cursor is already used with the laser as every game menu is. Register a screen
only if it needs more than that. While it has the focus the player is in `Menus` ([Contexts](#contexts)).

On a screen the controls are the game's mouse and keys, so a screen written for a flat screen works unchanged:

| Control | What the screen gets |
|---|---|
| The main hand's laser (the right hand, the left with "Left-handed" on) | the mouse cursor, where the laser meets the panel |
| Its trigger / grip / stick | left click / right click / scroll wheel |
| B, or a tap of the left Menu button | back: the screen closes as it does for Escape on a flat screen |
| A text box clicked | the on-screen keyboard opens; a real keyboard works too |
| The mouse | moves the cursor over the whole screen as on a flat screen, until the laser's trigger or grip is pressed on it |

The player's guide has the same table (README, "Menus").

### RegisterPanelScreen

`Func<string owner, string screenType, string options, bool>`. Returns true when registered, false when refused.

| Argument | Meaning |
|---|---|
| `screenType` | The screen's `Type.FullName` ("MyPlugin.Gui.MyPickerScreen"; nested types with `+`), matched exactly: not its subclasses. A closed generic type (its full name has `[`) is refused: register a non-generic screen class. The game's screens and this plugin's cannot be registered (names starting `Sandbox.`, `SpaceEngineers.Game.`, `VRage.`, `SpaceEngineersVR.`). Up to 512 characters. |
| `options` | At least one of, comma-separated, any case: `Cursor`, `AtHand`, `WalkBeside`. |

- `Cursor`: the screen hides the cursor (`DrawMouseCursor` false) yet is used by pointing; in VR the cursor is shown on it
  while it has the focus, so the laser works.
- `AtHand`: a short-lived selector. The panel stands at the player's off hand (always the off hand: the left for a
  right-handed player) as the screen opens, centred on where that hand holds things and facing the head, and stays there,
  fixed to the player's play space, until the screen loses the focus, as the plugin's own wheels do, instead of ahead of the
  player. With the off hand not tracked it stands 0.4 m ahead of the eyes and 0.15 m down. The panel is **0.5 m wide** and
  shows the whole 16:9 GUI, so draw the selector around the screen's centre and size it to read at that width (the full GUI
  width is half a metre: text the size of the game's menu text is small there). The main hand's laser points at it.
- `WalkBeside`: on foot and in the jetpack, the sticks keep walking, turning and jumping while the screen has the focus, as on
  the wrist panel; the trigger, grips, B and Menu stay the screen's.

Two owners registering one type: the first by owner id (ordinal) is used. Registering the same owner and type again replaces
its options. Mods have no GUI screens of their own (the mod whitelist leaves them out), so this member is a plugin's in
practice, though it is on both routes.

### UnregisterPanelScreen

`Func<string owner, string screenType, bool>`. Takes the owner's registration of the type away. True when it had one.

## State, poses and haptics

These describe the player's own head, hands and controllers, in the world, in the game's units (metres). When VR is not
active they give the "no VR" value and change nothing; `DominantHand` is the player's setting, so it reads the same either way.

| Member | Returns | No VR / not valid |
|---|---|---|
| `DominantHand()` | The main hand: `1` right-handed, `0` left-handed, from the player's "Left-handed" setting in VR options. | the setting, as with VR: it is true with or without a headset |
| `Context()` | Where the player is, as [context names](#contexts), comma-separated in the order of that table (`"OnFoot,Build"`, `"WristPanel"`). | empty (also while input is stopped) |
| `ControllerProfile(hand)` | The OpenXR interaction profile of the controller in that hand, last seen: `"/interaction_profiles/oculus/touch_controller"`, `"/interaction_profiles/valve/index_controller"`, `"/interaction_profiles/htc/vive_controller"`... | empty (also with no controller in that hand) |
| `HandTracked(hand)` | The controller in that hand is tracked now. | `false` |
| `HandGripPose(hand)` | Where the hand holds things, a world matrix. | `MatrixD.Zero` while not tracked; and, even when `HandTracked` is true, while there is no world camera (the main menu, a loading screen), no character or a dead one, while the camera is something else's (spectator, a camera block, a remote control, a turret), and while the camera is far from the body (a teleport, a respawn) |
| `HandAimPose(hand)` | Where the hand points, a world matrix; `Forward` is the pointing direction. | `MatrixD.Zero`, as `HandGripPose` |
| `HeadPose()` | The player's eyes in the world: the game camera's matrix. | `MatrixD.Zero`, also with no camera (the main menu) |
| `HandTrigger(hand)` | How far the trigger is pulled, 0 to 1. | `0` |
| `HandSqueeze(hand)` | How hard the grip is squeezed, 0 to 1. | `0` |
| `HandStick(hand)` | The thumbstick, `X` right and `Y` up, -1 to 1. | zero |

A hand that is not 0 or 1 is refused. A pose is a matrix in the same space as the game's camera, so positions from
`Translation` can be compared with entities'.

Raw button states are not offered: register an [action](#controller-actions), which the player can bind to the button they
like. The trigger, grip and stick values are for feel (a gauge, a pull strength), not for deciding an action.

### Pulse

`Func<int hand, double amplitude, double seconds, bool>`. Buzzes that controller. `amplitude` 0 to 1 and `seconds` 0 to 10;
outside those they are clamped (0 seconds is the shortest pulse the controller gives). It is scaled by the player's haptics
setting. True when sent; `false` while VR is not active, for a hand that is not 0 or 1, and for a value that is not a number.

## Events

`Func<string owner, string eventName, Action handler, bool> Subscribe` calls `handler` on the game thread when the event
happens, at most a frame later. The handler has no arguments: read what you need through the other members. Subscribing the
same owner to the same event again replaces its handler. Returns true when subscribed; an unknown name or a null handler is
refused. Nothing is raised for the state the game is in when you subscribe: read it yourself first.

| Event (case sensitive) | Raised when |
|---|---|
| `ActiveChanged` | `IsActive` changed: VR came on or went off. |
| `DominantHandChanged` | The player switched "Left-handed" in VR options, whether VR is active or not: `DominantHand` changed. |
| `ContextChanged` | `Context` changed. |
| `HudChanged` | `HudVisible`, `HudDepth` or `HudTangents` changed. |
| `BindingsChanged` | The player rebound something, the controller changed, or an action was registered or taken away: what `ActionLabel` says may have changed. |

`Func<string owner, string eventName, bool> Unsubscribe` takes the owner's handler for that event away; true when it had one.
`Unregister(owner)` takes all of an owner's handlers away.

## Lifetime

| | Plugin route | Mod route |
|---|---|---|
| A registration lasts | Until you take it away or the game closes: across worlds, the main menu, and a headset coming on or off | Until you take it away or **the world unloads** |
| Taken away by | `Unregister`, `UnregisterId`, `UnregisterMenuItem`, `UnregisterPanelScreen`, `Unsubscribe` | The same, and by the plugin when the world changes; the log says so |

- **`Unregister(owner)`**, `Func<string, bool>`: takes away everything the owner registered (actions, event handlers, menu
  items, panel screens). True when there was anything. The player's bindings for the actions are kept.
- **`UnregisterId(owner, id)`**, `Func<string, string, bool>`: takes away the owner's action `id`; true when it was
  registered. The player's binding is kept for when it is registered again. An id never names a menu item, a screen or an
  event: those have their own members.
- **Player bindings** for `owner.id` are saved with the player's settings, and kept even when your plugin is not installed in
  a later run, so a player who removes and reinstalls you finds their bindings.
- **A world unload** takes away what a mod registered in it, at the next frame. A mod should still call `Unregister` in
  `UnloadData`. A new world's mods register again, and their registrations are not taken for the old world's.
- **A plugin's registrations** stay across worlds: your delegates must stay valid with no world loaded (check a session, a
  grid or an entity before using it in a callback).
- **Headset off or on**: nothing is registered or unregistered. Actions that are on go off (with their `0`); items and screens
  are simply not reachable; they work again when VR does.
- **The guard's state** belongs to the owner id for the run, not to the registration: see below.
- Registration works before a world loads, with or without a headset.

## When your code throws: the guard

The callbacks you give run under a guard, one per owner id: an action's `available` and `callback`, an event handler, and a
menu item's `run`, `canUse` and `state`. The plugin has to keep running whatever your code does, and so does every other
plugin.

- An exception is caught and never reaches the game. The owner's **first** exception of the run is logged in full, with its
  stack. Each later one is a line of its own (its type and message, no stack), **at most 5 per owner in 300 seconds**; past
  that they are counted, and the count is written when the 300 seconds end. A callback that throws more thinly than the
  guard trips at (below) is never switched off, so without the limit it could write a line every few seconds for the whole
  run. Catch and log your own exceptions if you want every stack.
- **Three exceptions within ten seconds switch all of the owner's callbacks off**: for **30 seconds** the first time, **120
  seconds** the second time, and **for the rest of the session** (until the game closes) the third time. Exceptions spread
  more thinly never trip it, though every one is swallowed.
- While switched off, none of the owner's callbacks is called. An action's `available` counts as false, so the action is off;
  its menu items are greyed out and cannot be used; its event handlers are not called. Registering and unregistering still
  work. `OwnerEnabled(owner)` (`Func<string, bool>`) says `false` meanwhile; it is true for an owner with nothing registered
  yet, and false for an invalid id.
- **The guard is not reset by unregistering and registering again**, nor by registering again in another world: it belongs to
  the owner id for the run. An owner switched off for the session stays off. The reason: a callback that throws on every frame
  would otherwise switch itself back on by registering again, and flood the log and the frame time for the rest of the run.
  The fix is the fault, not another id: the player's bindings are saved under your id.
- There is no time limit on a callback: one on the game thread cannot be stopped safely once called, so the frame waits for
  it, and one that never returns hangs the game, which no guard can catch. Each callback is timed instead: one that takes
  **more than 1 ms** is logged, once per owner and callback. A frame is 11.1 ms at 90 Hz (13.9 ms at 72 Hz), nearly all of
  it the game's and the plugin's, and an action's `available` is asked every frame, so 1 ms is already enough to drop
  frames. A callback's first slow call is not logged (a method's first call includes compiling it), nor a call during which
  a garbage collection ran. Answer from a field you keep up to date rather than working it out in the callback.
- The log has a line at each switch, and when it comes back on: see [Troubleshooting](#troubleshooting).

## Fairness, and what is not offered

The plugin gives a player in VR what a player without a headset has in the same place, and nothing more. The API follows: it
describes the **player's own** head and hands, controls the player chooses, and where things are shown; it never tells you
something about the game that a flat-screen player could not learn.

- **Hand and head poses go to plugins and mods, never to scripts.** They describe the player's own body. A script running on a
  grid (in a hosted game, on the host's) has no business knowing where the camera is or where a player points: a script in a
  flat game cannot read that. This is also why scripts get no `HudToWorld`: it would tell them the camera's position and
  direction. A script that wants to know whether VR is in use has `IsActive` and `HudVisible`.
- **No raw buttons, no input injection.** You cannot read the controller's buttons directly, nor press anything for the
  player. Actions are the player's to bind, and what an action does is your code, with the powers it has from a key.
- **Menu items and screens** follow the same reach and visibility rules as the plugin's own.
- **Nothing here changes the player's settings or the plugin's behaviour.** Registering adds to what the player can bind and
  point at; the player can take any of it away.
- **Local only.** Nothing is sent over the network.

Not offered in version 2:

- Pages in the VR options for a plugin's settings (keep your own config).
- Render or per-eye hooks.
- Other players' heads or hands, remote poses, or anything about another player.
- Menus other than `ActionsWheel` and `WristPanel`.
- Anything for scripts beyond the HUD members.
- Anything over the network.

A member that is not in [All members](#all-members) does not exist yet. To ask for one, open an issue on the plugin's GitHub
page saying what you would build with it.

## Multiplayer and servers

The API is local. It describes the headset of the player whose game your code runs in.

- Each player with the plugin has their own API in their own game. A mod's client-side code asks there.
- A mod's server-side code gets nothing: there is no plugin on a dedicated server. In a game a player hosts, the host's code
  asks about the host's own headset only.
- A programmable block script runs where its grid is simulated, so in a hosted game it sees the host's headset, not the
  headset of the player who owns the block.
- Never send API values to other players to decide what they see; each player's game decides what that player sees.

## Examples

### A script agent

Paste this into a programmable block's code: the script at the top, the class below it. Every member is safe to call without
the plugin, and then says VR is not active. It looks for the API again on each call until it finds it. This example writes
what it found to the block's Custom Data.

```csharp
SevrApiAgent sevr;

public Program()
{
    sevr = new SevrApiAgent(Me);
}

public void Main(string argument, UpdateType updateSource)
{
    Me.CustomData = sevr.Available
        ? "SEVR version " + sevr.Version + ", active " + sevr.IsActive + ", HUD visible " + sevr.HudVisible
          + ", HUD depth " + sevr.HudDepth.ToString("0.00") + " m, tangents " + sevr.HudTangents.X.ToString("0.000")
          + " x " + sevr.HudTangents.Y.ToString("0.000")
        : "SEVR not available";
}

/// <summary>
/// Cheezecake's SE VR's API, for a programmable block script. Paste this class into your script and make one in Program().
/// Every member is safe to call without the plugin: it then says VR is not active. It looks for the API again on each call
/// until it finds it, since a script made as a world loads can start before the plugin offers it.
/// </summary>
class SevrApiAgent
{
    readonly IMyProgrammableBlock me;
    Func<int> version;
    Func<bool> isActive, hudVisible;
    Func<double> hudDepth;
    Func<Vector2D> hudTangents;

    public SevrApiAgent(IMyProgrammableBlock me)
    {
        this.me = me;
        Find();
    }

    /// <summary>The plugin is in the game that runs this script. Not the same as VR being active.</summary>
    public bool Available { get { if (version == null) Find(); return version != null; } }

    /// <summary>The API's version, 0 without it.</summary>
    public int Version { get { return Available ? version() : 0; } }

    /// <summary>The game that runs this script is shown in a headset now.</summary>
    public bool IsActive { get { return Available && isActive != null && isActive(); } }

    /// <summary>That headset shows the HUD now.</summary>
    public bool HudVisible { get { return Available && hudVisible != null && hudVisible(); } }

    /// <summary>Metres from the eyes at which the HUD is seen. Only meaningful while IsActive.</summary>
    public double HudDepth { get { return Available && hudDepth != null ? hudDepth() : 0.0; } }

    /// <summary>Tangents of the HUD area's half width and half height. Only meaningful while IsActive.</summary>
    public Vector2D HudTangents { get { return Available && hudTangents != null ? hudTangents() : Vector2D.Zero; } }

    void Find()
    {
        var property = me.GetProperty("SevrApi");
        var typed = property == null ? null : property.As<IReadOnlyDictionary<string, Delegate>>();
        var api = typed == null ? null : typed.GetValue(me);
        if (api == null)
            return;
        isActive = Get<Func<bool>>(api, "IsActive");
        hudVisible = Get<Func<bool>>(api, "HudVisible");
        hudDepth = Get<Func<double>>(api, "HudDepth");
        hudTangents = Get<Func<Vector2D>>(api, "HudTangents");
        version = Get<Func<int>>(api, "Version");
    }

    static T Get<T>(IReadOnlyDictionary<string, Delegate> api, string name) where T : class
    {
        Delegate member;
        return api.TryGetValue(name, out member) ? member as T : null;
    }
}
```

### A HUD agent for a mod

For a mod that draws its own HUD (the version 1 members). It does the handshake, keeps the newest copy, and returns the
"without VR" values while the API is missing, so the code that uses it never checks. Make one in a session component's
`LoadData`, `Dispose` it in `UnloadData`. It uses only what the mod whitelist allows. Change the namespace to your mod's.

```csharp
using System;
using System.Collections.Generic;
using Sandbox.ModAPI;
using VRageMath;

namespace YourMod
{
    /// <summary>
    /// Cheezecake's SE VR's HUD members, for a mod. Make one in a session component's LoadData, Dispose it in UnloadData.
    /// Every member is safe to call before the API arrives, and without the plugin: it then says VR is not active.
    /// </summary>
    public sealed class SevrApiAgent : IDisposable
    {
        public const long Channel = 6000296989824141617L;
        public const string Request = "SEVR.ApiRequest";

        private Func<int> version;
        private Func<bool> isActive, hudVisible;
        private Func<double> hudDepth;
        private Func<Vector2D> hudTangents;
        private Func<Vector2D, Vector3D> hudToWorld;

        public SevrApiAgent()
        {
            MyAPIGateway.Utilities.RegisterMessageHandler(Channel, Received);
            MyAPIGateway.Utilities.SendModMessage(Channel, Request);
        }

        public void Dispose()
        {
            MyAPIGateway.Utilities.UnregisterMessageHandler(Channel, Received);
            version = null;
            isActive = hudVisible = null;
            hudDepth = null;
            hudTangents = null;
            hudToWorld = null;
        }

        /// <summary>The API has arrived: the plugin is loaded in this game. Not the same as VR being active.</summary>
        public bool Available => version != null;

        /// <summary>The API's version, 0 before it arrives.</summary>
        public int Version => version != null ? version() : 0;

        /// <summary>The game is being shown in a headset now.</summary>
        public bool IsActive => isActive != null && isActive();

        /// <summary>The headset shows the HUD now. Hide your HUD when this is false and IsActive is true.</summary>
        public bool HudVisible => hudVisible != null && hudVisible();

        /// <summary>Metres from the eyes at which the HUD is seen. Only meaningful while IsActive.</summary>
        public double HudDepth => hudDepth != null ? hudDepth() : 0.0;

        /// <summary>Tangents of the HUD area's half width and half height. Only meaningful while IsActive.</summary>
        public Vector2D HudTangents => hudTangents != null ? hudTangents() : Vector2D.Zero;

        /// <summary>A point of the HUD area (x right, y up, each -1 to 1) to its world position. Only meaningful while IsActive.</summary>
        public Vector3D HudToWorld(Vector2D hud) => hudToWorld != null ? hudToWorld(hud) : Vector3D.Zero;

        private void Received(object message)
        {
            var api = message as Dictionary<string, Delegate>;
            if (api == null)
                return; // a request, ours or another mod's

            version = Get<Func<int>>(api, "Version");
            isActive = Get<Func<bool>>(api, "IsActive");
            hudVisible = Get<Func<bool>>(api, "HudVisible");
            hudDepth = Get<Func<double>>(api, "HudDepth");
            hudTangents = Get<Func<Vector2D>>(api, "HudTangents");
            hudToWorld = Get<Func<Vector2D, Vector3D>>(api, "HudToWorld");
        }

        private static T Get<T>(Dictionary<string, Delegate> api, string name) where T : class
        {
            Delegate member;
            return api.TryGetValue(name, out member) ? member as T : null;
        }
    }
}
```

### Drawing a HUD with it

A session component that owns one agent, and turns its HUD positions into world positions through one method. On a flat
screen it draws where it always did; in a headset, on the VR HUD.

```csharp
using Sandbox.ModAPI;
using VRage.Game;
using VRage.Game.Components;
using VRage.Utils;
using VRageMath;
using YourMod;
using BlendTypeEnum = VRageRender.MyBillboard.BlendTypeEnum;

[MySessionComponentDescriptor(MyUpdateOrder.NoUpdate)]
public class MyHud : MySessionComponentBase
{
    SevrApiAgent sevr;

    public override void LoadData() => sevr = new SevrApiAgent();

    protected override void UnloadData()
    {
        sevr?.Dispose();
        sevr = null;
    }

    public override void Draw()
    {
        if (sevr.IsActive && !sevr.HudVisible)
            return; // the helmet is open: no HUD in a headset
        MatrixD camera = MyAPIGateway.Session.Camera.WorldMatrix;

        // A 10% wide marker at the HUD's top left, drawn after the game's post-processing as HUD billboards are.
        Vector2D centre = new Vector2D(-0.8, 0.8);
        Vector3D at = Place(centre, camera);
        double width = Size(0.1, camera);
        MyTransparentGeometry.AddBillboardOriented(MyStringId.GetOrCompute("Square"), Color.White, at,
            camera.Left, camera.Up, (float)(width / 2), BlendTypeEnum.PostPP);
    }

    /// Where a HUD point is drawn: on the VR HUD in a headset, 10 cm ahead of the camera on a flat screen.
    Vector3D Place(Vector2D hud, MatrixD camera)
    {
        if (sevr.IsActive)
            return sevr.HudToWorld(hud);
        double fov = MyAPIGateway.Session.Camera.FieldOfViewAngle; // degrees, vertical
        double tanY = System.Math.Tan(MathHelper.ToRadians(fov) / 2);
        double tanX = tanY * MyAPIGateway.Session.Camera.ViewportSize.X / MyAPIGateway.Session.Camera.ViewportSize.Y;
        return camera.Translation + 0.1 * (camera.Forward + hud.X * tanX * camera.Right + hud.Y * tanY * camera.Up);
    }

    /// A width given as a fraction of the HUD area's width, in metres where Place puts it.
    double Size(double fraction, MatrixD camera)
    {
        if (sevr.IsActive)
            return fraction * 2 * sevr.HudTangents.X * sevr.HudDepth;
        double fov = MyAPIGateway.Session.Camera.FieldOfViewAngle;
        double tanX = System.Math.Tan(MathHelper.ToRadians(fov) / 2) * MyAPIGateway.Session.Camera.ViewportSize.X
            / MyAPIGateway.Session.Camera.ViewportSize.Y;
        return fraction * 2 * tanX * 0.1;
    }
}
```

The one change a HUD framework needs is the same: the place where a HUD-space position becomes a world position near the
camera goes through `HudToWorld`, and sizes through `HudTangents` and `HudDepth`, while `IsActive` is true.

## Mods that do not use the API

A HUD drawn the way Text HUD API and Rich HUD Framework draw theirs, as billboards just ahead of the camera, already shows
on the VR HUD without the mod changing anything. While VR is active, each eye takes the game frame's billboards that are all
of these:

- drawn after post-processing (`BlendTypeEnum.PostPP`, those frameworks' default for HUD elements);
- given in world space, as quads, triangles or points (not lines, not given in an object's space, not with their own
  projection);
- wholly within 45 cm of the camera, and ahead of it.

It moves each one from the screen the mod laid it out on onto the band the game's HUD has on the visor: `HudTangents` wide,
16:9, at `HudDepth`. So it is seen where the game's HUD is, turns with the head, and hides when `HudVisible` is false. Unlike
a HUD placed with `HudToWorld`, it is drawn over near geometry such as cockpit consoles. It is hidden from the desktop's
external view. The plugin changes how the eyes draw the billboards, never the mod's code.

Left as they are: billboards anchored in the world (target boxes, markers on grids), lines, other blend types, and anything
placed with `HudToWorld`, which is always at least 50 cm out.

Using the API is still better where a mod can: it places each element itself, and can hide or change what does not suit a
headset. The plugin does not patch other mods or read their private fields: such patches break without warning whenever the
other side updates. For a mod that does not look right in VR yet, change it to call this API (usually in the one place it
turns HUD coordinates into world positions), and offer the change to the mod's author.

## Troubleshooting

The plugin's log is `SpaceEngineersVR.log` in the game's user data folder: `%APPDATA%\SpaceEngineers\` on Windows,
`~/.config/SpaceEngineers/` on Linux, or the folder given to the game with `-appdata`. It is replaced at each start; the run
before it stays as `SpaceEngineersVR.prev.log`. Each line starts with the time and `[INFO]`, `[WARN]` or `[ERROR]`.

| What you see | Why |
|---|---|
| Nothing arrives on the channel | The plugin is not installed, this is a dedicated server, or the world has not finished loading. Register in `LoadData` and send the request then; the plugin's own offer follows when the world is ready. |
| The API arrives several times | Each request on the channel, from any mod, brings it again, each time a new dictionary of the same delegates. Copy out what you use from the first, and register once. |
| An entry is missing from the dictionary, or not the type its member has | Another mod changed the dictionary before it reached you: every handler of one send shares it. Send `"SEVR.ApiRequest"` for a new one. |
| A mod's handler throws on the API, and the handlers after it on the channel never get that send | The game calls the channel's handlers one at a time, in the order they registered, and catches nothing (`SendModMessage`): the exception ends the send there. It comes out of whatever sent: at world start the plugin's offer, logged as `[ERROR] SEVR API could not be offered` with that mod's stack; after a request, the requesting mod's own `SendModMessage(Channel, "SEVR.ApiRequest")` call, which throws another mod's exception into its code. Catch everything in your handler. A mod that missed a send can send the request again, but a handler that throws every time and registered before it stops that send too. |
| `SevrApi` is not found by reflection | The lookup ran in your plugin's constructor (look from `Init` or later), the plugin is not installed, or the lookup used the assembly's name instead of the type's full name. |
| `SevrClient: {owner}: Connect was called before the game started its plugins; call it from Init or Update` | `Connect` ran in a constructor. Call it from `Init`. |
| `SevrClient: {owner}: Cheezecake's SE VR is not installed; working without VR` | It is not. Every SevrClient member does nothing this run. |
| `SevrClient: {owner}: Cheezecake's SE VR is too old (API version {n}, 2 needed); working without VR` | The player has a version 1 plugin. Every SevrClient member does nothing this run. |
| `SevrClient: {owner}: connected to Cheezecake's SE VR, API version {n}` | Found. A line ending `; without {members}` lists members this version lacks or has as another type; the methods that use them do nothing. |
| `SevrClient: {owner}: the VR plugin refused the name '{name}' (see its log)` | The display name given to `Connect` was empty, too long or more than one line: the refusal line in `SpaceEngineersVR.log` says which. |
| `SevrClient: {owner}: could not read {assembly} for the API: ...` | One loaded assembly could not be searched. The search goes on; harmless unless it was this plugin's. |
| A `Register...` call returns false | The reason is in the log, one line, once: see the refusal line below. |
| An action is registered but nothing happens | It starts unbound when its suggestion clashed (see [Suggested bindings](#suggested-bindings)): the player binds it under Options > VR > Controller. Or `available` returns false, or the player is not in one of its contexts (a wheel or the wrist panel is a place of its own). |
| A callback stopped being called | Your owner was switched off by the guard: look for `switched off` in the log; `OwnerEnabled` says false. |
| A menu item is greyed out | `canUse` returns false, or your owner is switched off. |
| A mod's action or item vanished after a world change | A mod's registrations end with its world. Register again when the API arrives in the new world. |
| `IsActive` is false in a headset | The headset's session is not running (asleep, or the runtime is closing it), or the plugin has not found the headset yet. |
| `HudToWorld` returns (0, 0, 0) | There is no camera yet. Skip drawing that frame. |
| A script's `GetProperty("SevrApi")` is null | The plugin is not in the game that runs the script, or the script asked before the plugin added it. Ask again later; the script agent does. |
| A script finds the property but `As<...>()` returns null | The script asked for `Dictionary<string, Delegate>`. Ask for `IReadOnlyDictionary<string, Delegate>`. |
| A script sees VR active though its owner has no headset | Scripts run in the host's game. |

Lines to look for in the log (text in braces varies):

| Line | Meaning |
|---|---|
| `SEVR API: version 2 offered on mod channel 6000296989824141617 ("SEVRAPI1"); a mod sends "SEVR.ApiRequest" there to be sent it (docs/MOD_API.md)` | A world started and the plugin is listening. |
| `SEVR API: version 2 offered to programmable block scripts as the property "SevrApi" (docs/MOD_API.md)` | Scripts can read the property now. |
| `[WARN] SEVR API: {owner} called {member}: refused, {reason} (docs/MOD_API.md; this is logged once)` | A call was refused. `{owner}` is `(no owner)` for a member without one and `(an invalid owner id)` for a bad id. The reason says what to change. |
| `[WARN] SEVR API: 256 different refused calls logged; later ones are not` | A caller refused again and again; only the first 256 reasons are written. |
| `[ERROR] SEVR API: {owner}'s {callback} threw` and a stack | The owner's first exception of the run. `{callback}` is one of `callback for action {id}`, `available answer for action {id}`, `{event} handler`, `run for menu item {id}`, `canUse for menu item {id}`, `state for menu item {id}`. |
| `[WARN] SEVR API: {owner}'s {callback} threw again, {type}: {message} (exception {n} of the run)` | A later exception, on one line. At most 5 per owner in 300 seconds. |
| `[WARN] SEVR API: {owner}: {n} more exception(s) not written in 300 s (at most 5 lines per owner in 300 s)` | The owner threw more than 5 times after its first in 300 seconds; this many were counted and not written. |
| `[WARN] SEVR API: {owner}'s {callback} took {ms} ms, over the 1.0 ms a callback may take; it runs on the game thread, so the frame waits for it (this is logged once)` | That callback was slow a second time. Logged once per owner and callback. |
| `SEVR API caller {owner}: 3 exceptions in {seconds} s, the last {type}: {message}; switched off, back in 30 s` | The guard tripped (the second time `120 s`). |
| `SEVR API caller {owner}: 3 exceptions in {seconds} s, the last {type}: {message}; switched off for the rest of the session` | The third time. Only a restart of the game switches the owner on. |
| `SEVR API caller {owner}: switched on again ({n} exceptions so far)` | The time passed. |
| `SEVR API: the world changed; {n} registration(s) made over the mod channel in another world taken away` | A mod's registrations ended with its world. |
| `SEVR API could not be offered` / `SEVR API: the world's end or the events' frame failed` | The plugin's own offer or frame failed; the log has the exception. Each is written for the first failure of the run only; three failures within 10 s switch that part off (the offer and the scripts' property, or the frame) as [the guard](#when-your-code-throws-the-guard) does. If its stack is in a mod's message handler, that mod threw on the offer (the row on a handler that throws, above); otherwise report it. |
| `Controller bindings for {profile} name actions not registered now (a plugin or mod not loaded); kept as saved: ...` | The player has bindings for actions no one registered this run. They are kept. |
| `Registered panel screen {type}: the cursor is shown on it in VR` / `...: at the {hand} hand, {n} m from the head` | A registered panel screen took the focus. |
| `Hand hints: {hand} hand's labels shown ...` | What a hand plate shows, with your lines among them. |

## Reference

| Item | Value |
|---|---|
| Class | `SpaceEngineersVR.Api.SevrApi` (public, static) |
| Plugin method | `public static Dictionary<string, Delegate> GetApi()` |
| Plugin client | [docs/samples/plugin/Source/SevrClient.cs](samples/plugin/Source/SevrClient.cs), copied into a plugin |
| Channel | `SevrApi.Channel`, `6000296989824141617L` |
| Request | `SevrApi.Request`, `"SEVR.ApiRequest"` |
| Script property | `SevrApi.ScriptProperty`, `"SevrApi"`: an `IReadOnlyDictionary<string, Delegate>` on every programmable block |
| Version | `SevrApi.Version`, `2` (read it from the dictionary) |
| Source | [src/SpaceEngineersVR/Api/SevrApi.cs](../src/SpaceEngineersVR/Api/SevrApi.cs) |
| Mod HUD placement | [src/SpaceEngineersVR/Rendering/ModHudBillboards.cs](../src/SpaceEngineersVR/Rendering/ModHudBillboards.cs) |
