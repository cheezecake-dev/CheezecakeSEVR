# Sample plugin

A Pulsar plugin that uses the API through `Source/SevrClient.cs`, the file a plugin copies to use it (docs/MOD_API.md,
"Plugins: SevrClient.cs"), with no reference to the VR plugin. `Source/SevrSample.cs` is the sample's own code. From the
plugin's `Init` it:

- registers "Sample beacon", an action on the tools wheel suggested on the right A button, with a callback that buzzes the
  right controller (`Pulse`), and polls it every frame (`ActionStarted`, `ActionHeld`, `ActionEnded`);
- registers twenty more actions on the tools wheel, each suggested on a control of its own. Each suggestion is given only
  where it clashes with nothing (docs/MOD_API.md, "Suggested bindings"), and on the tools wheel most do clash: in a test on
  the headset emulator three were given. Bind the rest on Options > VR > Controller to see the wrist panel's Controls page
  (Tools wheel tab) shrink its rows and end with "N more on the bindings page";
- logs what each action is bound to (`ActionLabel`), and again on `BindingsChanged`;
- puts "Sample beacon" on the actions wheel with the plugin's own icon, `Assets/SevrSampleIcon.dds`: the data file names it
  as an asset and Pulsar hands the plugin its path through `LoadAssets` (docs/MOD_API.md, "A plugin's own files"); and a "Sample reload" tile on the wrist panel's Add-ons page that calls
  `Unregister` and registers everything again (the player's bindings are kept);
- subscribes to `ActiveChanged`, `ContextChanged`, `BindingsChanged`, `DominantHandChanged` and `HudChanged`, and logs the hands'
  and head's state and poses on each change.

Every line it writes to the game's log (`SpaceEngineers*.log`) starts `SevrSample: plugin:`.

## Use it

- **In Pulsar:** add this folder as a local plugin with the data file `SevrSamplePlugin.xml`, and enable it with Cheezecake's SE VR.
- **To check it compiles:** a .NET Framework 4.8 class library (x64) of `Source/`, referencing from the game's `Bin64`
  folder `Sandbox.Common`, `Sandbox.Game`, `VRage`, `VRage.Game`, `VRage.Library` and `VRage.Math` (not copied to the
  output). Pulsar does not need one: it compiles `Source/` itself.
- **To use the API in your own plugin:** copy `Source/SevrClient.cs` into it as it is, and call `SevrClient.Connect` from your
  plugin's `Init`. `SevrSample.cs` shows each kind of member in use; change the owner id, labels and suggested bindings.
