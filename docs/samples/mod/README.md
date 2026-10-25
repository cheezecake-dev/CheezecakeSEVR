# Sample mod

A mod (C# 6, the mod whitelist) that gets the API over the mod channel, as docs/MOD_API.md's handshake says
(`SevrSampleMod/Data/Scripts/SevrSampleMod/SevrSampleSession.cs`). When the API arrives it:

- logs `Version`, `IsActive`, `HudVisible`, `HudDepth`, `HudTangents` and whether `HudToWorld` gives a point, and logs them
  again whenever VR or the HUD comes or goes;
- registers "Sample mod hello", an action anywhere the player plays with no suggested binding (the player binds it under
  Options > VR > Controller);
- puts "Sample mod hello" on the actions wheel with the mod's own icon, `Textures/SevrSampleIcon.dds`;
- calls `Unregister` in `UnloadData`.

Every line it writes to the game's log starts `SevrSample: mod:`.

## Use it

Copy the `SevrSampleMod` folder into the game's local mods folder (`%APPDATA%\SpaceEngineers\Mods`), and add it to a world's
mods (Load Game, Edit settings, Mods). Its registrations end with the world.
