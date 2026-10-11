# Cheezecake's SE VR

**Early access.** Play Space Engineers 1 in a PC VR headset. The game's own renderer draws the world once for each eye
and sends both pictures to the headset through OpenXR, so you get real depth instead of a flat screen floating in front
of you, and it needs no VorpX, ReShade or other injector. Your controllers become your astronaut's hands. Tools sit in
them and aim where you point. You can press buttons with a fingertip, grab hulls and ladders, and hold the stick and
throttle to fly. Your toolbar is on a wheel in your hand, and your status is on your wrist.

It is a plugin for [Pulsar](https://github.com/SpaceGT/Pulsar), installed from the PluginHub.

<!-- SCREENSHOT 1 (hero): first-person view in a hangar, both hands visible, welder in the right hand mid-weld with
     sparks. Save as images/hero.png and replace this comment with: ![Welding in VR](images/hero.png)
     Use images/, not docs/images/: docs/ is export-ignored, so git archive would leave the picture out. -->

> **Early access:** it has been played on one setup, and a lot of it is new. Expect rough edges. Please
> [report what you find](#reporting-bugs): reports from other headsets and graphics cards help most.

## Requirements

- **Space Engineers 1** on Windows (Steam), started through [Pulsar](https://github.com/SpaceGT/Pulsar)'s **Legacy**
  launcher (`Legacy.exe`). Pulsar's other launchers list the plugin as not supported. This is not for Space Engineers 2.
- **A PC VR headset** with an OpenXR runtime that supports Direct3D 11.
  - **Tested:** Meta Quest 3 over Meta Link (cable), on an NVIDIA RTX 3060.
  - **Should work, untested:** Air Link, Virtual Desktop, and SteamVR headsets. Bindings are offered for Valve Index,
    HTC Vive and Windows Mixed Reality controllers, but none of them has been tried yet.
- **A graphics card with room to spare.** The world is drawn twice every frame. **Dynamic resolution** (on by default)
  draws the eyes at down to 70% resolution while the GPU cannot keep up. To make the game cheaper still, lower the
  render resolution in your headset's PC software (for Link, in the Meta Horizon Link app's graphics settings).

## Install with Pulsar

1. Install Pulsar and start Space Engineers with **`Legacy.exe`** once, so you know it works.
2. Make your headset's runtime the active OpenXR runtime.
   - **Link:** in the Meta Horizon Link app on the PC, Settings > General > OpenXR Runtime.
   - **SteamVR:** SteamVR Settings > OpenXR.
3. Connect the headset (start Link) **before** you start the game. The plugin looks for the headset as the game starts.
4. Start the game with `Legacy.exe`. In the main menu, click **Plugins**, then **+**.
5. Find **Cheezecake's SE VR** in the list and enable it. Click **Apply** and let Pulsar restart the game.
   - Pulsar downloads the plugin's source and compiles it. It also copies the official Khronos OpenXR loader, which
     ships in this repository and is checked by SHA-256.
   - This happens once per version.
6. The game comes back up in the headset. The window on your monitor becomes a mirror of the left eye.

To play flat again, disable the plugin in Pulsar's Plugins menu, or start with `-novr`.

## First run

- **Recentre:** sit or stand the way you will play and look straight ahead. **Hold the left Menu button for just over
  half a second**, until you feel a firm tick. A quick tap of Menu still opens the game menu. Recentre whenever you
  turn your chair or move in the room. Sitting in a seat recentres on it by itself.
- **Play position:** Options > VR > Display > **Play position**, `Standing` (the default) or `Seated`.
  - Standing: your real height drives your view, and a real crouch crouches your astronaut.
  - Seated: your head becomes your astronaut's eyes, standing tall, wherever it is when you recentre.
- **Height (Standing):** set up the headset's play area first, so it knows where the floor is. Then go to Options > VR
  > **Calibrate height**, stand straight and click. Your eye height is measured after 3 seconds and saved.
- **Comfort:** turning is a 30 degree snap turn by default. Options > VR > **Comfort** also has:
  - smooth turn;
  - a comfort vignette;
  - camera shake (off by default);
  - zero-g pitch and roll.
- **Menus:** point at them with your right hand. The trigger clicks, the grip right-clicks, the stick scrolls, and B goes
  back.
- **Settings:** everything is in **Options > VR**, on five tabs: Display, Comfort, Controls, Hands and Interface.
  - Each setting's tooltip says what it does.
  - Every way out keeps your changes. **Undo changes** puts back what you had when you opened the screen.
  - The file is `%APPDATA%\SpaceEngineers\SpaceEngineersVR.cfg`.

## Controls

Button names are the Quest (Touch) controller's: A and B are on the right controller, and X, Y and Menu on the left.
This is the default right-handed setup. With **Left-handed** on, the left hand points the menu laser and holds the tools.
The keyboard, mouse and a gamepad keep working alongside the controllers. The game's own prompts name these buttons, for
example "Press [Right grip] to open".

| | On foot / jetpack | Seat: cockpit, rover, remote control | Turret, searchlight | Build mode (block in hand) |
|---|---|---|---|---|
| Left stick | walk / fly, the way your head faces | forward, back, strafe (rover: throttle, steer) | - | as on foot / jetpack |
| Left stick click | sprint; jetpack in zero g, held: the right stick pitches and rolls you | lights | - | as on foot / jetpack |
| Right stick left / right | snap turn (or smooth turn) | yaw | turn | snap turn; with the right grip held, a push turns the block 90 deg |
| Right stick up / down | on foot: up jumps, down crouches; jetpack: up / down | pitch | pitch | as on foot / jetpack; with the right grip held, a push moves the block farther (up) or nearer (down) |
| Right stick click | reload, with a hand weapon | landing gear | - | large / small block |
| Right trigger | primary: fire, weld, grind, drill | primary | fire | place the block |
| Left trigger | secondary (with a gun: nothing; raise the gun to your eye to use its sight) | secondary | - | remove the block (creative; in survival the grinder removes blocks) |
| Right grip | use, or grab what your hand is on | hold the flight stick (see below) | - | held: the block turns with your hand in 90 deg steps |
| Left grip | in empty air: tools wheel; on something: grab; at your helmet's mouth: push to talk | hold the throttle; at the mouth: push to talk | at the mouth: push to talk | as on foot / jetpack |
| A | jump; jetpack: up | up | zoom in, held | jump; jetpack: up. With the right grip held: the block's next stage |
| B | jetpack on / off | down | zoom out, held | jetpack on / off. With the right grip held: the previous stage |
| X | dampeners on / off | dampeners on / off | - | tap: symmetry on / off; hold: next symmetry plane (creative) |
| Y | wrist panel | tap: tools wheel; hold half a second: leave the seat | hold half a second: leave | wrist panel (the block menu is on its tile) |
| Menu | tap: game menu; hold: recentre | as on foot | as on foot | as on foot |

- **Building:** with a block in hand you keep jump and the jetpack (A and B), so you can build while flying. The block's
  distance is the right grip with the right stick up or down; the game moves a block only in creative. A hand
  closed on a hull, a door or an item still grabs it, and a refused place or remove gives a double buzz.
- **Flying with your hands on the controls** (on by default): in a cockpit, the ship flies only while you hold its
  controls, where the astronaut's hands rest. Close your right grip near the stick and your left near the throttle. A
  light buzz tells you when a hand is close enough.
  - The throttle hand gives the left stick and A / B.
  - The stick hand gives the right stick and the triggers, and rolls when you tilt it sideways.
  - Let go and that hand's input stops.
  - X, Y, Menu and the stick clicks work without holding anything.
  - In a remote control, or a seat whose pose has no hand on a control, the grips roll instead.
- **Hand flying** (off by default, Options > VR > Controls): fly by moving the controller while you hold the stick or
  throttle.
- **Camera block view:** a sideways flick of the right stick switches camera. Hold the stick up or down to zoom, and the
  zoomed view shows on a magnified inset. Hold Y for half a second to come back.
- A passenger seat gets the buttons but not the sticks or grips.
- Valve Index controllers have no Menu button the game can use. Recentre with Options > VR > **Recentre view**.

### Menus

| | |
|---|---|
| Right hand's laser | where it hits the menu is the mouse cursor |
| Its trigger / grip / stick | left click / right click / scroll |
| B, or a tap of the left Menu button | back |
| Click a text box | an on-screen keyboard opens (a real keyboard works too) |
| Terminal: the other hand's grip / trigger held | moves or queues x10 / x100 (both: x1000) |

### Tools wheel, actions wheel, wrist

- **Tools wheel** (your toolbar):
  - To open it: on foot, **hold the left grip** in empty air; in a seat, **tap Y**.
  - To pick a slot: push the left stick at it, then let go of the grip or pull a trigger.
  - The middle of the wheel is the empty hand. The right stick turns the toolbar page, and B closes the wheel without
    doing anything.
- **Actions wheel**: click the left stick while the tools wheel is open. It holds the game's dampeners, lights, helmet,
  HUD and broadcasting, plus its Menu, View and Blueprint pages.
- **Wrist panel**: on foot, press **Y**. It has big buttons for Inventory, Terminal, G menu, Lights, Helmet, Dampeners,
  Relative, Heal, Broadcast, Symmetry and Colour. Press them with the laser. You can keep walking while it is open.
- **Status on the wrist**: the screen on your left gauntlet shows your suit and body status: health, energy, oxygen,
  hydrogen, inventory fill and more. Turn your forearm up to read it, as you would a watch. While it is on, the HUD
  leaves those parts out.

<!-- SCREENSHOT 2 (wrist): the tools wheel open at the left hand, or the wrist panel with the status screen on the
     gauntlet. Save as images/wrist.png and replace this comment with: ![Tools wheel and wrist](images/wrist.png) -->

### Hands

On foot and on the jetpack. Most of these can be turned off in Options > VR > **Hands**.

- **Tools in your hands:** the tool or gun aims where your hand points, and a block goes where your hand points.
- **Two-handed tools:** close your other hand on the front of the tool or gun. A rifle, grinder or drill held in one hand
  kicks hard (**Tool and weapon kickback** sets how hard).
- **Reload:** close your other hand's grip at the gun's magazine, or click the right stick.
- **Helmet and lamp:** grip your helmet to open or close it. Tap the side of your head for the suit lamp.
- **Push to talk:** hold the left grip with your hand at your helmet's mouth.
- **Fingertip buttons:** touch a button panel's button with your index fingertip.
- **Grab to use:** close your hand on a seat, door, terminal, cargo container, medical room or anything else the Use key
  works on.
- **Grab items:** close your hand on a floating item to hold it. Open your hand to throw it, or let go at your chest to
  stow it.
- **Grab hulls** (zero g): pull yourself along a hull. Let go and you drift on.
- **Grab ladders:** grip a rung and pull down to climb.
- **Touch toolbar** (in a cockpit): set a cockpit screen's script to "Touch Toolbar" (by default the middle screen gets
  it), then tap its slots with a fingertip.
- **External view** (off by default, Options > VR > Interface): a small screen on the dash shows your ship from behind.
  Grip it to move it.

<!-- SCREENSHOT 3 (cockpit): seated in a cockpit, both hands on the stick and throttle, ideally with the External view
     screen on the dash. Save as images/cockpit.png and replace this comment with:
     ![Flying with your hands on the controls](images/cockpit.png) -->

## Known issues

This is **early access**. These are the known issues and limits:

- **One tested setup.** It has only been played on a Quest 3 over Meta Link (cable) with an RTX 3060. Other headsets,
  connections and graphics cards should work, but have not been tried.
- **Link freezes on recent NVIDIA drivers.** Meta has advised that NVIDIA drivers newer than 591.86 can make Link
  freeze during play. That is Meta's notice about Link and the driver. We have not traced it to this plugin. If the
  headset picture freezes for seconds at a time, 591.86 is the driver Meta names. Air Link or Virtual Desktop are also
  worth trying.
- **Minor:** the drill's tip moves slightly while you walk.
- **Newer and less tested.** These are in, but have had little or no time in a headset yet:
  - where the zoomed view sits when you zoom a camera, turret or searchlight;
  - the death screen view, and the respawn menu with the death screen behind it;
  - the on-screen keyboard moving to the top of a text editor to keep your line clear;
  - typing amounts at a store block or ATM;
  - the actions wheel's Voxel material page (creative);
  - the wrist panel's Heal tile;
  - zero-g pitch and roll staying off while you are in gravity.
- **In a cockpit, the controller's laser can drift while the ship falls or moves fast.** A fix is in progress.
- **Multiplayer has not been tested.** Your arms and where you hold your tool are only shown on your own PC. Other players
  see your tool where their game animates it, but your shots still go where your hand points.
- **Hands are for on foot.** In a seat your hands hold the stick and throttle, and cockpit buttons cannot be pressed
  with a finger (the touch toolbar screen can).
- **Left-handed:** two-handed tools aim correctly, but the astronaut's arms keep the right-handed pose.
- **Game updates** can change the code the plugin hooks. A hook that fails is logged and skipped. If the renderer
  cannot be hooked, the game starts without VR.
- Space Engineers 1 only, on Windows with Direct3D 11, through Pulsar's Legacy launcher.

## Reporting bugs

Open an issue on the [issue tracker](https://github.com/cheezecake-dev/CheezecakeSEVR/issues/new/choose); the bug
report form asks for what we need:

- your **headset and how it is connected**: Link cable, Air Link, Virtual Desktop or SteamVR;
- your **graphics card and driver version**;
- **both plugin logs**, from `%APPDATA%\SpaceEngineers\`:
  - `SpaceEngineersVR.log` is the last run;
  - `SpaceEngineersVR.prev.log` is the run before it. Starting the game again moves the old log there, so if you
    restarted after the problem, that is the one with it in;
- **steps to reproduce**: what you did, what happened, and what you expected.

Before you report, check these:
- **No headset found** (the log says `No headset connected`): start Link or SteamVR before the game, and check that your
  headset's runtime is the active OpenXR runtime ([Install](#install-with-pulsar), step 2).
- **No sound in the headset:** **Sound through the headset** (Options > VR > Display) is on by default and needs a
  restart after a change. Start Link before the game.
- **A button does nothing:** the log records each controller press (`Input: ...`), so attach it.

## Credits

- Keen Software House, for Space Engineers.
- [Pulsar](https://github.com/SpaceGT/Pulsar) and the [PluginHub](https://github.com/StarCpt/PluginHub), which build and
  deliver the plugin.
- [Harmony](https://github.com/pardeike/Harmony) by Andreas Pardeike (MIT), which the plugin uses to hook the game.
- The [Khronos OpenXR loader](https://github.com/KhronosGroup/OpenXR-SDK-Source) (Apache 2.0). Its official release
  build ships in `lib/openxr/`, with its licence.

This is a separate project from Math0424's earlier SpaceEngineersVR plugin, and is not related to it.

## Licence

MIT. See [LICENSE](LICENSE).
