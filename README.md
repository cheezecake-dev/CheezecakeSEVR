# Cheezecake's SE VR

Play Space Engineers 1 in a PC VR headset. The game's own renderer draws the world once for each eye and hands both
pictures to your headset through OpenXR, so you get real depth, not a flat screen floating in front of you, and no
VorpX, ReShade or other injector. Your controllers become your astronaut's hands: tools sit in them and aim where
you point, you can press buttons with a fingertip, pull your welder from your hip, grab hulls and ladders, pick tools from
a wheel and open your inventory from a panel on your wrist. It is a plugin for [Pulsar](https://github.com/SpaceGT/Pulsar).

<!-- screenshot: hero shot - first-person view in a hangar, both hands visible, welder in the right hand mid-weld with sparks -->

**This is an early release.** It has been played on a Quest 3 over Link; a lot of it is new, so expect rough edges,
and please report what you find (see [Troubleshooting](#troubleshooting)).

## Requirements

- Space Engineers 1 on Windows (Steam), started through [Pulsar](https://github.com/SpaceGT/Pulsar) with its
  **Legacy** launcher (`Legacy.exe`). The plugin is built for .NET Framework; Pulsar's other launchers list it as not
  supported on that host.
- A PC VR headset with an OpenXR runtime that supports Direct3D 11.
  - **Tested:** Meta Quest 3 over Link (the Meta Horizon Link runtime).
  - **Should work, untested:** other OpenXR runtimes, such as SteamVR. Controller bindings are also offered for Valve
    Index, HTC Vive and Windows Mixed Reality controllers, but none of those has been tried yet.
- A graphics card with room to spare: the world is drawn twice every frame, so expect it to cost roughly twice what the
  game costs on a flat screen at the headset's resolution.

## Install with Pulsar

1. Install Pulsar and start Space Engineers with it once, using **`Legacy.exe`**, so you know it works.
2. Make your headset's runtime the active OpenXR runtime. For Quest over Link: in the Meta Horizon Link app on the PC,
   Settings > General > OpenXR Runtime, set it active. For SteamVR: SteamVR Settings > OpenXR, set it as the current
   runtime.
3. Connect the headset (start Link) **before** you start the game. The plugin finds the headset as the game starts.
4. Start the game with Pulsar's `Legacy.exe`. In the main menu, click **Plugins**, then **+** to add plugins.
5. Find **Cheezecake's SE VR** in the list and enable it.
6. Click **Apply** and let Pulsar restart the game. Pulsar downloads the plugin's source, compiles it, and downloads the
   official Khronos OpenXR loader (about 32 MB, checked against a pinned SHA-256). This happens once per version.
7. The game comes back up in the headset. The window on your monitor becomes a 1920x1080 mirror of the left eye.

To play flat again, disable the plugin in Pulsar's Plugins menu, or start with `-novr` on the command line.

<!-- screenshot: Pulsar's Plugins screen with Cheezecake's SE VR selected and its description showing -->

## First run

- **Recentre:** sit or stand the way you will play, look straight ahead, and **hold the left Menu button for a little
  over half a second** until you feel a firm tick. Your view jumps to your astronaut's eyes, facing the way you face. A quick tap
  of Menu still opens the game menu. Do this whenever you turn your chair or move in the room. Sitting down in a seat
  recentres onto the seat by itself, and getting up puts you back where you were on foot.
- **Play position:** Options > VR > Height > **Play position**: `Standing` (the default) or `Seated`.
  - *Standing:* your real height drives your view, and crouching for real crouches your astronaut. Calibrate your
    height once (next point).
  - *Seated:* wherever your head is when you recentre becomes your astronaut's eyes, standing tall on foot. Leaning
    down never crouches.
- **Height calibration (Standing):** set up the play area or boundary in the headset first, so the runtime knows where
  the floor is. Then Options > VR > **Calibrate height**, stand the way you will play, look straight ahead and click.
  After 3 seconds your eye height is measured and saved. **Height offset** nudges it afterwards; **Clear** forgets it.
- **Comfort:** turning is a snap turn of 30 degrees by default. Smooth turn and a comfort vignette are in Options > VR.
- **Menus:** point at a menu with your right hand (the laser). Trigger clicks, grip right-clicks, the stick scrolls. B
  or a tap of the left Menu button goes back.

<!-- screenshot: Options > VR screen in the headset, laser pointing at "Calibrate height" -->

## Controls

Button names are the Quest (Touch) controller's: A and B are on the right controller, X, Y and Menu on the left.
"Right" and "left" are for the default right-handed setup; with **Left-handed** on, your left hand points the menu
laser and holds the tools. The keyboard, mouse and a gamepad keep working alongside the controllers.

The game's own prompts are rewritten to name these buttons ("Press [Right grip] to open", "[A] to jump").

### On foot, on the jetpack, in a seat

| | On foot | Jetpack | Seat: cockpit, rover, remote control | Turret (and turret controller, searchlight) |
|---|---|---|---|---|
| Left stick | walk, the way your head faces | fly, the way your head faces | forward, back, strafe (rover: throttle, steer) | - |
| Right stick left / right | snap turn (or smooth turn) | snap turn (or smooth turn) | turn | turn |
| Right stick up / down | up: jump, down: crouch | climb, descend | pitch | pitch |
| Right trigger | primary: fire, weld, grind, drill | primary | primary | fire |
| Left trigger | secondary | secondary | secondary | - |
| Right grip | use (or grab, see [Hands](#hands)) | use (or grab) | roll right | - |
| Left grip | hold: tools wheel (or grab) | hold: tools wheel (or grab) | roll left | - |
| A | jump | up | up | - |
| B | jetpack on / off | jetpack on / off | down | - |
| X | dampeners on / off | dampeners on / off | dampeners on / off | - |
| Y | wrist panel | wrist panel | tap: tools wheel; hold 0.5 s: leave the seat | tap: tools wheel; hold 0.5 s: leave |
| Left stick click | sprint | - | lights | - |
| Right stick click | - | - | landing gear | - |
| Menu (left) | tap: game menu; hold: recentre | tap: game menu; hold: recentre | tap: game menu; hold: recentre | tap: game menu; hold: recentre |

- A passenger seat gets the buttons but not the sticks or grips: your head looks around the cabin.
- Looking through a camera block keeps the controls of the seat you are in; hold Y for half a second to come back.
- **Hand flying** (off by default, Options > VR > Controls): in a ship seat, close a grip with your hand where a flight
  stick (tool hand) or a throttle (other hand) would be, and fly by moving the controller.
- Valve Index controllers have no Menu button the game can use: recentre with Options > VR > **Recentre view** instead.

### Menus

| | |
|---|---|
| Pointing hand (right) | the laser: where it hits the menu is the mouse cursor |
| Its trigger / grip / stick up-down | left click / right click / scroll |
| B, or a tap of the left Menu button | back: closes the screen, as Escape does |
| Click a text box | an on-screen keyboard opens to type into (a real keyboard works too) |

Move the real mouse and it takes over the cursor until you point again.

### Tools wheel, actions wheel, wrist panel

- **Tools wheel** (your toolbar): on foot or on the jetpack, **hold the left grip** with your hand in empty air; in a
  seat, **tap Y**. Push the left stick at a slot, then let go of the grip to pick it (in a seat, the right trigger
  picks; B or another Y tap closes). Right stick left / right turns the toolbar page.
- **Actions wheel** (the game's dampeners, lights, helmet, HUD, broadcasting, and its Menu, View and Blueprint pages):
  click the left stick while the tools wheel is open. It opens at your left hand. Point at an entry with the left stick
  or by moving your hand toward it, then let go of the grip or pull a trigger to use it. B goes back or closes; Y or
  Menu closes.
- **Wrist panel**: on foot or on the jetpack, press **Y**, or turn your left forearm up and look at the gauntlet. Big
  buttons for Inventory, Terminal, G menu, Lights, Helmet, Dampeners, Broadcast, Symmetry and Colour. Press one with
  the laser. You can keep walking while it is open. Y again closes it, and so does looking away when you opened it
  by looking.

On foot the HUD's hotbar is hidden, since the wheel and the panel do its job; a short label names the tool you pick.
"Show toolbar on foot" in Options > VR brings it back.

<!-- screenshot: tools wheel open at the left hand with the welder slot highlighted -->
<!-- screenshot: wrist panel on the left forearm, laser on "Inventory" -->

### Hands

These are for when you are on your feet or on the jetpack; in a seat you use the sticks and buttons. Each one can be
turned off in Options > VR > Controls. A grip only does one thing: if your hand is on something below when you close
it, that wins over the tools wheel or Use.

- **Tools in your hands:** the tool or gun you hold sits in your hand and aims where your hand points: the welder,
  grinder and drill work where you point them, and guns shoot that way. Building and Use follow your hand too: a
  block goes where your hand points. A thin line and a dot show what your hand points at.
- **Two-handed tools:** close your other hand's grip on the front of the tool or gun, where the astronaut's other hand
  goes. A light tick tells you it is close enough. The tool then points along the line between your hands. Let go to
  go back to one hand. A pistol is steadied but still aims with the tool hand.
- **Holsters:** grip with your tool hand at your hip for the welder, at the other hip for the grinder, over your
  shoulder for the rifle, over the other shoulder for the hand drill (you get the best one you carry). A light tick
  tells you your hand is in a holster. To put the tool away, grip at its holster and let go there. Grip your helmet
  with either hand to open or close it; tap the side of your head with a fingertip for the suit lamp.
- **Fingertip buttons:** touch a button on a button panel with your index fingertip to press it. A jukebox's next,
  previous and pause, and a vending machine's next and previous, work the same way.
- **Grab to use:** close your grip with your hand on a seat, cryo pod, door, medical room, survival kit, cargo container
  or terminal block to use it, as the Use key would. Pull or push a door a hand's width while you hold it.
- **Grab items:** close a hand on a floating item (ore, ice, components, a dropped tool) to hold it; open your hand to
  throw it. Let go of it at your chest to put it in your inventory.
- **Grab hulls:** with no gravity, grip a ship's or station's hull and pull yourself along it. Let go and you drift
  on at the speed your hand moved you.
- **Grab ladders:** grip a ladder to get on it, then pull your hand down to climb up, or push it up to climb down.

<!-- screenshot: two-handed rifle, both hands on the gun -->
<!-- screenshot: hand at the hip drawing the welder from its holster -->

### External view

An optional small screen on your dash shows your ship (or you, on foot) from behind and above, while you stay in your
seat in first person. Turn it on in Options > VR > Display > **External view**. It costs an extra render pass; by
default it draws every other frame to save most of that. Third person itself is off by default in VR ("Allow third
person" turns it back on).

<!-- screenshot: cockpit view with the External view screen on the dash showing the ship from behind -->

## Settings

Everything is in **Options > VR** (the VR button above Credits, from the main menu or the in-game menu). Changes apply
at once, except the two marked (restart). The settings scroll with the pointing hand's stick. Every way out (OK, B,
the left Menu button, Escape) keeps and saves what you set; **Undo changes** puts everything back as it was when you
opened the screen, and **Defaults** shows the defaults. Each setting's tooltip says what it does. The file is
`%APPDATA%\SpaceEngineers\SpaceEngineersVR.cfg` (edit it only while the game is closed).

| Section | Setting | Default | What it does |
|---|---|---|---|
| Display | HUD distance, HUD width | 1.5 m, 55 deg | where the HUD floats and how wide it looks |
| | Show toolbar on foot | off | the game's hotbar on foot too |
| | Comfort vignette | 0 (off) | darkens the edges of your view while the sticks move or turn you; head movement never triggers it |
| | Allow third person | off | lets the camera key switch to third person |
| | Level chase camera | on | in third person in a ship, keeps the horizon level |
| | External view, size, at half rate | off, 0.30 m, on | the dash screen above |
| Menus | Menu distance, Menu width | 2 m, 60 deg | where menus open and how wide they look |
| | Main menu width | 85 deg | how wide the main menu looks |
| | On-screen keyboard | on | a keyboard opens when you click a text box with the laser |
| Height | Play position | Standing | Standing or Seated, see [First run](#first-run) |
| | Calibrate height, Clear | | measure your eye height (Standing) |
| | Height offset | 0 m | raises or lowers your view on top of the calibration |
| | Keep eyes out of the body | on | leaning into your astronaut's chest or backpack stops the view at the surface |
| | Crouch when you crouch | on | a real crouch crouches your astronaut (Standing only) |
| | Seat head reach | 25 cm | how far your head can move from the pilot's head in a seat |
| | Lock head to body, Head reach on foot | on, 25 cm | on foot, how far your head can move from the astronaut's head |
| Controls | Snap turn | 30 deg | how far one flick turns you (15 to 90) |
| | Smooth turn, Smooth turn speed | off, 120 deg/s | turn steadily instead of in steps |
| | Stick deadzone | 0.15 | raise it if you drift with the sticks at rest |
| | Ship stick sensitivity | 1 | how fast the right stick and grips turn a ship, turret or camera |
| | Hand flying, Stick full deflection, Throttle travel, Hand flying sensitivity | off, 25 deg, 10 cm, 1 | fly by holding a virtual stick and throttle |
| | Left-handed | off | the left hand points the laser and holds the tools |
| | Haptic strength | 100 % | controller buzz for firing, tools, placing blocks and getting hit |
| | Grab to use, Wrist panel, Wrist panel on look, Press with fingertip, Holsters, Grab items, Grab hulls, Grab ladders, Two-handed tools | all on | the [Hands](#hands) and wrist panel features, one switch each |
| | Control hints | on | short HUD reminders, such as "Hold Y to leave the seat" |
| | VR button names | on | the game's prompts name controller buttons instead of keys |
| | VR radial menus, VR actions wheel | on, on | the wheels name their buttons; the actions wheel opens at your hand |
| | Recentre view | | the same as holding the left Menu button |
| Advanced | Fallback IPD | 64 mm | eye distance used only when no headset gives the real one |
| | Mirror width, Mirror height (restart) | 1920, 1080 | the size of the game window on your monitor |

There is no render-scale setting yet. The eyes are drawn at the size your headset runtime asks for, so to make the game
cheaper, lower the render resolution in your headset's PC software (for Link, in the Meta Horizon Link app's device
graphics settings).

## Known issues and limits

- **Early release.** Much of the hands work, the wheels and the External view are new and lightly tested. Expect odd
  hand positions, gestures that need a second try, and the occasional missing prompt.
- **Only Quest 3 over Link has been tested.** Other runtimes and controllers have bindings but are untried.
- **Performance:** the world is drawn twice per frame on one GPU, with no render-scale option yet. The External view adds
  a third pass.
- **Multiplayer has not been tested.** Your arms, the tool's position in your hand and items you hold are local: other
  players see your tool where their game animates it (shots go the way your hand points). Holding a floating item is
  done on your PC only, so in multiplayer others may not see it where your hand has it.
- **Hands are for on foot.** In a seat you use the sticks and buttons; cockpit buttons cannot be pressed with a finger.
- **Third person** is off by default in VR. If you turn it on, walking the way your head faces and snap turning work in
  first person only, and a world, a save or a respawn still starts in first person.
- **Left-handed:** two-handed tools aim correctly but the astronaut's arms keep the right-handed pose, and the wrist
  panel does not open by looking at it (Y still opens it).
- **Game updates** can change the code the plugin hooks. A hook that fails is logged and skipped, and if the renderer
  cannot be hooked the game starts without VR.
- Windows, Space Engineers 1 and Direct3D 11 only.

## Troubleshooting

The plugin writes `%APPDATA%\SpaceEngineers\SpaceEngineersVR.log` (the run before is kept as
`SpaceEngineersVR.prev.log`). Please attach it to bug reports, with the game's own log from the same folder.

**No headset found / the game shows both eyes side by side on the monitor.** The log says `No headset connected`.
- Connect the headset and start Link (or SteamVR) *before* starting the game, then restart the game.
- Check that your headset's runtime is the active OpenXR runtime (see [Install](#install-with-pulsar), step 2).
- `The active OpenXR runtime does not support Direct3D 11`: switch to a runtime that does.
- `Could not load ...openxr_loader.dll`: Pulsar did not deliver the OpenXR loader. Restart the game so Pulsar checks
  its download again; Pulsar's own log (`Legacy\info.log` in the Pulsar folder) says why a download failed.
- `Loaded after the render device was created`: the plugin was not started early enough, which happens when it is
  loaded by something other than Pulsar's Legacy launcher.
- With two graphics cards, `The game is drawing on <GPU> but the headset needs the GPU ...` means the game picked the
  other card.

**The headset picture freezes for seconds or minutes (Quest Link).** Meta has warned that NVIDIA drivers newer than
591.86 can make Link freeze during gameplay, and later drivers have been reported to freeze too. That is Link and the
driver, not the plugin, and it happens in other games as well. Driver 591.86 is the version Meta names as working;
Steam Link or Virtual Desktop are alternatives.

**No sound in the headset.** The game plays sound to Windows' default output device. With Link, choose the headset
(Oculus Virtual Audio Device) as the output, ideally in the Link app's audio setting so Windows switches when Link
starts, and start Link before the game. If you switch the output while the game is running, restart the game. The game
normally mutes itself when its window is not focused; the plugin keeps the sound on while the headset is running, so
you do not need to click on the mirror window.

**A button does nothing.** The log lists each controller button press and where it happened (`Input: ...`). Turning
**Control hints** on shows short reminders the first few times.

**Turn VR off without uninstalling:** start with `-novr`, or set `enabled=false` in `SpaceEngineersVR.cfg`.

Report bugs on the [issue tracker](https://github.com/cheezecake-dev/CheezecakeSEVR/issues) with your headset, runtime, GPU, driver
version and the two logs.

## Credits

- Keen Software House, for Space Engineers.
- [Pulsar](https://github.com/SpaceGT/Pulsar) and the [PluginHub](https://github.com/StarCpt/PluginHub), which build and
  deliver the plugin.
- [Harmony](https://github.com/pardeike/Harmony) by Andreas Pardeike (MIT), which the plugin uses to hook the game.
- The [Khronos OpenXR loader](https://github.com/KhronosGroup/OpenXR-SDK-Source) (Apache 2.0), downloaded from its
  official release.

This is a separate project from Math0424's earlier SpaceEngineersVR plugin, and not related to it.

## Licence

MIT - see [LICENSE](LICENSE).
