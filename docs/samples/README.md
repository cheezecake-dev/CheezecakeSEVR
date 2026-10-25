# SEVR API samples

Three small integrations with Cheezecake's SE VR's API, written from [../MOD_API.md](../MOD_API.md) alone, one for each route.
Copy the one you need. Each writes lines starting `SevrSample:` (to the game's log, or for the script to its Echo), so what
it did can be read back.

| Folder | Route | Uses |
|---|---|---|
| [plugin/](plugin/) | A Pulsar plugin, through `SevrClient.cs` (one file a plugin copies; it finds the API by reflection) | Actions with suggested bindings, a callback and polling, `ActionLabel`, `Pulse`, an actions-wheel item with the plugin's own icon (a Pulsar asset) and a wrist-panel tile, events, state and poses, `Unregister` |
| [mod/](mod/) | A mod, over the mod channel | The handshake, an action, an actions-wheel item with the mod's own icon, `IsActive` and the HUD members |
| [script/](script/) | A programmable block script | The `SevrApi` property: `Version`, `IsActive` and the HUD members |
