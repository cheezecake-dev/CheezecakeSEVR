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
  launcher (`Legacy.exe`). Pulsar's Interim launcher (.NET 10) also lists the plugin, but is untested on Windows. On
  Linux, see [Linux](#linux-early). This is not for Space Engineers 2.
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

## Linux (early)

Native Linux, no Proton: Pulsar's **Interim** launcher runs the game on .NET 10, SDL3 and DXVK Native, which turns
Direct3D 11 into Vulkan. The plugin installs from the PluginHub as on Windows, and Pulsar compiles it for Linux.
How it works: [docs/LINUX.md](docs/LINUX.md).

You need:
- Space Engineers (Steam) and [Pulsar for Linux](https://github.com/SpaceGT/Pulsar/releases) (the linux-x64 build), started
  once with `Interim.bin` so it installs CometWorks' linux-compat.
- A headset with [WiVRn](https://github.com/WiVRn/WiVRn): the server on the PC, the app on the headset.
- Your distribution's **OpenXR loader** (`libopenxr-loader1`, `openxr-libs` or `openxr`). Nothing is shipped for Linux:
  the plugin uses this one.
- Your distribution's **OpenAL** (`libopenal1`, `openal-soft` or `openal`). Without it the game starts and runs without
  sound.
- **DXVK Native with a patch.** WiVRn and Monado need Vulkan extensions that DXVK Native does not enable; without them the
  plugin stops before the headset session and logs why. Until CometWorks ships the fix
  ([patch](tools/linux/dxvk-native-xr-external-fd.patch)), the setup script builds DXVK with it (one to several minutes)
  and copies it into linux-compat, keeping the originals. **Run the script again after a linux-compat update**, which
  replaces the files.

The script installs the loader and OpenAL, builds and installs DXVK, checks for WiVRn and `adb`, and with `--launcher`
adds a menu entry that starts the WiVRn server, runs `adb reverse` if a cable is attached, and starts Pulsar. Close
Pulsar first.

```sh
git clone https://github.com/cheezecake-dev/CheezecakeSEVR.git
cd CheezecakeSEVR
tools/linux/setup.sh --no-plugin --install-deps --launcher
```

Then start the game with `Interim.bin`, and in the main menu click **Plugins**, then **+**, enable **Cheezecake's SE VR**
and click **Apply**. `--check` only reports what is installed; `--help` lists the other options. Without `--no-plugin`
the script registers the clone as a development folder instead, for working on the plugin; enable only one of the two.

**Headset settings that work.** In the WiVRn app, disconnect first, turn on Extended configuration, and set Foveated
encoding to 30% or lower, Render resolution 120%, Refresh rate 90, Bitrate 200 Mbit/s. For a cable, run
`adb reverse tcp:9757 tcp:9757`, pick the USB option in the WiVRn dashboard and switch the headset's Wi-Fi off.

**Play.** Start the WiVRn server, open the WiVRn app in the headset and connect, then start the game from the launcher (or
`cd <Pulsar folder> && ./Interim.bin -bin64 <Space Engineers>/Bin64`). Pulsar compiles the plugin on that start.

You need not set an OpenXR runtime. If none is active the plugin picks the first installed of WiVRn (Flatpak, then
system), Monado and SteamVR, and logs which in `SpaceEngineersVR.log`. To use another, make it the active runtime or start
the game with `XR_RUNTIME_JSON` set to its `.json` file; your choice always wins.

**Limits.** WiVRn is the only Linux runtime played so far; Monado and SteamVR should work but are untested. Under
Proton the plugin is not offered: use native Pulsar.

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
  - a comfort grid: on the jetpack in zero g, a faint grid at your real floor that stays still around you (off by default);
  - camera shake (off by default);
  - zero-g pitch and roll.
- **Inside your helmet:** Options > VR > Display > **Helmet view** (on by default) puts you inside your astronaut's
  helmet, on foot and in a seat.
  - The helmet's padded inside and the visor's seal sit round the edge of your view, as close to your eyes as a real
    helmet's. They are lit by the light through the visor and by your suit lamp. Looking ahead, they only cover the edge
    your headset adds: everything a player without a headset sees stays in view. **Helmet interior** turns it off.
  - The helmet sits on your suit's neck ring. It turns a little with your head, about 12 degrees each way. Turn your head
    further and it turns inside the helmet, and the helmet's side comes into view, as in a real suit. Turn your body (the
    stick) to look round freely.
  - You look out through the visor's glass, tinted as real tinted glass is: it filters the light and never adds any.
    **Visor tint colour** sets the colour: Neutral, Gold (an astronaut's sun visor), Blue, Green or Rose. **Visor tint**
    sets how much light it takes, from clear to 60%; 13% is about the game's own glass.
  - Everything outside the helmet is seen through the visor, the floating screens too: the external view, a camera's
    or turret's view, menus, the wrist panel and the laser. Only the HUD on the glass and the helmet's inside are on your
    side of it. The external view and camera screens also go dark with the world in a cut and under the comfort
    vignette; menus and the wrist panel stay readable.
  - The HUD is projected onto the glass (see below). It stays with the helmet, so glancing far round looks away from it;
    **HUD fixed to the helmet** off makes it follow your head instead. It powers on with a flicker as the visor seals.
  - **Sun visor:** grip the brow of your helmet and draw your hand down to lower its gold sun visor over the glass, and
    push up to raise it. **Sun visor** sets how dark it is.
  - **Glare:** look toward the sun through the closed visor and its light scatters in the glass, a glow and a haze. Lower
    the sun visor to cut it. **Visor glare** sets how strong it is, down to none.
  - Open the helmet and the air hisses out. The glass swings up, taking its tint and the HUD with it, and the helmet
    stays round you. Close it and the air hisses back in.
  - With the helmet closed you hear yourself breathe, harder after a sprint and more strained as your suit's air runs
    low. **Breathing in the helmet** sets Off, Quiet (the default) or Normal. Only you hear it.
- **Walls:** lean your head into a wall, a canopy or a dashboard and the view fades to black rather than going through
  it. It does so in third person and while you steer a remote control too.
- **Menus:** point at them with your right hand. The trigger clicks, the grip right-clicks, the stick scrolls, and B goes
  back.
- **The HUD is on your visor:** the toolbar, suit status, block info, notifications, chat and the rest of the HUD sit
  toward the edges of your view, curved round your head like a helmet visor, with the middle of your view clear. With
  the helmet view on, the HUD is projected onto the glass as one display, laid out whole as the game lays it out. It is
  light on the glass, so it hides little of the world behind it, and like a real helmet display it is focused at the
  HUD distance, where your eyes can rest on it. Without the helmet view, each part keeps its side and corner and turns
  to face your eyes. Markers and the crosshair stay where they point. In a cockpit, the artificial horizon lies on the
  real horizon, and the altitude sits under the crosshair. To have
  the whole HUD on one flat panel ahead of you instead, turn off Options > VR > Interface > **HUD on the visor**. The
  desktop window shows the HUD flat either way.
- **Settings:** everything is in **Options > VR**, on five tabs: Display, Comfort, Controls, Hands and Interface.
- **For mod authors:** a mod that draws a HUD can find out where the VR HUD is through the plugin's API, so it reads
  right in a headset. See [docs/MOD_API.md](docs/MOD_API.md).
  - Each setting's tooltip says what it does.
  - Every way out keeps your changes. **Undo changes** puts back what you had when you opened the screen.
  - The file is `SpaceEngineersVR.cfg` in the game's own folder beside `SpaceEngineers.cfg`: `%APPDATA%\SpaceEngineers\`
    (`~/.config/SpaceEngineers/` on Linux), or the folder given to the game with `-appdata`.

## Controls

Button names are the Quest (Touch) controller's: A and B are on the right controller, and X, Y and Menu on the left.
This is the default right-handed setup. With **Left-handed** on, the left hand points the menu laser and holds the tools
and guns, and the right hand takes the front grip. With a gun or tool in hand on foot the triggers swap (the left one
fires, welds, grinds and drills; the right is the secondary) and the left stick click reloads; the datapad stows on the right forearm. Building and seats keep the buttons below.
The keyboard, mouse and a gamepad keep working alongside the controllers. The game's own prompts name these buttons, for
example "Press [Right grip] to open".

| | On foot / jetpack | Seat: cockpit, rover, remote control | Turret, searchlight | Build mode (block in hand) |
|---|---|---|---|---|
| Left stick | walk / fly, the way your head faces | forward, back, strafe (rover: throttle, steer) | - | as on foot / jetpack |
| Left stick click | sprint; jetpack in zero g, held: the right stick pitches and rolls you. Pointing at a container, or right hand on one: withdraw for the Build Planner (no sprint or roll) | lights | - | held: the right hand, right stick, A and B work the block (see Building); no sprint, no zero-g roll |
| Right stick left / right | snap turn (or smooth turn) | yaw | turn | snap turn; with the left stick click held, a push turns the block 90 deg |
| Right stick up / down | on foot: up jumps, down crouches; jetpack: up / down | pitch | pitch | as on foot / jetpack; with the left stick click held, a push moves the block farther (up) or nearer (down) |
| Right stick click | reload, with a hand weapon. Pointing at a container, or right hand on one: put the Build Planner in production; with the right grip held: deposit all | landing gear | - | large / small block |
| Right trigger | primary: fire, weld, grind, drill (Left-handed with a gun or tool: secondary) | primary | fire | place the block |
| Left trigger | secondary (with a gun: nothing; raise the gun to your eye to use its sight. Left-handed with a gun or tool: primary) | secondary | - | remove the block (creative; in survival the grinder removes blocks) |
| Right grip | use, or grab what your hand is on; on the right of your helmet: open or close it; on the left: suit lamp. At a container the use waits until you let go of the grip | hold the flight stick: tilt it to roll or pitch, twist it to yaw (see below); on your helmet: as on foot | - | as on foot: use, or grab what your hand is on |
| Left grip | in empty air: tools wheel; on something: grab; on the right of your helmet: open or close it; on the left: suit lamp; at its mouth: push to talk | hold the throttle; on your helmet: as on foot; at the mouth: push to talk | at the mouth: push to talk | as on foot / jetpack |
| A | jump; jetpack: up | up | zoom in, held | jump; jetpack: up. With the left stick click held: the block's next stage |
| B | jetpack on / off | down | zoom out, held | jetpack on / off. With the left stick click held: the previous stage |
| X | tap: dampeners on / off; hold: ping what you point at (see Ping) | tap: dampeners on / off; hold: ping where you look | - | tap: symmetry on / off; hold: next symmetry plane (creative) |
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
- **Camera block view:** the game gives a camera block, a turret or a searchlight no look-around, so its view is on a
  screen ahead of you, in the dark, at the game's field of view. Your head does not turn it; it turns only as the block
  is aimed. A sideways flick of the right stick switches camera. Hold the stick up or down to zoom, and the zoom shows on
  the screen as on a monitor. Hold Y for half a second to come back.
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
  HUD and broadcasting, plus its Menu, View and Blueprint pages, and Ping and Mark GPS.
- **Ship model**: the actions wheel's last entry holds a small hologram of your ship above your off hand. Seated it is
  the ship you are in; on foot it is the ship your pointing hand is aimed at, else the nearest within 50 m. It shows
  only ships that are yours or shared with your faction, never another player's, a neutral one or an unowned one.
  Grip it with either hand to turn it, with both to make it bigger or smaller; let go and it stays where you left it.
  Damaged blocks are red and blocks lost while it is out are dark. Point at a block on it and the real block is
  outlined in the world, with its name and integrity beside it. The same entry puts it away. Options > VR > Interface >
  **Ship model** turns the entry off.
- **Model overlays**: while the model is out, the actions wheel has a Model overlays page. Thrusters (orange), Power
  (yellow), Doors (green) and Weapons (outlined red) each switch on and off there, and the wheel stays up for the next.
  The same switches are in Options > VR > Interface.
- **Voxel material** (creative): while the voxel hand is in your hand, the actions wheel has a Voxel material page. It
  is the game's own voxel material wheel: your last-used materials on the page, each group of materials a ring of its
  own. Choosing one sets the brush's material and puts it first among the last used.
- **Ping**: hold **X** and point at something within your suit radio's range (200 m): ore the HUD marks, a block or
  ship of yours or your faction's, a player, a spot on an asteroid or a planet. A laser shows what you are on, named on
  the HUD ("Iron ore", "Damaged: Large Reactor (Miner 2)"); it tells no more than the game's own HUD, so a ship that
  is not yours or your faction's is only "Large grid", a player outside your faction only "Player", and ore is named
  only where the HUD draws its marker. Pointing at nothing in range makes no ping. Let go to ping it: a GPS on your
  HUD for a minute and a line in your faction's chat with the GPS in it, which the game of everyone in that chat adds to
  their GPS list; no plugin is needed on their side. With no faction the ping stays your own marker, unless you set the
  chat to "Faction, else global chat". Pull the pointing hand's trigger while still holding X to keep it as your own GPS
  instead.
  In a pilot seat it is where you look. A quick tap of X is still the dampeners, now as you let go. The actions
  wheel's Ping and Mark GPS do the same with X up: point, pull the trigger, or B to cancel. Options > VR > Controls >
  Ping sets how long a ping lasts and where its chat line goes, or turns the hold on X off.
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
  kicks hard (**Tool and weapon kickback** sets how hard). The game's own recoil applies only when that kick
  is weaker, and on foot it turns the gun, not the view; **Game recoil in VR** Off leaves it out in single player.
- **Reload:** click the gun hand's stick (or close your other hand's grip at the gun's magazine) and the magazine drops
  out. Left-handed, that is the left stick, and sprint moves to the right stick click while the gun is held (on the
  jetpack with zero-g roll on, the left click stays the roll's and reload stays on the right).
  Then reach to your other hand's hip, close the grip to take a fresh one, and push it up into the well under the gun until
  it clicks. The gun is loaded then, or when the game's own reload time is up if that is later: reloading by hand is never
  quicker than the game's (**Reload by hand**; off, the click reloads on the game's timer).
- **Helmet and suit lamp:** on foot or in a seat, grip the right side of your helmet, with either hand, to open or close
  it, and the left side to switch the suit lamp (in a seat too it is your suit's lamp, not the ship's lights); or tap the side of your head with a fingertip for the lamp (**Helmet and lamp by hand**). With
  **Helmet view** off, either side switches the lamp. The helmet and the lamp are also on the wrist panel and the actions wheel.
- **Push to talk:** hold the left grip with your hand at your helmet's mouth.
- **Fingertip buttons:** touch a button panel's button with your index fingertip.
- **Grab to use:** close your hand on a seat, door, terminal, cargo container, medical room or anything else the Use key
  works on.
- **Grab items:** close your hand on a floating item to hold it. Open your hand to throw it, or let go at your chest to
  stow it.
- **Grab hulls** (zero g): pull yourself along a hull or an asteroid, hand over hand. Let go with your hand moving and you
  fly off at its speed, up to **Push-off top speed** (6 m/s by default).
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
  - the screen that shows a camera block's, turret's or searchlight's view, and its zoom;
  - the death screen view, and the respawn menu with the death screen behind it;
  - the on-screen keyboard moving to the top of a text editor to keep your line clear;
  - typing amounts at a store block or ATM;
  - the actions wheel's Voxel material page (creative);
  - the wrist panel's Heal tile;
  - the HUD on the visor, on foot and in a seat, and the HUD projected on the glass;
  - the helmet's inside round the view and its travel, the visor tint, the sun visor, the glare, the HUD powering on, and
    the breathing in the helmet;
  - feeling the jetpack, the magnetic boots and hard landings in the hands;
  - each gun's shot felt as that gun (pistol, rifle, launcher), the support hand feeling it too, and the click of an empty gun;
  - the artificial horizon on the real horizon in a cockpit;
  - zero-g pitch and roll staying off while you are in gravity;
  - the view steadied on magnetic boots (on an asteroid your body and arms still rock; the view does not);
  - pitch and yaw from the held flight stick;
  - the Vive wand layout, not yet tried on real wands.
- **Mod HUDs:** mods that draw their HUD through Text HUD API or Rich HUD Framework show it too close to read in a
  headset. Those frameworks don't use the plugin's [mod API](docs/MOD_API.md) yet; the rest of the game's HUD is
  unaffected.
- **Multiplayer has not been tested.** Your arms and where you hold your tool are only shown on your own PC. Other players
  see your tool where their game animates it, but your shots still go where your hand points.
- **Hands are for on foot.** In a seat your hands hold the stick and throttle, and cockpit buttons cannot be pressed
  with a finger (the touch toolbar screen can).
- **Left-handed:** the status screen stays on the left gauntlet: the astronaut model has its screen only there.
- **Game updates** can change the code the plugin hooks. A hook that fails is logged and skipped. If the renderer
  cannot be hooked, the game starts without VR.
- Space Engineers 1 only: on Windows with Direct3D 11 through Pulsar's Legacy launcher, or on native Linux through
  Interim (see [Linux](#linux-early)).

## Reporting bugs

Open an issue on the [issue tracker](https://github.com/cheezecake-dev/CheezecakeSEVR/issues/new/choose); the bug
report form asks for what we need:

- your **headset and how it is connected**: Link cable, Air Link, Virtual Desktop or SteamVR (WiVRn on Linux);
- your **graphics card and driver version**;
- **both plugin logs**, from `%APPDATA%\SpaceEngineers\` (`~/.config/SpaceEngineers/` on Linux, or the game's `-appdata` folder):
  - `SpaceEngineersVR.log` is the last run;
  - `SpaceEngineersVR.prev.log` is the run before it. Starting the game again moves the old log there, so if you
    restarted after the problem, that is the one with it in;
- **steps to reproduce**: what you did, what happened, and what you expected.

Before you report, check these:
- **No headset found** (the log says `No headset connected`): VR starts when the headset connects, also after the game
  has started; check that your headset's runtime is the active OpenXR runtime ([Install](#install-with-pulsar), step 2).
  On a PC with two graphics cards, start Link or SteamVR before the game, so it picks the headset's card.
- **No sound in the headset:** **Sound through the headset** (Options > VR > Display) is on by default and needs a
  restart after a change. Start Link before the game. On Linux the sound moves to WiVRn's output once the headset has
  connected, which needs PipeWire's `pw-dump` and `pw-metadata`.
- **A button does nothing:** the log records each controller press (`Input: ...`), so attach it.

## Credits

- Keen Software House, for Space Engineers.
- [Pulsar](https://github.com/SpaceGT/Pulsar) and the [PluginHub](https://github.com/StarCpt/PluginHub), which build and
  deliver the plugin.
- [Harmony](https://github.com/pardeike/Harmony) by Andreas Pardeike (MIT), which the plugin uses to hook the game.
- The [Khronos OpenXR loader](https://github.com/KhronosGroup/OpenXR-SDK-Source) (Apache 2.0). Its official release
  build ships in `lib/openxr/`, with its licence.
- [ColbyJack1134](https://github.com/ColbyJack1134) and his [SpaceEngineersVR](https://github.com/ColbyJack1134/SpaceEngineersVR),
  with his permission: the particle density setting, the double-click tolerance for lists and tables, the free mouse, the
  controller pose guard, the lens-mask margin and the menu fallback when the VR view fails are adapted from his code, and
  the cockpits are his: the moving sticks, throttles, wheels and handlebars, the touchable switches, covers and handles,
  the seat panel and seat fit, and the per-cockpit flight settings. His description of the game's artificial horizon
  layout was the reference for ours.

This is a separate project from Math0424's earlier SpaceEngineersVR plugin, and is not related to it.

## Licence

MIT. See [LICENSE](LICENSE).
