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
  - **Should work, untested:** Air Link, Virtual Desktop, and SteamVR headsets.
  - **Controllers, bindings offered, untested:** Valve Index, HTC Vive (its own layout, see [Vive wands](#vive-wands)), Windows Mixed Reality, HP Reverb G2, Steam
    Frame, PICO 4, PICO 4 Ultra and PICO Neo3. Quest 3, 3S and Quest Pro controllers use the Touch bindings, the ones
    tested. None of the others has been tried yet. The bindings for the Steam Frame, PICO and HP Reverb G2 controllers
    were informed by Fuchs's
    [Space VR](https://github.com/gronkh68-rgb/space-engineers-vr) (MIT).
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

## Linux (early, one tested setup)

Native Linux, no Proton: Pulsar runs the game on .NET 10, SDL3 and DXVK Native, which turns Direct3D 11 into Vulkan.
Tested: Meta Quest 3 through WiVRn over USB, two RTX 3060s, Ubuntu 26.04.
How it works and what it took: [docs/LINUX.md](docs/LINUX.md).

You need Space Engineers (Steam), [Pulsar for Linux](https://github.com/SpaceGT/Pulsar/releases) (the linux-x64 build,
started once so it installs CometWorks' linux-compat), and a headset with [WiVRn](https://github.com/WiVRn/WiVRn). Then:

```sh
git clone https://github.com/cheezecake-dev/CheezecakeSEVR.git
cd CheezecakeSEVR
tools/linux/setup.sh --install-deps --launcher
```

The script finds Pulsar and the game, and does what is missing:
- **OpenXR loader** from your distribution (`libopenxr-loader1`, `openxr-libs` or `openxr`). `--install-deps` installs it.
- **DXVK Native with a patch.** WiVRn and Monado need Vulkan extensions that DXVK Native does not enable, so the game
  crashes when the headset session starts. Until CometWorks ships the fix
  ([patch](tools/linux/dxvk-native-xr-external-fd.patch)), the script builds DXVK with it (one to several minutes) and copies it
  into linux-compat, keeping the originals. **Run the script again after a linux-compat update**, which replaces the files.
  It skips the build when linux-compat already has the fix.
- **The plugin**, registered in Pulsar as a development folder, so it appears enabled in the plugin list.
- **WiVRn**: checks for the server and `adb`, and prints how to install them.
- `--launcher` adds a menu entry that starts the WiVRn server, runs `adb reverse` if a cable is attached, and starts Pulsar.

`tools/linux/setup.sh --check` only reports what is installed. `--help` lists the other options (`--pulsar`, `--se-bin64`,
`--no-dxvk`). Close Pulsar before running it.

**Headset settings that work.** In the WiVRn app, disconnect first, turn on Extended configuration, and set Foveated
encoding to 30% or lower, Render resolution 120%, Refresh rate 90, Bitrate 200 Mbit/s. For a cable, run
`adb reverse tcp:9757 tcp:9757`, pick the USB option in the WiVRn dashboard and switch the headset's Wi-Fi off.

**Play.** Start the WiVRn server, open the WiVRn app in the headset and connect, then start the game from the launcher (or
`cd <Pulsar folder> && ./Interim.bin -bin64 <Space Engineers>/Bin64`). Pulsar compiles the plugin on that start.

You need not set an OpenXR runtime. If none is active the plugin picks the first installed of WiVRn (Flatpak, then
system), Monado and SteamVR, and logs which in `SpaceEngineersVR.log`. To use another, make it the active runtime or start
the game with `XR_RUNTIME_JSON` set to its `.json` file; your choice always wins.

## First run

- **Recentre:** sit or stand the way you will play and look straight ahead. **Hold the left Menu button for just over
  half a second**, until you feel a firm tick. A quick tap of Menu still opens the game menu. Recentre whenever you
  turn your chair or move in the room. Sitting in a seat recentres on it by itself.
- **Play position:** Options > VR > Display > **Play position**, `Standing` (the default) or `Seated`.
  - Standing: your real height drives your view, and a real crouch crouches your astronaut.
  - Seated: your head becomes your astronaut's eyes, standing tall, wherever it is when you recentre.
- **Height (Standing):** set up the headset's play area first, so it knows where the floor is. Then go to Options > VR
  > **Calibrate height**, stand straight and click. Your eye height is measured after 3 seconds and saved. Then hold both
  arms straight out to the sides for 2 seconds: the span between your hands is saved too, and your astronaut's arms stretch
  far enough to reach where yours do (Options > VR > **Scale arms to arm span** turns that off).
- **Comfort:** turning is a 30 degree snap turn by default. On magnetic boots the view is steadied against the bumps of
  the hull or asteroid underfoot; your own turning is not slowed. Options > VR > **Comfort** also has:
  - smooth turn;
  - a comfort vignette;
  - camera shake (off by default);
  - zero-g pitch and roll.
- **Menus:** point at them with your right hand. The trigger clicks, the grip right-clicks, the stick scrolls, and B goes
  back.
- **The HUD is on your visor:** the toolbar, suit status, block info, notifications, chat and the rest of the HUD sit
  toward the edges of your view, curved round your head like a helmet visor, with the middle of your view clear. Each
  part keeps its side and corner, and turns to face your eyes. Markers and the crosshair stay where they point. To have
  the whole HUD on one flat panel ahead of you instead, turn off Options > VR > Interface > **HUD on the visor**. The
  desktop window shows the HUD flat either way.
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
| Left stick click | sprint; jetpack in zero g, held: the right stick pitches and rolls you. Pointing at a container, or right hand on one: withdraw for the Build Planner (no sprint or roll) | lights | - | held: the right hand, right stick, A and B work the block (see Building); no sprint, no zero-g roll |
| Right stick left / right | snap turn (or smooth turn) | yaw | turn | snap turn; with the left stick click held, a push turns the block 90 deg |
| Right stick up / down | on foot: up jumps, down crouches; jetpack: up / down | pitch | pitch | as on foot / jetpack; with the left stick click held, a push moves the block farther (up) or nearer (down) |
| Right stick click | reload, with a hand weapon. Pointing at a container, or right hand on one: put the Build Planner in production; with the right grip held: deposit all | landing gear | - | large / small block |
| Right trigger | primary: fire, weld, grind, drill | primary | fire | place the block |
| Left trigger | secondary (with a gun: nothing; raise the gun to your eye to use its sight) | secondary | - | remove the block (creative; in survival the grinder removes blocks) |
| Right grip | use, or grab what your hand is on; on your helmet: suit lamp. At a container the use waits until you let go of the grip | hold the flight stick: tilt it to roll or pitch, twist it to yaw (see below) | - | as on foot: use, or grab what your hand is on |
| Left grip | in empty air: tools wheel; on something: grab; on your helmet: suit lamp; at its mouth: push to talk | hold the throttle; at the mouth: push to talk | at the mouth: push to talk | as on foot / jetpack |
| A | jump; jetpack: up | up | zoom in, held | jump; jetpack: up. With the left stick click held: the block's next stage |
| B | jetpack on / off | down | zoom out, held | jetpack on / off. With the left stick click held: the previous stage |
| X | dampeners on / off | dampeners on / off | - | tap: symmetry on / off; hold: next symmetry plane (creative) |
| Y | wrist panel | tap: tools wheel; hold half a second: leave the seat | hold half a second: leave | wrist panel (the block menu is on its tile) |
| Menu | tap: game menu; hold: recentre | as on foot | as on foot | as on foot |

- **Building:** with a block in hand you keep jump and the jetpack (A and B), so you can build while flying. Hold the
  **left stick click** to work the block: twist your right hand to turn it in 90 deg steps, push the right stick sideways
  to turn it a step, push it up or down to move the block farther or nearer (the game moves a block only in creative),
  and press A or B for the block's next or previous stage. While you hold the click it is not sprint, and not the
  zero-g roll. A pending paste and the voxel hand's brush take the same click. The right grip is as on foot: closed on a
  hull it grabs it, and on a cargo container, refinery, terminal, door or seat it uses it (the game's highlight and a light
  tick show your hand is on one), so you can open an inventory without putting the block away. A fingertip still presses
  buttons; a floating item is picked up with the left hand. A refused place or remove gives a double buzz.
- **Quick take, production and dump:** point at a cargo container, connector, conveyor port, refinery, assembler or
  anything else with an inventory, where the middle mouse works in the flat game (the game's highlight shows it), with
  a welder, grinder or hand drill in your hand or with nothing in it; or rest your right hand on one (the highlight shows
  it), which is the way with Left-handed on, where the tool or gun is in your left hand. Then the
  **left stick click** takes the components your Build Planner needs, the **right stick click** puts the Build Planner in
  production in the grid's assemblers, and the **right stick click with the right grip held** deposits all (but hand tools,
  ammunition, bottles and consumables), as the game's middle mouse, Shift + middle mouse and Alt + middle mouse do there.
  The game's own messages say what happened ("All components were successfully withdrawn.", the components that are
  missing, "All possible components were deposited.", the production's success or failure). On foot and on the jetpack;
  not with a block in hand (the stick clicks are building's), nor with the colour tool, and not from a seat. A click taken
  here is not sprint, the zero-g roll or reload. At a container the right grip's use (opening the inventory) waits until
  you let go of the grip, so a deposit does not also open it; a deposit made with the grip held cancels the use. A
  container you may not use leaves the clicks as they were. A reminder shows the first time you point at one or your
  hand is on one.
- **Flying with your hands on the controls** (on by default): in a cockpit, the ship flies only while you hold its
  controls, where the astronaut's hands rest. Close your right grip near the stick and your left near the throttle. A
  light buzz tells you when a hand is close enough.
  - The throttle hand gives the left stick and A / B.
  - The stick hand gives the right stick and the triggers, and flies like a flight stick: tilt it sideways to roll,
    forward to put the nose down, back to pull it up, and twist it to yaw. The right stick's pitch and yaw work too.
    Options > VR > Controls has **Yaw from twisting the stick hand** and **Yaw from the thumbstick** (both on): turn
    either off to leave the yaw to the other. Pitch and roll by tilting are always on.
  - Let go and that hand's input stops. A control stays held for as long as the grip stays closed, wherever the hand
    goes, so it keeps working with the hand out of the headset's view.
  - X, Y, Menu and the stick clicks work without holding anything.
  - In a remote control, or a seat whose pose has no hand on a control, the grips roll instead.
- **Hand flying** (off by default, Options > VR > Controls): fly by moving the controller while you hold the stick or
  throttle.
- **Camera block view:** a sideways flick of the right stick switches camera. Hold the stick up or down to zoom, and the
  zoomed view shows on a magnified inset. Hold Y for half a second to come back.
- A passenger seat gets the buttons but not the sticks or grips.
- Valve Index controllers have no Menu button the game can use. Recentre with Options > VR > **Recentre view**.
- Steam Frame controllers: the left controller's View button is Menu, and its D-pad's down and up are X and Y. The
  right controller's X and Y, the bumpers and the D-pad's left and right do nothing. PICO and HP Reverb G2 controllers
  have the Quest's buttons in the same places.

### Vive wands

A wand has a trackpad and one Menu button where the Quest's controller has a stick and four buttons, so the Quest's
controls are made from them. The triggers and grips are as on the Quest. Each row below is one Quest control, so the
controls table above applies to it.

| Wand | Is the Quest's | Notes |
|---|---|---|
| Left trackpad, touched | left stick | walk / fly, tools wheel slots |
| Left trackpad, clicked | left stick click | |
| Left Menu | Y | wrist panel; in a seat tap: tools wheel, hold half a second: leave the seat |
| Right trackpad, touched | nothing | a resting thumb never turns you |
| Right trackpad, clicked at the left or right | right stick left / right | turn (in a seat: yaw) |
| Right trackpad, clicked at the top or bottom | right stick up / down | on foot jump / crouch, jetpack up / down. In a seat: A (up) and B (down), the thrust up and down |
| Right trackpad, clicked in the middle | right stick click | reload, landing gear, production, block size |
| Right Menu, tap | B | jetpack on / off, down thrust in a seat, back in menus |
| Right Menu, held half a second | Menu, tapped | the game menu |

- There is no X: the dampeners are on the wrist panel and the actions wheel.
- Recentre with SteamVR's own reset. The plugin follows it.
- In a seat the right pad's up and down are thrust, so pitch comes from tilting the stick hand (see **Flying with your
  hands on the controls**).
- In menus and the tools wheel, the right pad clicked up or down is the stick, and a tap of the right Menu button is B.

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
  - It opens on its middle, which is the empty hand: open it and pull a trigger straight away, and your hand is empty.
    Letting go of the grip without moving the stick only closes the wheel, so a missed grab does not holster your tool.
  - To pick a slot: push the left stick at it, then let go of the grip or pull a trigger. The stick back in the middle
    is the empty hand again, for the trigger or for letting go of the grip.
  - The right stick turns the toolbar page, and B closes the wheel without doing anything.
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
- **Suit lamp:** grip your helmet, or tap the side of your head, to switch the suit lamp. The helmet itself is on the
  wrist panel and the actions wheel.
- **Push to talk:** hold the left grip with your hand at your helmet's mouth.
- **Fingertip buttons:** touch a button panel's button with your index fingertip.
- **Grab to use:** close your hand on a seat, door, terminal, cargo container, medical room or anything else the Use key
  works on.
- **Grab items:** close your hand on a floating item to hold it. Open your hand to throw it, or let go at your chest to
  stow it.
- **Grab hulls** (zero g): pull yourself along a hull. Let go and you drift on.
- **Grab ladders:** grip a rung and pull down to climb.
- **Hands stop on glass:** your hands stop on windows and cockpit canopies, outside on foot and inside in a seat, as
  they do on hulls.
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
  - the HUD on the visor, on foot and in a seat;
  - zero-g pitch and roll staying off while you are in gravity;
  - the view steadied on magnetic boots (on an asteroid your body and arms still rock; the view does not);
  - pitch and yaw from the held flight stick;
  - the Vive wand layout, not yet tried on real wands.
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
