# Support guide: Cheezecake's SE VR

For players who need help, and for anyone answering them. It describes version 0.6.0 (the plugin list shows 0.6.0.0).
Every section stands alone, so a section can be quoted without the rest. Facts here come from the plugin's source and
the other documents in this repository: [README.md](../README.md), [LINUX.md](LINUX.md) and [MOD_API.md](MOD_API.md).

Contents: [What it is](#what-it-is-and-what-it-is-not) - [Requirements](#requirements) -
[Install, update, uninstall](#install-update-and-uninstall) - [Settings file](#settings-file) -
[Settings reference](#settings-reference) - [Controller bindings](#controller-bindings) -
[Using it in play](#using-it-in-play) - [Startup sequence](#startup-sequence) -
[Runtimes, headsets and performance](#runtimes-headsets-and-performance) - [Linux](#linux) -
[Troubleshooting](#troubleshooting) - [Log files](#log-files) - [Known issues](#known-issues) -
[Conflicts and the mod API](#conflicts-and-the-mod-api) - [FAQ](#faq) -
[Questions this guide doesn't answer](#questions-this-guide-doesnt-answer) - [Reporting a bug](#reporting-a-bug)

## What it is and what it is not

Cheezecake's SE VR is a plugin for Space Engineers 1 that plays the game in a PC VR headset. It is early access.

What it does:
- The game's own renderer draws the world once for each eye and sends both pictures to the headset through OpenXR. It
  needs no VorpX, ReShade or other injector.
- Your controllers become your astronaut's hands. Tools sit in them and aim where you point. You press buttons with a
  fingertip, grab hulls and ladders, and hold the stick and throttle in cockpits.
- Your toolbar is on a wheel at your left hand. Your suit and body status is on a screen on your left wrist.
- You are inside the astronaut's helmet, with the HUD on the visor.
- A window on your monitor mirrors the headset (it can be hidden).

What it does not do:
- It does not run Space Engineers 2.
- It does not give a flat player or a server any information. Anything it shows is what a player without a headset
  could see in the same place.
- It does not patch other mods or plugins. Mods get an [API](#conflicts-and-the-mod-api) instead.
- It has been played on one setup only (a Meta Quest 3 over Meta Link with an NVIDIA RTX 3060). Other headsets should
  work but are untested.
- Multiplayer has not been tested between two PCs yet. In a game hosted by a player who has the plugin, other players
  with the plugin, in VR or flat, see your head and hands move. Players without it see your character as the game animates
  it. Your shots go where your hand points.

## Requirements

Windows:
- Space Engineers 1 (Steam), started through [Pulsar](https://github.com/SpaceGT/Pulsar)'s **Legacy** launcher
  (`Legacy.exe`). Pulsar's Interim launcher also lists the plugin, but it is untested on Windows.
- A PC VR headset whose OpenXR runtime supports Direct3D 11 (the extension `XR_KHR_D3D11_enable`).
- A graphics card with room to spare: the world is drawn twice every frame.
- The Khronos OpenXR loader. The plugin brings it (version 1.1.63, x64, checked by SHA-256); you install nothing.

Linux (early, see [Linux](#linux)):
- Space Engineers (Steam) and Pulsar for Linux (the linux-x64 build), started once with `Interim.bin` so it installs
  CometWorks' linux-compat.
- A headset on WiVRn (the server on the PC, the app on the headset). WiVRn is the only Linux runtime played so far.
- Your distribution's OpenXR loader (`libopenxr-loader1`, `openxr-libs` or `openxr`) and OpenAL (`libopenal1`,
  `openal-soft` or `openal`). Without OpenAL the game runs without sound.
- DXVK Native with a patch, installed by `tools/linux/setup.sh`.

Headsets:
- Tested: Meta Quest 3 over Meta Link (cable), NVIDIA RTX 3060.
- Should work, untested: Air Link, Virtual Desktop, SteamVR headsets.
- Controller bindings are offered, but untested, for Valve Index, HTC Vive, Windows Mixed Reality, HP Reverb G2, Steam
  Frame, HTC Vive Cosmos, HTC Vive Focus 3, PICO 4, PICO 4 Ultra and PICO Neo3. Quest 3, 3S and Quest Pro controllers use the Touch bindings, the ones tested.

Not supported: Space Engineers 2; Proton on Linux (use native Pulsar).

## Install, update and uninstall

Install on Windows:
1. Install Pulsar and start Space Engineers with `Legacy.exe` once, so you know it works.
2. Make your headset's runtime the active OpenXR runtime.
   - Link: in the Meta Horizon Link app on the PC, Settings > General > OpenXR Runtime.
   - SteamVR: SteamVR Settings > OpenXR.
3. Connect the headset (start Link) before you start the game. The plugin looks for the headset as the game starts.
4. Start the game with `Legacy.exe`. In the main menu click **Plugins**, then **+**.
5. Find **Cheezecake's SE VR** in the list and enable it. Click **Apply** and let Pulsar restart the game. Pulsar
   downloads the plugin's source and compiles it; this happens once per version.
6. The game comes back up in the headset. The window on your monitor becomes a mirror of the left eye.

Install on Linux: see [Linux](#linux).

Update: Pulsar updates the plugin from the PluginHub and compiles the new version once. The plugin list shows the
version (0.6.0 shows as 0.6.0.0) and the log's first line shows it too (`SpaceEngineersVR 0.6.0.0 starting via ...`).
Your settings file is kept. On Linux, run `tools/linux/setup.sh` again after a linux-compat update, because an update
replaces the DXVK files.

Uninstall or play flat for a while:
- Disable the plugin in Pulsar's Plugins menu, or
- start the game with the argument `-novr` (the log says `VR disabled (-novr)`).
- To remove every trace, also delete `SpaceEngineersVR.cfg`, `SpaceEngineersVR.log` and `SpaceEngineersVR.prev.log` from
  the game's user folder (see [Settings file](#settings-file)).

## Settings file

Where: `SpaceEngineersVR.cfg`, in the game's own user folder, beside `SpaceEngineers.cfg`.
- Windows: `%AppData%\SpaceEngineers\`
- Linux: `~/.config/SpaceEngineers/`
- Either way, the folder given to the game with `-appdata` if you used it.

Format: plain text, one `key=value` per line. A line that starts with `#` is a comment, and so is anything after a `#`
on a value line. The plugin writes the whole file with a comment above every setting that describes it. It writes the
file when you leave Options > VR, to a temporary file first and then swaps it in, so a crash cannot leave half a file.

Value rules:
- A yes/no setting accepts `true`, `1`, `yes` or `on`, and `false`, `0`, `no` or `off`. It is written as `true` or `false`.
- A choice setting is stored by the name of the choice, as shown in the [settings reference](#settings-reference).
- A number setting must be inside its range. Percent, millimetre and centimetre settings show as such on screen but are
  stored as a fraction or in metres. For example Visor tint 13% is stored as `0.13`, and Seat head reach 25 cm as `0.25`.
- Keys are not case sensitive.

A line that cannot be used is skipped and logged, and the setting keeps its default or its earlier value:
- `Settings file line N: expected key=value, got '...'; skipped`
- `Settings file line N: unknown setting 'x'; skipped`
- `Settings file line N: <key>: <problem>; keeping '<value>'`
- `Settings file line N: '<key>' is set again; this one wins if it is valid`

Other lines about the file in `SpaceEngineersVR.log`:
- `Settings read from <path>` and `Settings saved to <path>`: normal.
- `No settings file at <path>; using the defaults`: the first run, or the file was deleted.
- `Could not read the settings file <path>; using the defaults` and `Could not save the settings to <path>`: the file is
  locked or the folder is not writable. Close other programs that hold it, and check the folder.

Keys that are not on any options screen. Leave these as they are; the options screens set them:
- `enabled` (`true`): `false` starts the game without VR. Also `-novr`. It is not in the menu, because with VR off there would be no menu to turn it back on.
- `eyeHeight` (metres, 0.3 to 3, empty when not calibrated) and `armSpan` (metres, 1 to 3, empty when not measured):
  set by **Calibrate height** and cleared by **Clear**.
- `gripFit`: how your astronaut's hand sits on each kind of controller. The **Hand fit** sliders set it.
- A few more keys remember where you moved the external view screen and your cockpit seat and stick positions.

Reset:
- One setting: change it in Options > VR.
- All settings: Options > VR > **Defaults** sets every setting on every tab to its default. Leaving the screen keeps
  them; **Undo changes** brings back what you opened it with.
- A broken file or a fresh start: close the game, delete `SpaceEngineersVR.cfg`. The next start uses the defaults and writes a new file.
- Calibration only: Options > VR > **Clear** forgets the height and arm span.
- Hand placement only: Options > VR > Hands > **Reset hand fit**.

Command-line arguments, which override the file for that run only and are never saved:

| Argument | Effect |
|---|---|
| `-novr` | Start without VR. |
| `-vrnodesktop` | Hide the desktop window for this run. |
| `-vrmirror WxH` | Desktop window size, for example `-vrmirror 1280x720`. |
| `-ipd metres` | Fallback IPD, for example `-ipd 0.063`. |
| `-hudDistance metres`, `-hudWidth degrees` | HUD distance and width. |
| `-menuDistance metres`, `-menuWidth degrees` | Menu distance and width. |

A value outside a setting's range is moved to the nearest end of the range.

## Settings reference

All settings are in the game's **Options > VR** screen, on five tabs: Display, Comfort, Controls, Hands and Interface. A
sixth, Controller, is the bindings page ([Controller bindings](#controller-bindings)).
Each setting's tooltip says what it does. Every way out of the screen (OK, Back, Escape, the close button) keeps your
changes and saves the file.

Buttons on the screen, outside the tabs:
- **Calibrate height**: stand straight and click. After a 3 second countdown your eye height is measured and saved. Then
  hold both arms straight out to the sides: after a 3 second countdown the span between your hands is measured over
  2 seconds and saved (unless **Scale arms to arm span** is off). The headset needs a known floor: set up the play area
  or boundary first, or the screen says `No floor known: set up the play area or boundary in the headset`.
- **Clear**: forgets the calibration. Your view height is the headset's own again and your arms stretch as far as they always have.
- **Recentre view**: recentres the view on all axes (same as holding the left Menu button).
- **Defaults**: every setting on every tab to its default. **Undo changes** puts back what you opened the screen with.
- **OK**: saves and closes.

How to read the tables:
- **Label** is the name on screen. A label ending in "(restart)" takes effect at the next start of the game.
- **Config key** is the name in `SpaceEngineersVR.cfg`.
- **Values** is the allowed range or the list of choices. A yes/no setting shows as `true/false`. Where the screen shows
  a percentage or other unit, the range is given in that unit in brackets.
- **Default** is the stored default, in the units of the file.
- Groups named "(from ColbyJack1134's SpaceEngineersVR)" hold settings whose behaviour is carried over from that project.
  Cockpit flight sticks are one of them.

Hands tab, group **Hand fit for <controllers in use>**: six sliders that place your astronaut's hand on your controller.
They are stored per controller type in the `gripFit` key. **Reset hand fit** forgets the fit for the controllers in use.

| Label | Range |
|---|---|
| Turn pitch | -90 to 90 deg |
| Turn roll | -90 to 90 deg |
| Turn yaw | -90 to 90 deg |
| Shift side | -50 to 50 cm |
| Shift up | -50 to 50 cm |
| Shift back | -50 to 50 cm |


### Display tab

**Display > Play position**

| Label | Config key | Values | Default | What it does |
|---|---|---|---|---|
| Play position | `playPosition` | Standing / Seated | Standing | Standing: your real height drives your view. Seated: your head's height at recentre becomes your astronaut's eyes. |
| Height offset | `heightOffset` | -0.5 to 0.5 m | 0 | Raises (+) or lowers (-) your view on top of the calibration. |
| Scale arms to arm span | `armSpanScaling` | true/false | true | Calibrate height also measures your arm span, and your astronaut's arms stretch to match. Off: Calibrate height measures height only. |
| Crouch when you crouch | `crouchWithHead` | true/false | false | On foot, in Standing play, crouch for real (head 30 cm or more below standing for a moment) and your character crouches, standing up when you do. |

**Display > Helmet**

| Label | Config key | Values | Default | What it does |
|---|---|---|---|---|
| Helmet view | `helmetView` | true/false | true | You are inside your astronaut's helmet, on foot and in a seat: padded interior, tinted visor glass and the HUD on the glass. Off: none of this. |
| Helmet interior | `helmetInterior` | true/false | true | The padded inside of the helmet and the visor's seal round the edge of your view. Needs Helmet view. |
| HUD fixed to the helmet | `hudOnHelmet` | true/false | true | The HUD stays with the helmet, which turns only a little with your head. Off: the HUD turns with your head. Needs Helmet view and HUD on the visor. |
| Visor tint colour | `visorTintColour` | Neutral / Gold / Blue / Green / Rose | Neutral | Colour of the visor glass. It only takes light away, never adds any. Needs Helmet view. |
| Visor tint | `visorTint` | 0 to 0.6 (screen: 0 to 60%) | 0.13 | How much light the visor glass takes away: 0 is clear, 13% is about the game's own glass. Needs Helmet view. |
| Sun visor colour | `sunVisorColour` | Neutral / Gold / Blue / Green / Rose | Gold | Colour of the sun visor. Needs Helmet view. |
| Sun visor | `sunVisorTint` | 0.2 to 0.85 (screen: 20 to 85%) | 0.6 | How much light the sun visor takes away when lowered, on top of the glass. Needs Helmet view. |
| Visor glare | `visorGlare` | 0 to 1 (screen: 0 to 100%) | 1 | How strong the sun's glare in the closed visor is; 0 turns it off. Needs Helmet view. |
| Frost on the visor | `visorFrost` | true/false | true | Frost grows in from the closed visor's rim where the game's temperature is Freeze or Extreme freeze, and melts in the warm. Needs Helmet view. |
| Breath on the visor | `visorBreath` | true/false | true | Each breath mists the inside of the closed visor in the cold or when breathing hard. Needs Helmet view. |
| Weather on the visor | `visorWeather` | true/false | true | Rain, snow and dust storms land on the closed visor outdoors; a roof or closed cockpit keeps them off. Needs Helmet view. |
| Wipe the visor | `visorWipe` | true/false | true | A fingertip drawn across the outside of the visor wipes it. Off: the visor clears only by itself. Needs Helmet view. |
| Helmet air | `helmetAirVolume` | 0 to 3 (screen: 0 to 300%) | 1.5 | Loudness of the air as the helmet opens and closes, as a share of the game's air vent sound; 0 is silent. Needs Helmet view. |
| Breathing in the helmet | `helmetBreathing` | Off / Quiet / Normal | Normal | The breathing you hear with the helmet closed. Normal is the game's own volume. Off still lets you hear yourself choke when the suit is out of air. |

**Display > External view**

| Label | Config key | Values | Default | What it does |
|---|---|---|---|---|
| External view size | `externalViewSize` | 0.15 to 0.6 m | 0.3 | Width of the external view screen in the headset. |
| External view at half rate | `externalViewHalfRate` | true/false | true | Draws the external view every other frame. Saves most of its cost. |
| Move the external view by hand | `moveViewByHand` | true/false | true | Grip the external view screen with either hand to move it. Off: it stays on the dash. |

**Display > Headset**

| Label | Config key | Values | Default | What it does |
|---|---|---|---|---|
| Refresh rate | `refreshRate` | Runtime default | Runtime default | The headset's display rate. Runtime default leaves it as the headset has it; the other choices are the rates the headset offers, listed once VR is running. A headset whose runtime has no refresh-rate control ignores this. Takes effect at once. |
| Dynamic resolution | `dynamicResolution` | true/false | true | Draws the eyes at a lower resolution, down to 70% each way, while the GPU cannot keep up; back up when it can. A frame the GPU misses is shown again, which can feel like judder. |
| Sound through the headset (restart) | `headsetAudio` | true/false | true | The game's sound plays through the headset's own audio device when the runtime names one, and on the system default output while the game is played in the window because the headset is gone. Off: the system default output. On Linux with WiVRn the sound moves to WiVRn's PipeWire output (needs pw-dump and pw-metadata). |
| Fallback IPD | `ipd` | 0.04 to 0.09 metres (screen: 40 to 90 mm) | 0.064 | Distance between the eyes, used only when no headset gives the real one. |

**Display > Headset (from ColbyJack1134's SpaceEngineersVR)**

| Label | Config key | Values | Default | What it does |
|---|---|---|---|---|
| Lens mask | `lensMask` | true/false | true | Skips drawing the corners of each eye the lenses never show, which saves GPU time. Those corners are black in the desktop window. |
| Particle density | `particleDensity` | 0.15 to 1 (screen: 15 to 100%) | 1 | Share of the game's continuous GPU particles (dust, smoke, sparks, thruster flames, weather) that is made. Lower frees GPU time in busy scenes. |

**Display > Performance**

| Label | Config key | Values | Default | What it does |
|---|---|---|---|---|
| Headset layers in the desktop window | `mirrorLayers` | true/false | true | The desktop window shows the visor, HUD, menus, laser and external view over the eye. Off: it shows the eye alone and the headset's frames take a little less time. |

**Display > Third person**

| Label | Config key | Values | Default | What it does |
|---|---|---|---|---|
| Allow third person | `thirdPerson` | true/false | false | Lets the camera-mode key switch your view to third person. Worlds, saves and respawns always start in first person in VR. |
| Level chase camera | `levelChaseCamera` | true/false | true | Third person in a ship: the camera keeps the horizon level and turns only with the ship's heading. Needs Allow third person. |

**Display > Desktop window**

| Label | Config key | Values | Default | What it does |
|---|---|---|---|---|
| Desktop view | `desktopView` | true/false | true | The game window on the monitor mirrors the headset. Off: the window is hidden (on Linux it stays, black) and costs no GPU time; a hidden window takes no keyboard or mouse. |
| Mirror width (restart) | `mirrorWidth` | 640 to 3840 px | 1920 | Width of the game window on your monitor. Takes effect at the next start. |
| Keep the game on its monitor | `keepMonitor` | true/false | true | With more than one monitor, the game opens on the monitor it was last set to, even when Windows lists the monitors in another order since. Off: the game takes the monitor at the place in that list it saved. Takes effect at the next start. |
| Mirror height (restart) | `mirrorHeight` | 360 to 2160 px | 1080 | Height of the game window on your monitor. Takes effect at the next start. |

**Display > Desktop window (from ColbyJack1134's SpaceEngineersVR)**

| Label | Config key | Values | Default | What it does |
|---|---|---|---|---|
| Mouse can leave the window | `freeMouse` | true/false | true | The game does not confine the mouse to its window while VR runs. Windows only. |

### Comfort tab

**Comfort > Turning**

| Label | Config key | Values | Default | What it does |
|---|---|---|---|---|
| Snap turn | `snapTurn` | 15 to 90 deg | 30 | How far one flick of the right stick turns you. With Zero-g pitch and roll on Snap, also one pitch or roll step. |
| Smooth turn | `smoothTurn` | true/false | false | Turn steadily while the right stick is held sideways, instead of in snap steps. |
| Smooth turn speed | `smoothTurnSpeed` | 30 to 360 deg/s | 120 | Turn speed with the right stick pushed fully over, when Smooth turn is on. Half push, half speed. |
| Zero-g pitch and roll | `zeroGTurn` | Smooth / Snap / Off | Smooth | On the jetpack in zero g, hold the left stick click and the right stick pitches and rolls you. Smooth: steadily at the Smooth turn speed. Snap: one Snap turn step a flick. Off: the click does nothing on the jetpack. |

**Comfort > Motion**

| Label | Config key | Values | Default | What it does |
|---|---|---|---|---|
| Comfort vignette | `comfortVignette` | 0 to 1 | 0 | Darkens the edges of your view while the thumbsticks move or turn you. 0 turns it off. Moving your head never triggers it. |
| Comfort grid | `comfortGrid` | true/false | false | On the jetpack in zero g, a faint grid at your real floor that stays still around you. |
| Fade on camera cuts | `comfortFade` | true/false | true | The view fades from black after an instant cut (entering or leaving a seat, camera or turret, a respawn, a jump, a teleport, standing up in gravity). Off: the view cuts straight over. |
| Camera shake | `cameraShake` | Off / Low / Full | Off | How much of the game's view shake reaches you (tools, damage, explosions, thrust, head bob). Off: the view moves only with your head. Low: a quarter. Full: as the game has it. |

**Comfort > G-forces in a seat**

| Label | Config key | Values | Default | What it does |
|---|---|---|---|---|
| Pushed into the seat | `seatGPush` | true/false | true | In a seat, your view eases a few centimetres back into the seat when the ship accelerates, brakes or turns hard. |
| Grey-out under G | `seatGGreyOut` | true/false | true | In a seat, from about 2 g held for a few seconds, the edges of your view dim inward; the middle stays clear. |
| Breathing hard under G | `seatGBreathing` | true/false | true | In a seat with the helmet closed, your astronaut breathes hard from about 2 g and for a while after. Needs Breathing in the helmet. |

**Comfort > Head**

| Label | Config key | Values | Default | What it does |
|---|---|---|---|---|
| Lock head to body | `headLock` | true/false | true | On foot, your head can only move so far from your character's head; the view stops at the reach below. Off: the view goes where your head goes. |
| Head reach on foot | `headReach` | 0.1 to 1 metres (screen: 10 to 100 cm) | 0.25 | With Lock head to body on, how far forward your head can move from your character's head. Sideways it is four fifths of this. |
| Keep eyes out of the body | `bodyClearance` | true/false | true | On foot, the view stops at the surface of your character's chest, shoulders or backpack instead of going inside. Off: the view goes through the body. |
| Seat head reach | `seatReach` | 0.1 to 1 metres (screen: 10 to 100 cm) | 0.25 | In a seat, how far forward, up or down your head can move before the view slows to a stop. Sideways it is four fifths of this. |

### Controls tab

**Controls > Controllers**

| Label | Config key | Values | Default | What it does |
|---|---|---|---|---|
| Left-handed | `leftHanded` | true/false | false | The left hand points the menu laser and holds tools and guns; the right hand takes the front grip. Its trigger fires and its stick click reloads. Building and seats keep their buttons. |
| Stick deadzone | `stickDeadzone` | 0.05 to 0.4 | 0.15 | How far a thumbstick must be pushed before it counts. Raise it if you drift with the sticks at rest. |
| Ship stick sensitivity | `shipStickSensitivity` | 0.25 to 3 | 1 | How fast the right stick and the grips turn a ship, turret or camera. 1 matches a gamepad pushed all the way. |

**Controls > Cockpits (from ColbyJack1134's SpaceEngineersVR)**

| Label | Config key | Values | Default | What it does |
|---|---|---|---|---|
| Cockpit sticks | `colbyCockpit` | true/false | true | In a cockpit with flight sticks, the sticks move with your hands. Squeeze a grip at one to take hold. Off: the seat flies with the thumbsticks and grips as on a gamepad. |
| Thumbstick controls in cockpits | `colbyThumbsticks` | true/false | false | With Cockpit sticks on, the thumbsticks also fly or drive as on a gamepad. Off: only the cockpit's own sticks, wheel and handlebars do. With Cockpit sticks off, the thumbsticks always fly. |
| Twist the cockpit sticks | `colbyStickTwist` | true/false | true | Twisting a held cockpit stick yaws the ship (right stick) or moves it up and down (left stick). |
| Tap to hold the cockpit sticks | `colbyTapHoldSticks` | true/false | false | A quick squeeze of the grip by a cockpit stick keeps hold of it without squeezing; squeeze again to let go. |
| Tap to hold analog levers | `colbyTapHoldLevers` | true/false | false | A quick squeeze of the grip on a cockpit handle keeps hold of it without squeezing; squeeze again to let go. |
| Fly with the wheel and handlebars | `colbyVehicleFlight` | true/false | false | In a rover, buggy or speeder seat, the wheel or handlebars yaw the vehicle as a ship. Off: they steer the wheels. A cockpit's own Flight Settings override this for that cockpit. |
| Two-hand pitch and roll on handlebars | `colbyBarTilt` | true/false | false | Speeder handlebars, both grips held: raise or lower both hands to pitch and tilt the bars to roll. Needs Fly with the wheel and handlebars. |
| Wheel pitch and roll | `colbyWheelMotion` | true/false | false | Rover or buggy wheel: push and pull to pitch, turn to roll. Needs Fly with the wheel and handlebars. |

**Controls > Ping**

| Label | Config key | Values | Default | What it does |
|---|---|---|---|---|
| Hold X to ping | `pingHoldX` | true/false | true | Hold X and point (in a seat, look) at something within 200 m, then let go to ping it. Off: X is the dampeners as you press it; the actions wheel's Ping and Mark GPS still work. |
| Ping lasts | `pingSeconds` | 10 to 180 s | 60 | How long your own ping marker stays on the HUD. |
| Ping chat | `pingChat` | Faction only / Faction, else global chat / Off | Faction only | Where a ping's chat line goes: your faction's channel only, your faction's else global chat, or nowhere (the marker is then only yours). |

### Hands tab

**Hands > Grab**

| Label | Config key | Values | Default | What it does |
|---|---|---|---|---|
| Grab to use | `grabToUse` | true/false | true | Closing the grip with your hand on anything the Use key works on uses it (seats, terminals, doors, cargo, medical rooms, stores and more). |
| Grab items | `grabItems` | true/false | true | On foot, a grip on a floating item holds it; open the hand to throw it; let go at your chest to stow it. |
| Grab hulls | `grabHulls` | true/false | true | In zero g, grip a hull or asteroid to pull yourself along it, hand over hand with both hands. |
| Push-off top speed | `pushOffMaxSpeed` | 1 to 8 m/s | 6 | The fastest you fly off when you let go of a hull or asteroid with your hand moving. |
| Grab ladders | `grabLadders` | true/false | true | A grip on a ladder gets you on it; pull down to climb up, push up to climb down. |

**Hands > Touch**

| Label | Config key | Values | Default | What it does |
|---|---|---|---|---|
| Press with fingertip | `fingerPress` | true/false | true | On foot, touching a push-button (button panels, jukebox buttons) with an index fingertip presses it. |
| Helmet and lamp by hand | `helmetAndLamp` | true/false | true | Grip the right side of your helmet to open or close it, the left side for the suit lamp (with Helmet view off, either side is the lamp). Tap the side of your head with a fingertip for the lamp. |
| Hands stop on glass | `handsStopOnGlass` | true/false | true | Your hands stop on window glass and cockpit canopies as on hulls. Off: they pass through glass. |

**Hands > Tools**

| Label | Config key | Values | Default | What it does |
|---|---|---|---|---|
| Two-handed tools | `twoHandedTools` | true/false | true | Close your other hand's grip on the front of the tool or gun to hold it with both hands. Off: one hand only. |
| Laser on hand tools | `toolLaser` | true/false | true | A welder, grinder or hand drill in your hand shows a thin laser from its tip as far as it reaches, with a dot where it meets something it can work on. Off: no laser. |
| Two-hand grip stays on | `twoHandGripStays` | true/false | true | Once the other hand has taken the front grip, the astronaut's hand stays on it while you hold the grip button, however far your real hand moves (aim eases away beyond 15 cm). |
| Reload by hand | `physicalReload` | true/false | true | Reload hand weapons with your hands: click the gun hand's stick, take a fresh magazine from your hip and push it in. Off: the click reloads on the game's timer. |
| Tool and weapon kickback | `kickback` | Off / Light / Full | Full | One-handed rifles, launchers, grinders and hand drills kick. Light: half as hard. Off: no kick. Two hands keep it steady. |
| Game recoil in VR | `gameRecoil` | Auto / Off (comfort) | Auto | Auto: the game's own recoil is left out of a shot only when the kickback is at least as strong. Off (comfort): left out always, single player only. |

**Hands > Guns (from ColbyJack1134's SpaceEngineersVR)**

| Label | Config key | Values | Default | What it does |
|---|---|---|---|---|
| Aim down the sights | `colbyAimDownSights` | true/false | true | Raise a gun and look along its sights to aim down them (tighter shots, slower walk, no sprint). Your view is not zoomed. |

**Hands > Haptics**

| Label | Config key | Values | Default | What it does |
|---|---|---|---|---|
| Haptic strength | `hapticStrength` | 0 to 1 (screen: 0 to 100%) | 1 | How hard the controllers buzz (fire, weld, grind, drill, build, hits, heartbeat). 0 turns all of it off. |
| Heartbeat | `hapticHeartbeat` | true/false | true | A slow double pulse in both controllers while health or suit oxygen is low. |
| Movement | `hapticMovement` | true/false | true | Jetpack rumble, magnetic boot clunks and hard landings felt in both hands. Scaled by Haptic strength. |
| G-forces in a seat | `hapticSeatG` | true/false | true | In a seat, a steady press in both controllers from about 1 g. Scaled by Haptic strength. |

### Interface tab

**Interface > Interface parts**

| Label | Config key | Values | Default | What it does |
|---|---|---|---|---|
| Status on the wrist | `wristStatus` | true/false | true | A screen on your left gauntlet shows suit and body status (bars, icons, inventory fill). The HUD leaves those parts out. Off: no screen, and the HUD shows them as the game does. |
| Toolbar on the HUD | `showToolbarOnFoot` | true/false | false | Keeps the HUD's bottom toolbar on foot and on the jetpack. Off: left out there; seats, camera views, the G menu and the terminal still show it. |
| Inventory toolbar without a headset | `inventoryToolbarFlat` | true/false | true | Without a headset, the inventory screen shows the toolbar along its bottom, worked with the mouse, as it does in VR. Off: the game's own inventory screen when there is no headset. |
| Control hints | `controlHints` | true/false | true | Short first-time reminders on the HUD (menus, tools wheel, wrist panel, moving inventory items). |
| External view | `externalView` | true/false | false | A small screen on the dash shows your ship from behind, or one of its cameras. Costs a third scene pass. Its size and half-rate options are under Display. |

**Interface > HUD**

| Label | Config key | Values | Default | What it does |
|---|---|---|---|---|
| HUD distance | `hudDistance` | 0.5 to 5 m | 1.5 | How far ahead of your eyes the HUD floats. You see the change once the menu is closed. |
| HUD width | `hudWidth` | 20 to 120 deg | 55 | How wide the HUD looks, in degrees of your view. You see the change once the menu is closed. |
| HUD on the visor | `hudVisor` | true/false | true | The HUD's parts sit on the visor with the middle of your view clear. Off: the whole HUD on one flat panel ahead of you. |
| HUD text size | `hudTextSize` | 0.8 to 2 (screen: 80 to 200%) | 1 | How large the HUD's parts on the visor are drawn. Needs HUD on the visor. |
| HUD text backing | `hudTextBacking` | 0 to 0.9 (screen: 0 to 90%) | 0 | Darkens behind the HUD text on the visor so it reads against a bright sky; 0 draws none. Needs HUD on the visor. |
| HUD colour | `hudColour` | Game's colours / Green / Amber / Cyan | Game's colours | Filters the HUD on the visor to a helmet display's Green, Amber or Cyan. Needs HUD on the visor. |
| HUD opacity | `hudOpacity` | 0.1 to 1 (screen: 10 to 100%) | 1 | How strongly the HUD on the visor shows; lower fades it. Needs HUD on the visor. |
| Suit power failing | `suitPowerFailing` | true/false | true | As the suit battery runs low, the HUD flickers and dims, and at zero goes dark with the wrist screen; the suit lamp sputters. Off: the HUD and lamp are only as the game draws them. |
| Low stat warnings at the HUD's edge | `lowStatWarnings` | true/false | true | A small flashing icon at the HUD's edge for health, food, energy, oxygen and hydrogen at the game's own critical mark, never sooner. |

**Interface > Menus**

| Label | Config key | Values | Default | What it does |
|---|---|---|---|---|
| Menu distance | `menuDistance` | 0.5 to 6 m | 2 | How far ahead of your head a menu is put. |
| Menu width | `menuWidth` | 20 to 120 deg | 60 | How wide a menu looks, in degrees of your view. |
| Main menu width | `mainMenuWidth` | 40 to 110 deg | 85 | How wide the main menu looks. Menus in a world use Menu width. |
| On-screen keyboard | `vrKeyboard` | true/false | true | Clicking a text box with the laser opens an on-screen keyboard. Off: only a real keyboard types. |

**Interface > Wrist panel**

| Label | Config key | Values | Default | What it does |
|---|---|---|---|---|
| Wrist panel | `wristPanel` | true/false | true | On foot and on the jetpack, Y opens the quick-actions panel on your left wrist; Y again closes it. Off: Y does nothing there. |
| Wrist panel size | `wristPanelSize` | 0.2 to 0.5 m | 0.3 | Width of the quick-actions panel when it opens on your left wrist. |

**Interface > Prompts**

| Label | Config key | Values | Default | What it does |
|---|---|---|---|---|
| VR button names | `vrButtonNames` | true/false | true | Game prompts name the controller buttons (for example [Right grip]) instead of keys. Off: the keyboard's names. |
| Button labels by the hands | `handHints` | Off / Until learned / Always | Until learned | Look at a hand and a label beside it names what each of its buttons does in what you are doing now. Until learned: a button's line fades each time you use it and is gone after three uses; **Show the labels again**, under the group, brings them back. Always: every line, every time. The wrist panel's Controls page lists them all either way. |

**Interface > Wheels**

| Label | Config key | Values | Default | What it does |
|---|---|---|---|---|
| VR radial menus | `vrRadialMenu` | true/false | true | The wheels say which controller buttons work them, and the actions wheel leaves out what has no use in VR. Off: the game's wheels as on a gamepad. |
| VR actions wheel | `vrActionsWheel` | true/false | true | Clicking the left stick on the tools wheel opens the game's actions as a wheel at your hand. Off: the game's own gamepad radial screen. |

**Interface > Cockpit**

| Label | Config key | Values | Default | What it does |
|---|---|---|---|---|
| Touch toolbar on the cockpit screen | `touchToolbar` | true/false | true | A cockpit screen set to the script Touch Toolbar shows the ship's toolbar to tap. With no screen set, it goes on the most central screen while you pilot. |

**Interface > Ship model**

| Label | Config key | Values | Default | What it does |
|---|---|---|---|---|
| Ship model in your hand | `shipModel` | true/false | true | The actions wheel's Ship model entry holds a small hologram of your ship above your off hand (only ships that are yours or your faction's). Off: no entry on the wheel. |
| Ship model: thrusters | `shipModelThrusters` | true/false | true | The ship model shows thrusters in orange. Also on the Model overlays page of the actions wheel. |
| Ship model: power | `shipModelPower` | true/false | true | The ship model shows power producers in yellow. Also on the Model overlays page. |
| Ship model: doors | `shipModelDoors` | true/false | true | The ship model shows doors, airlocks and hangar doors in green. Also on the Model overlays page. |
| Ship model: weapons | `shipModelWeapons` | true/false | true | The ship model outlines guns and turrets in red. Also on the Model overlays page. |

## Controller bindings

Button names are the Quest (Touch) controller's: A and B are on the right controller, X, Y and Menu on the left. Every
other controller is mapped onto that layout, so the tables in this section apply to all of them.

**Options > VR > Controller** is the bindings page: the controller in your hands, drawn, and every action with the
controls it is bound to. **Add binding** waits for every control to be let go, takes the next one you press, and asks how
it is used: press, tap, hold (with its time) or, where the controller senses a resting finger, touch. A binding can be
taken away, and an action put back to its defaults. A binding that cannot work as set shows red, its tooltip saying why.
OK, **Undo changes** and **Defaults** keep, take back or reset the bindings with the other settings. Your bindings are
saved per controller in `controllerBindings`; the tables below are the defaults. Other settings that change the controls:
- **Left-handed** (Options > VR > Controls > Controllers) swaps the main hand, see below.
- **Hold X to ping** and **Wrist panel** (Options > VR) turn the matching button use off.
- **Stick deadzone**, **Snap turn**, **Smooth turn** and **Ship stick sensitivity** change how sticks feel.
The keyboard, mouse and a gamepad keep working alongside the controllers. The game's own prompts name these buttons, for
example "Press [Right grip] to open" (**VR button names**).

### Controls by context (default, right-handed)

| | On foot / jetpack | Seat: cockpit, rover, remote control | Turret, searchlight | Build mode (block in hand) |
|---|---|---|---|---|
| Left stick | walk / fly, the way your head faces | forward, back, strafe (rover: throttle, steer); not in a cockpit with its own sticks (see Cockpit sticks) | - | as on foot / jetpack |
| Left stick click | sprint; on the jetpack (in zero g or gravity), held: the right stick pitches and rolls you; tapped: it keeps pitching and rolling you until the next click. Pointing at a container, or right hand on one: withdraw for the Build Planner (no sprint or roll) | lights | - | held: the right hand, right stick, A and B work the block (see Building); no sprint, no zero-g roll |
| Right stick left / right | snap turn (or smooth turn) | yaw; not in a cockpit with its own sticks | turn | snap turn; with the left stick click held, a push turns the block 90 deg |
| Right stick up / down | on foot: up jumps, down crouches; jetpack: up / down | pitch; not in a cockpit with its own sticks | pitch | as on foot / jetpack; with the left stick click held, a push moves the block farther (up) or nearer (down) |
| Right stick click | reload, with a hand weapon. Pointing at a container, or right hand on one: put the Build Planner in production; with the right grip held: deposit all | landing gear | - | large / small block |
| Right trigger | primary: fire, weld, grind, drill (Left-handed with a gun or tool: secondary) | primary | fire | place the block |
| Left trigger | secondary (with a gun: nothing; raise the gun to your eye to use its sight. Left-handed with a gun or tool: primary) | secondary | - | remove the block (creative; in survival the grinder removes blocks) |
| Right grip | use, or grab what your hand is on; on the right side of your helmet: open or close it; on the left side: suit lamp; on the front: sun visor (see above). At a container the use waits until you let go of the grip | take the cockpit's right stick, wheel or handlebar (see Cockpit sticks); holding nothing: roll right; on your helmet: as on foot | - | as on foot: use, or grab what your hand is on |
| Left grip | in empty air: tools wheel; on something: grab; on the right side of your helmet: open or close it; on the left side: suit lamp; on the front: sun visor | take the cockpit's left stick or throttle; holding nothing: roll left; on your helmet: as on foot | - | as on foot / jetpack |
| A | jump; jetpack: up | up | zoom in, held | jump; jetpack: up. With the left stick click held: the block's next stage |
| B | jetpack on / off | down | zoom out, held | jetpack on / off. With the left stick click held: the previous stage |
| X | tap: dampeners on / off; hold: ping what you point at (see Ping) | tap: dampeners on / off; hold: ping where you look | - | tap: symmetry on / off; hold: next symmetry plane (creative) |
| Y | wrist panel | tap: tools wheel (wrist turned up and read: wrist panel); hold half a second: leave the seat | hold half a second: leave | wrist panel (the block menu is on its tile) |
| Menu | tap: game menu; hold: recentre | as on foot | as on foot | as on foot |

With **Left-handed** on, the left hand points the menu laser and holds tools and guns, and the right hand takes the front
grip. With a gun or tool in hand on foot the triggers swap (the left one fires, welds, grinds and drills; the right is the
secondary) and the left stick click reloads. Sprint moves to the right stick click while the gun is held. Building and
seats keep the buttons in the table.

Timings (from the source):
- Menu button: a tap is under 0.6 s. Held 0.6 s it recentres, with a light ramp in the left controller and a firm tick
  when it takes. Held-for-recentre never opens the game menu.
- Y in a seat or turret: held 0.5 s leaves the seat.

Building (a block in hand): hold the left stick click to work the block. Twist your right hand to turn it in 90 degree
steps, push the right stick sideways to turn it a step, push it up or down to move it farther or nearer (creative only),
and press A or B for the block's next or previous stage. While you hold the click it is not sprint and not the zero-g roll.

Quick take, production and dump: point at a cargo container, connector, conveyor port, refinery, assembler or anything with
an inventory (or rest your right hand on one), with a welder, grinder or hand drill in your hand or nothing in it. The
left stick click takes the components your Build Planner needs, the right stick click puts the Build Planner in
production, and the right stick click with the right grip held deposits all (but hand tools, ammunition, bottles and
consumables). On foot and on the jetpack only, not with a block in hand.

Cockpit sticks (Options > VR > Controls > Cockpits (from ColbyJack1134's SpaceEngineersVR)): in one of the game's cockpits,
or a mod's cockpit built on one whose mod tells the plugin so, the sticks in the seat move with your hands. Reach a hand to one and squeeze the grip to take hold. Tilt the right stick to
pitch and roll and twist it to yaw. Push and pull the left one to move and twist it to go up and down. In a rover, buggy or
speeder, take the wheel or handlebars the same way. A grip that holds nothing rolls the ship. The thumbsticks fly such a
cockpit too only with **Thumbstick controls in cockpits** on. A remote control and a seat without sticks of its own (a
modded cockpit its mod has not told the plugin about) fly on the thumbsticks. A passenger seat gets the buttons but not the sticks or grips.

Camera block view: a camera block, turret or searchlight gives no look-around, so its view is on a screen ahead of you.
A sideways flick of the right stick switches camera. Hold the stick up or down to zoom. Hold Y for half a second to come back.

### Menus

| | |
|---|---|
| Right hand's laser | where it hits the menu is the mouse cursor |
| Its trigger / grip / stick | left click / right click / scroll |
| The mouse | moves the cursor over the whole menu as on a flat screen, wherever you look; it keeps it until the laser's trigger or grip is pressed on the menu |
| B, or a tap of the left Menu button | back |
| Click a text box | an on-screen keyboard opens (a real keyboard works too) |
| Terminal: the other hand's grip / trigger held | moves or queues x10 / x100 (both: x1000) |
| Inventory: a tool or gun dragged onto the toolbar at the bottom | goes in that slot (on the slot in your hand: you hold it) |
| Inventory: the grip on a tool or gun, then the hand pulled back | you hold it |

### Bindings per OpenXR interaction profile

The plugin suggests bindings for every profile below, each on its own, so a runtime that does not know a profile skips it
and the others are unaffected. The runtime picks the profile per hand from the controller actually in use. The log says
which: `OpenXR left hand interaction profile: <profile>` and the same for the right hand. The log also lists what is bound:
`OpenXR bound sources (active after a sync): ...`.

All profiles bind: the grip pose and aim pose, the haptic output, and (when the runtime offers `XR_EXT_palm_pose`) the palm pose.

Paths are given as input names below `/user/hand/left` and `/user/hand/right`.

| Profile | Extension the runtime must list | Trigger | Grip (squeeze) | Stick | Stick click | A, B (right) | X, Y (left) | Menu |
|---|---|---|---|---|---|---|---|---|
| `/interaction_profiles/oculus/touch_controller` (Quest 3 Touch Plus, 3S and Quest Pro controllers; the tested layout) | none | `trigger/value` | `squeeze/value` | `thumbstick` | `thumbstick/click` | `a/click`, `b/click` | `x/click`, `y/click` | left `menu/click` |
| `/interaction_profiles/bytedance/pico4_controller` (PICO 4) | `XR_BD_controller_interaction` | `trigger/value` | `squeeze/value` | `thumbstick` | `thumbstick/click` | `a/click`, `b/click` | `x/click`, `y/click` | left `menu/click` |
| `/interaction_profiles/bytedance/pico_neo3_controller` (PICO Neo3) | `XR_BD_controller_interaction` | `trigger/value` | `squeeze/value` | `thumbstick` | `thumbstick/click` | `a/click`, `b/click` | `x/click`, `y/click` | `menu/click` on both hands |
| `/interaction_profiles/bytedance/pico_ultra_controller_bd` (PICO 4 Ultra) | `XR_BD_ultra_controller_interaction` | `trigger/value` | `squeeze/value` | `thumbstick` | `thumbstick/click` | `a/click`, `b/click` | `x/click`, `y/click` | left `menu/click` |
| `/interaction_profiles/hp/mixed_reality_controller` (HP Reverb G2) | `XR_EXT_hp_mixed_reality_controller` | `trigger/value` | `squeeze/value` | `thumbstick` | `thumbstick/click` | `a/click`, `b/click` | `x/click`, `y/click` | `menu/click` on both hands |
| `/interaction_profiles/htc/vive_cosmos_controller` (HTC Vive Cosmos) | `XR_HTC_vive_cosmos_controller_interaction` | `trigger/value` | `squeeze/click` | `thumbstick` | `thumbstick/click` | `a/click`, `b/click` | `x/click`, `y/click` | left `menu/click` |
| `/interaction_profiles/htc/vive_focus3_controller` (HTC Vive Focus 3) | `XR_HTC_vive_focus3_controller_interaction` | `trigger/value` | `squeeze/click` | `thumbstick` | `thumbstick/click` | `a/click`, `b/click` | `x/click`, `y/click` | left `menu/click` |
| `/interaction_profiles/valve/index_controller` (Valve Index) | none | `trigger/value` | `squeeze/value` | `thumbstick` | `thumbstick/click` | right `a/click`, `b/click` | left `a/click` is X, left `b/click` is Y | left `trackpad/force`: the left trackpad pressed firmly |
| `/interaction_profiles/valve/frame_controller_valve` (Steam Frame) | `XR_VALVE_frame_controller_interaction` | `trigger/value` | `squeeze/value` | `thumbstick` | `thumbstick/click` | right `a/click`, `b/click` | left `dpad_down/click` is X, left `dpad_up/click` is Y | left `view/click` |
| `/interaction_profiles/htc/vive_controller` (HTC Vive wands) | none | `trigger/value` | `squeeze/click` | `trackpad` | `trackpad/click` | none: made from the trackpad and Menu, see below | none | `menu/click` on both hands |
| `/interaction_profiles/microsoft/motion_controller` (Windows Mixed Reality) | none | `trigger/value` | `squeeze/click` | `thumbstick` | `thumbstick/click` | right `trackpad/click`: the lower half is A, the upper B | left `trackpad/click`: the lower half is X, the upper Y | `menu/click` on both hands |
| `/interaction_profiles/khr/simple_controller` (fallback every runtime has) | none | `select/click` | none | none | none | none | none | `menu/click` on both hands |

Notes:
- On the Touch, PICO, Reverb, Cosmos, Focus 3 and Index rows, a control the controller does not have gets no binding. A
  Windows Mixed Reality controller's trackpad, clicked, gives its A, B, X and Y. The simple controller has no A, B, X or Y,
  no grip and no stick.
- Steam Frame: the right controller's X and Y, its Menu, both bumpers and the D-pad's left and right do nothing. PICO,
  HTC Vive Cosmos, HTC Vive Focus 3 and HP Reverb G2 controllers have the Quest's buttons in the same places. The Cosmos's
  bumpers (`shoulder/click`) are bound for the bindings page and do nothing by default.
- Quest 3, 3S and Quest Pro controllers use the `oculus/touch_controller` bindings.
- The simple controller is what a Quest gives when it is tracking your hands and not your controllers. The log then says
  `OpenXR hands: the Quest is tracking hands, not controllers (controllers asleep or hand tracking on): pick up the controllers`.
- Touch sensors (a finger resting on a control) are bound as extras on the Touch, PICO, Focus 3, Index, Steam Frame, Vive
  and Mixed Reality (trackpad touch) profiles, and not on the Reverb G2, Cosmos or simple profiles. They inform the hand
  pose, not a button.
- A binding the runtime refuses costs only that control. The log says `The runtime did not take the bindings for <profile>`
  or `OpenXR binding <path> left out`.
- Untested hardware: every profile except the Touch one has been offered but not tried.

### Vive wands

A wand has a trackpad and one Menu button where the Quest's controller has a stick and four buttons, so the Quest's controls
are made from them. The triggers and grips are as on the Quest. Each row is one Quest control, so the controls table applies to it.

| Wand | Is the Quest's | Notes |
|---|---|---|
| Left trackpad, touched | left stick | walk / fly, tools wheel slots |
| Left trackpad, clicked | left stick click | |
| Left Menu, tap | Y | wrist panel; in a seat: tools wheel |
| Left Menu, held | B (on foot) / Y held (in a seat) | jetpack on / off (held a third of a second); in a seat, held half a second: leave the seat |
| Right trackpad, touched | nothing | a resting thumb never turns you |
| Right trackpad, clicked at the left or right | right stick left / right | turn (in a seat: yaw) |
| Right trackpad, clicked at the top or bottom | right stick up / down, and A (top) / B (bottom) held | on foot jump / crouch, jetpack up / down. In a seat the thrust up and down, at a turret zoom in and out, as the spectator up and down, with the colour tool the medium and large brush |
| Right trackpad, clicked in the middle | right stick click | reload, landing gear, production, block size; in an inventory, moves an item to the other side. With the left trackpad held while building: the next block stage |
| Right Menu, tap | X tapped, B | dampeners on / off (on foot, on the jetpack and in a seat), back in menus; with the left trackpad held while building, the previous block stage |
| Right Menu, held half a second | Menu, tapped | the game menu |
| Right Menu held, then the right trigger | X held | aim a ping while the trigger is held, let go to ping; pull the left trigger as well (the right Menu still held) to mark a GPS instead |
| Left trackpad held while building, then the left trigger | X while building | tap: symmetry on / off; hold half a second: the next symmetry plane |

- There is no X button: its jobs are the rows above. Press the right Menu just before the trigger: held on its own for
  half a second it opens the game menu.
- Recentre with SteamVR's own reset. The plugin follows it.
- In a seat the right pad's up and down are thrust, so in a cockpit pitch comes from tilting its right stick (see
  [Cockpit sticks](#controls-by-context-default-right-handed)).
- In menus and the tools wheel, the right pad clicked up or down is the stick, and a tap of the right Menu button is B.
- The Vive layout has not been tried on real wands yet.

## Using it in play

First minute:
1. Connect the headset first, then start the game (see [Install](#install-update-and-uninstall)).
2. Recentre: sit or stand the way you will play and look straight ahead. Hold the left **Menu** button for 0.6 seconds,
   until you feel a firm tick. A quick tap of Menu opens the game menu instead. Recentre whenever you turn your chair or
   move in the room. Sitting in a seat recentres on it by itself.
3. Play position: Options > VR > Display > **Play position**, `Standing` (the default) or `Seated`. Seated makes your head
   your astronaut's eyes wherever it is when you recentre.
4. Standing: set up the headset's play area so it knows where the floor is, then Options > VR > **Calibrate height**.
   Stand straight and click, wait for the 3 second countdown, then hold your arms straight out for the arm span.
5. Comfort: turning is a 30 degree snap turn by default. Options > VR > Comfort has smooth turn, a comfort vignette, a
   zero-g comfort grid, camera shake and the G-force options.

Menus: point with your right hand. The trigger clicks, the grip right-clicks, the stick scrolls, B goes back. Clicking a
text box opens an on-screen keyboard (**On-screen keyboard**); a real keyboard works too.

Tools wheel (your toolbar):
- On foot, hold the left grip in empty air to open it. In a seat, tap Y. It opens at your left hand and stays there.
- It opens on its middle, which is the empty hand. Push the left stick at a slot, then let go of the grip or pull a trigger.
- The right stick turns the toolbar page. B closes the wheel without doing anything.
- Click the left stick while it is open for the **actions wheel**: dampeners, lights, helmet, HUD, broadcasting, the
  game's Menu, View and Blueprint pages, Ping and Mark GPS, and a ship model.

Wrist: on foot, press **Y** for the wrist panel: along its top **Game menu**, **Controls** (what every button does),
**VR settings** and **HUD**; under them Inventory, Terminal, G menu, Lights, Helmet, Heal, Dampeners, Relative, Broadcast,
Symmetry, Colour and External view. Press its buttons with the laser. In a seat, turn your wrist up to read it and tap Y
(a tap anywhere else is the tools wheel), or pick the actions wheel's **Quick actions** for the same panel with the ship's
switches. Turn your left forearm up to read the status screen (health, energy, oxygen, hydrogen, inventory fill).

Helmet:
- Grip the right side of your helmet with either hand to open or close it; the left side switches the suit lamp. Tap the
  side of your head with a fingertip for the lamp too.
- Grip the front of the helmet and draw your hand down to lower the sun visor; push up to raise it. It only comes down with
  the helmet closed.
- Draw a fingertip across the front of the closed visor to wipe frost, water, snow and dust.

Hands and tools:
- Close your other hand on the front of a tool or gun for a two-handed hold. A rifle, grinder or drill in one hand kicks.
- Reload by hand: click the gun hand's stick, the magazine drops out, reach to your other hip (or lower, belt height or
  below), pull that hand's trigger to take a fresh one, and keep it pulled while you push it up into the gun until it clicks.
- Aim down the sights by raising a gun and looking along it. A mod's gun does so once its mod gives the plugin its sights.
- Touch a push-button with your index fingertip to press it. Close your hand on a seat, door, terminal or container to use it.
- In zero g, grip a hull or asteroid to pull yourself along. Let go with your hand moving to fly off.
- Grip a ladder rung and pull down to climb.
- Inventory: drag a tool or gun from the inventory onto the toolbar at the bottom, or squeeze the grip on it and pull your hand back.

Ping: hold **X** and point at something within your suit radio's range (200 m). Let go to ping it: a marker on your HUD for
60 seconds and a line in your faction's chat with the GPS. Pull the pointing hand's trigger while holding X to keep it as
your own GPS. In a pilot seat it is where you look. A quick tap of X is still the dampeners.

Voice chat: use the game's voice activation (Options, Audio). There is no push-to-talk gesture.

Flying: in a cockpit, reach to the stick or throttle and squeeze the grip to take hold. See
[Controller bindings](#controller-bindings) for every button.

## Startup sequence

What happens, in order, and what each failure looks like. Lines are from `SpaceEngineersVR.log`, which is rewritten on
every start (the previous run is kept as `SpaceEngineersVR.prev.log`).

1. Pulsar compiles the plugin and starts the game. The plugin's first hook runs before the game's own startup. The log
   begins `SpaceEngineersVR 0.6.0.0 starting via <host> (before the game started)`, then `Plugin assembly: ...` and `Args: ...`.
   - Failure: `SpaceEngineersVR could not set up before the game; continuing without VR`, or `VR setup failed; continuing without VR`.
     The game runs flat. Attach the log.
   - If the plugin loaded too late, the log has a line beginning `Loaded after the render device was created` and nothing
     is patched. Start the game through Pulsar's Plugins list as in the install steps.
2. Settings are read: `Settings read from <path>` or `No settings file at <path>; using the defaults`.
3. `VR disabled (-novr)` if you passed `-novr` or the file says `enabled=false` (the same line for both). Nothing else runs.
4. The OpenXR loader is loaded: `OpenXR loader: <path>`. On Windows it is the loader Pulsar delivered
   (`Pulsar asset ...`). On Linux it is your distribution's loader.
   - Failure: `Could not load <path> (<reason>)`. On Linux the reason reads `the system has no libopenxr_loader.so.1 and the
     plugin folder has none. Install the OpenXR loader: libopenxr-loader1 (Debian, Ubuntu), openxr (Arch), openxr-libs (Fedora)`.
   - `Pulsar delivered no 'openxr_loader' asset` or `The OpenXR loader in use (...) is not the one Pulsar
     delivered; check Placement="Bin" in the plugin data file`: the plugin's data file or Pulsar's download is wrong.
     Update the plugin and try again.
5. The OpenXR runtime is found (see [Runtimes](#runtimes-headsets-and-performance)). The runtime must offer Direct3D 11
   (`XR_KHR_D3D11_enable`), or on Linux the Vulkan extension `XR_KHR_vulkan_enable`.
   - Failure: `The active OpenXR runtime does not support Direct3D 11 (XR_KHR_D3D11_enable)`.
   - Success: `OpenXR instance created (extensions: ...)`.
6. The headset is looked for on a background thread, every 2 seconds, for the whole run.
   - No runtime running: `The OpenXR runtime is not running; VR starts when it is running and the headset connects`
     (on Linux: `The OpenXR runtime is not running (start the WiVRn server); VR starts when ...`).
   - Runtime up, no headset: `No headset connected; VR starts when one connects`.
   - Runtime failed: `The OpenXR runtime failed (<message>); trying again every 2 s`.
   - Found: `Headset: <name>, eye <width>x<height>, GPU LUID <id>, ...`.
   - On Windows the game waits for this answer as long as it takes. On Linux it waits 15 seconds, and if there is no answer
     it logs `No OpenXR answer from the runtime after 15 s (WiVRn waits for the headset to connect); starting with the
     game's own picture, and VR starts when the headset connects`.
7. If OpenXR cannot be used at all: `OpenXR is not usable on this machine, so no headset can be found; nothing is patched and the
   game runs as usual`. The game plays flat for the run.
8. The patches are applied: `Patched: ...` and `Patches: N applied, failed: [...]`. A failed optional feature is named in a
   message box in the main menu; a failed core feature turns VR off for the run. See [Troubleshooting](#troubleshooting).
   If no headset was found yet, what changes play is patched when it connects, and the log says
   `Headset found: patching what changes play`.
9. The game picks the headset's graphics card: `Drawing on the headset's GPU: <name> (adapter N, video settings say M)`.
   - Failure: `The headset's GPU (LUID <id>, DXGI index N) is not in the game's adapter list`. VR stays off for the run
     (the game plays flat, and the log says to restart with the headset connected).
   The window is then set to the VR mirror: `VR mode: mirror window <W>x<H>, adapter N (user setting ... kept in config)`. Your
   own video settings stay in the game's config.
   - If the game has no saved video settings yet: `Game has no saved video settings yet; stereo starts from the next launch`.
     Start the game once more.
10. Linux only: the plugin checks that DXVK Native has the Vulkan extensions OpenXR needs. If not:
    `DXVK lacks the Vulkan extensions OpenXR needs (stock linux-compat DXVK); run tools/linux/setup.sh to install the patched one`.
11. The session is created on the game's graphics device: `OpenXR session created: eyes <W>x<H> <format>` (on Linux
    `..., on Vulkan through DXVK`). Swapchains follow (`OpenXR panel swapchain`, `OpenXR visor swapchain`).
    - A failure that cannot get better until the game restarts (wrong GPU, a DXVK without the extensions) ends in
      `<message>; playing flat. Restart the game with the headset connected to play in VR`.
    - Another failure: `Could not start the headset session (<message>); trying again once the headset is found again`.
      After repeated failures: `Could not start the headset session; playing flat for the rest of the run`.
12. The session moves through its states: `OpenXR session <state> -> <state>` for each change: Idle, Ready, Synchronized, Visible, Focused. In
    Focused the headset shows the game and the controllers work (`OpenXR controllers active`).
13. Controllers: `OpenXR input ready: action set 'gameplay' attached, bindings suggested for N interaction profile(s)`, then
    `OpenXR left hand interaction profile: ...` and the same for the right.
    - Failure: `OpenXR controller input could not be set up; continuing without controllers`.
14. Sound moves to the headset (unless **Sound through the headset** is off): `Sound: ...` lines.

If the headset is lost during play: `Headset lost (<reason>); playing in the window until it reconnects`. Two seconds
later, if it has not come back, `Headset gone for 2 s (no session): playing flat, the game's picture in the 1920x1080 window, until the headset session has the focus again`: the window keeps its size, and shows the game's own picture fitted to it,
with the menus laid out for it and the game's mouse cursor. The HUD says "Headset lost: playing in the window". The
sound moves to the Windows default output: `Sound: to the Windows default output (no headset is connected); the game makes
its audio again`. (Link's headset audio device stays in Windows while the headset is disconnected, so without this the
sound went on into it and nothing was heard.) As in the game without the plugin, its own audio option to mute when the
window loses focus silences it while the window is in the background: click the window. When the headset reconnects, `The picture turns to
VR's (headset back)`, VR comes back by itself, and so does the sound: `Sound: back to <device> (headset back)`.

## Runtimes, headsets and performance

The plugin talks OpenXR only. Whatever runtime is the active OpenXR runtime on the PC is the one it uses.

Windows:
- Meta Quest over Link (cable): Meta Horizon Link app, Settings > General > OpenXR Runtime. This is the tested setup.
- SteamVR headsets: SteamVR Settings > OpenXR. Untested.
- Air Link and Virtual Desktop: untested.
- Start Link or SteamVR before the game. On a PC with two graphics cards this lets the game pick the headset's card
  (the log says `Drawing on the headset's GPU: ...`).
- The runtime must list `XR_KHR_D3D11_enable`. Otherwise the log says
  `The active OpenXR runtime does not support Direct3D 11 (XR_KHR_D3D11_enable)`.

Linux:
- WiVRn is the only runtime played so far. Monado and SteamVR should work but are untested.
- You need not set a runtime. If none is active the plugin picks the first installed of WiVRn (Flatpak, then system),
  Monado and SteamVR, and logs `OpenXR runtime: none chosen, using <name> (<path>). Set XR_RUNTIME_JSON or an active
  runtime to use another.` Your own choice (an active runtime, or `XR_RUNTIME_JSON` set to a runtime's `.json` file) always wins.
- If none is found: `OpenXR runtime: none chosen and none found (looked for WiVRn, Monado and SteamVR). Install WiVRn, or
  set XR_RUNTIME_JSON to the runtime's .json file.`
- Graphics API: Direct3D 11 is turned into Vulkan by DXVK Native, and the session is created on DXVK's Vulkan objects with
  `XR_KHR_vulkan_enable`.

Extensions the plugin uses when the runtime offers them (none is needed except the graphics one):
- `XR_KHR_visibility_mask` (lens mask), `XR_KHR_composition_layer_cylinder` (cylinder layers), `XR_EXT_palm_pose` (palm pose),
  `XR_FB_display_refresh_rate` (the Refresh rate setting), `XR_OCULUS_audio_device_guid` (sound through the headset on Windows),
  and the controller extensions `XR_BD_controller_interaction`, `XR_BD_ultra_controller_interaction`,
  `XR_EXT_hp_mixed_reality_controller`, `XR_VALVE_frame_controller_interaction`.

Refresh rate:
- Options > VR > Display > Headset > **Refresh rate** lists `Runtime default` and the rates the headset offers. The list
  fills once VR is running. A runtime without `XR_FB_display_refresh_rate` ignores the setting.
- The log lists them: `OpenXR display refresh rates offered: ... Hz` and `OpenXR display refresh rate: N Hz (the runtime's default)`.
- A rate the headset does not offer is replaced by the nearest one: `OpenXR: the runtime does not offer N Hz; asking for M Hz
  instead`. A refusal logs `OpenXR: requesting N Hz failed: <result>`.
- A higher rate gives the GPU less time per frame. If the picture judders, lower the rate or the render resolution.
- Linux with WiVRn: the refresh rate is set in the WiVRn app (see [Linux](#linux)).

Performance settings, from cheapest to change, and what each costs or saves:
- **Dynamic resolution** (Display > Headset, on): draws the eyes at a lower resolution, down to 70% each way in steps of 5%,
  while the GPU cannot keep up, and back up when it can. Cost: a softer picture while it is down, and a frame the GPU
  misses is shown again, which can feel like judder. Turn it off for a steady picture if your GPU keeps up.
- **Headset layers in the desktop window** (Display > Performance, on): off, the desktop window shows the eye alone and the
  headset's frames take a little less time.
- **Desktop view** (Display > Desktop window, on): off, the window is hidden and costs no GPU time. A hidden window takes no
  keyboard or mouse, so play is by the controllers alone. On Linux the window stays, black.
- **Lens mask** (Display > Headset (from ColbyJack1134's SpaceEngineersVR), on): skips the corners of each eye the lenses never show.
  Saves GPU time. Those corners are black in the desktop window.
- **Particle density** (same group, 100%): lower makes fewer of the game's continuous GPU particles (dust, smoke, sparks,
  thruster flames, weather).
- **External view** (Interface > Interface parts, off): costs a third scene pass. **External view at half rate** (on)
  draws it every other frame and saves most of that cost; **External view size** is its width.
- Mirror width and height (restart): the size of the desktop window.
- The headset's own render resolution, in its PC software (for Link, the Meta Horizon Link app's graphics settings), is the
  largest lever. The world is drawn twice every frame.

Link on NVIDIA: Meta has advised that NVIDIA drivers newer than 591.86 can make Link freeze during play. That is Meta's
notice about Link and the driver; it has not been traced to this plugin. Air Link or Virtual Desktop are worth trying.

## Linux

Native Linux, no Proton: Pulsar's **Interim** launcher runs the game on .NET 10, SDL3 and DXVK Native, which turns Direct3D 11
into Vulkan. The plugin installs from the PluginHub as on Windows, and Pulsar compiles it for Linux. Tested on one setup:
a Meta Quest 3 through WiVRn over USB, Ubuntu 26.04, an RTX 3060. The menu shows in the headset with the laser on it, the
session reaches Focused and the game's sound reaches the headset. Not every feature has had a headset pass on Linux.

Requirements:
- Space Engineers (Steam) and Pulsar for Linux (the linux-x64 build), started once with `Interim.bin` so it installs CometWorks' linux-compat.
- A headset with WiVRn: the server on the PC, the app on the headset.
- The distribution's OpenXR loader: `libopenxr-loader1` (Debian, Ubuntu), `openxr-libs` (Fedora) or `openxr` (Arch). Nothing is
  shipped for Linux: the plugin uses this one.
- The distribution's OpenAL: `libopenal1`, `openal-soft` or `openal`. Without it the game starts and runs without sound.
- DXVK Native with a patch. WiVRn and Monado need Vulkan extensions that DXVK Native does not enable; without them the plugin
  stops before the headset session and logs why. The patch is `tools/linux/dxvk-native-xr-external-fd.patch`; the setup
  script builds DXVK with it (one to several minutes) and copies it into linux-compat, keeping the originals.
- For sound to move to the headset: PipeWire's `pw-dump` and `pw-metadata`.
- `adb` for a cable connection.

Setup:
```sh
git clone https://github.com/cheezecake-dev/CheezecakeSEVR.git
cd CheezecakeSEVR
tools/linux/setup.sh --no-plugin --install-deps --launcher
```
- The script installs the loader and OpenAL (`--install-deps`, with sudo, through apt, dnf or pacman), builds and installs
  DXVK, checks for WiVRn and `adb`, and with `--launcher` adds a menu entry that starts the WiVRn server, runs `adb reverse`
  if a cable is attached, and starts Pulsar. Close Pulsar first.
- `--check` only reports what is installed. `--help` lists the other options. `--no-dxvk` skips the DXVK step, `--pulsar DIR`
  and `--se-bin64 DIR` name the folders if they are not found.
- Without `--no-plugin` the script registers the clone as a development folder in Pulsar instead of using the PluginHub
  version. Enable only one of the two.
- Run the script again after a linux-compat update, which replaces the DXVK files.
- Then start the game with `Interim.bin`, and in the main menu click **Plugins**, then **+**, enable **Cheezecake's SE VR**
  and click **Apply**. Pulsar compiles the plugin on that start.

Headset settings that work (WiVRn app): disconnect first, turn on Extended configuration, then Foveated encoding 30% or lower,
Render resolution 120%, Refresh rate 90, Bitrate 200 Mbit/s. For a cable: `adb reverse tcp:9757 tcp:9757`, pick the USB option
in the WiVRn dashboard and switch the headset's Wi-Fi off. WiVRn's defaults are tuned for Wi-Fi; a cable has the bandwidth for
more. Only a USB connection has been tested.

Play: start the WiVRn server, open the WiVRn app in the headset and connect, then start the game from the launcher or
`cd <Pulsar folder> && ./Interim.bin -bin64 <Space Engineers>/Bin64`.

Linux behaviour to know:
- The headset may connect after the game starts. WiVRn waits for a headset when the plugin asks for the runtime, so the game
  waits 15 seconds at startup, then goes on with its own picture; VR starts when the headset connects.
- If the WiVRn server stops, the game goes back to the window and VR returns when the server is back and the headset has reconnected.
  If only the headset app stops, the session stays and comes back about 2 seconds after the app reconnects.
- If the game picked another graphics card than the headset needs, VR stays off until the game is restarted with the headset connected.
- With **Desktop view** off the window stays, black.
- The game's sound moves to WiVRn's PipeWire output while the headset is connected, and goes back to the default output
  when it is gone.
- Measured on the tested setup (RTX 3060, 90 Hz): main menu about 900 of 900 frames per 10 seconds; in a world, dynamic
  resolution settles at 70% of the runtime's eye size, with 94 to 98% of frames submitted.
- Linux logs: the game log is the newest `SpaceEngineers_<date>_<time>.log` in `~/.config/SpaceEngineers/`, the plugin logs are `SpaceEngineersVR.log` and
  `SpaceEngineersVR.prev.log` in the same folder (or the `-appdata` folder). DXVK Native writes its own log to stderr
  unless `DXVK_LOG_PATH` is set.

## Log files

Where (the same folder as `SpaceEngineers.cfg`):
- Windows: `%AppData%\SpaceEngineers\`
- Linux: `~/.config/SpaceEngineers/`
- Either way, the folder given to the game with `-appdata` if you used it.

Files there:
- `SpaceEngineers_<date>_<time>.log` (for example `SpaceEngineers_20260919_034222834.log`): the game's own log, one per
  start; ask for the newest. With Pulsar's `-stableLogs` option it is one file, `SpaceEngineers.log`, overwritten each start.
- `SpaceEngineersVR.log`: the plugin's log for the last start.
- `SpaceEngineersVR.prev.log`: the run before. Starting the game again moves the old log there, so if you restarted after
  the problem, this is the one with it in. Attach both.
- Pulsar's own log, `info.log`. Ask for it when the plugin does not show up in the Plugins list or does not compile.
  It is `<Pulsar folder>\Legacy\info.log` (typically `%AppData%\Pulsar\Legacy\info.log` on Windows,
  `~/.config/Pulsar/Legacy/info.log` on native Linux), or `Interim\info.log` instead of `Legacy` if the player created an
  `Interim` folder there. Pulsar overwrites it on every launch, so ask for it right after the problem happens.

Line format: `HH:mm:ss.fff [INFO] [thread] message`, with `[WARN]` and `[ERROR]` for problems. An `[ERROR]` line is followed by the exception.

What to look for: search the log for `[WARN]` and `[ERROR]`. The first line shows the version:
`SpaceEngineersVR 0.6.0.0 starting via ...`. Other lines worth knowing:
- `Headset: <name>, eye <W>x<H>, GPU LUID ...`: the headset was found.
- `OpenXR left hand interaction profile: ...`: which controller layout the runtime chose.
- `OpenXR session <state> -> <state>`: the session's states; Focused means the headset shows the game.
- `Input: right A pressed (...)`: every controller press. If a button does nothing, check that its line appears.
  At most a limited number of button lines are logged a second; the log then says `Input: N more button lines skipped`.
- `Settings saved to <path>`, `Settings read from <path>`.
- `Height calibrated: eye height N m above the floor`, `Arm span calibrated: ...`, `Height calibration cleared`.

## Troubleshooting

Each entry is symptom, cause, fix. Quoted text is what the log or screen says.

### The game starts flat, with no VR

| Log says | Cause | Fix |
|---|---|---|
| `VR disabled (-novr)` | `-novr` was passed, or `enabled=false` is in `SpaceEngineersVR.cfg`. | Remove the argument; set `enabled=true` or delete the cfg. |
| `The OpenXR runtime is not running; VR starts when it is running and the headset connects` | No runtime is answering. | Start Link, SteamVR or the WiVRn server. VR starts by itself when it connects; the plugin looks every 2 seconds. |
| `No headset connected; VR starts when one connects` | The runtime is up but sees no headset. | Connect the headset (start Link, put it on). Make sure your headset's runtime is the active OpenXR runtime (see [Install](#install-update-and-uninstall), step 2). On a PC with two graphics cards, start Link or SteamVR before the game. |
| `The OpenXR runtime failed (<message>); trying again every 2 s` | The runtime or its service failed or the instance was lost. | Restart the runtime. The plugin tries again. |
| `The active OpenXR runtime does not support Direct3D 11 (XR_KHR_D3D11_enable)` | The active runtime cannot take a Direct3D 11 game. | Make a runtime that supports it the active one. |
| `Could not load <path> (<reason>)` | The OpenXR loader could not be loaded. | Windows: update the plugin, which brings the loader. Linux: install the loader package named in the line. |
| `OpenXR is not usable on this machine, so no headset can be found; nothing is patched and the game runs as usual` | OpenXR could not be used at all (loader or runtime). | Fix the line above it in the log. |
| A line beginning `Loaded after the render device was created` | The plugin was loaded too late. | Start the game through Pulsar's Legacy launcher with the plugin enabled, and restart it after enabling. |
| `The headset's GPU (LUID <id>, DXGI index N) is not in the game's adapter list` | On a PC with two graphics cards, the game's card list does not include the headset's. | Start Link or SteamVR first, restart the game. |
| `<message>; playing flat. Restart the game with the headset connected to play in VR` | The game's graphics device cannot be used by the headset (another GPU, or on Linux a DXVK without the runtime's extensions). | Restart the game with the headset connected. On Linux, see the DXVK entry below. |
| `Could not start the headset session; playing flat for the rest of the run` | The session failed more than once. | Restart the game with the headset connected; attach the log. |

### A message box in the main menu

| Message | Cause | Fix |
|---|---|---|
| `VR is off for this run. This version of Space Engineers changed something the plugin needs, so the game runs as usual on the monitor. An update to the plugin will follow.` (then `Not working: ...`) | A game update changed code a core feature hooks. The log has `Core feature <name> could not be patched and is off`. | Play flat, check for a plugin update, and report it with the log. |
| `Some features of the plugin are off. This version of Space Engineers changed something they need; the rest of the plugin works. An update to the plugin will follow.` (then `Off: ...`) | Optional features failed to hook after a game update: a VR feature, or the scenarios the plugin lists in New Game (`ScenarioRegistry`) or the Bug Swarm (`BugSwarm`). The log has `Feature <name> could not be patched and is off`, or the feature's own line saying what it cost. | The rest works without them. Report it with the log. |
| `The VR view failed, so the world is hidden for now; the menus still work. Close this message to try the view again. The game's menu can save and leave. Details are in SpaceEngineersVR.log.` | The eye drawing threw an error. The log has `<context> failed; the eyes are off and the menus stay up in VR, until the message about it is closed`. | Close the message to try again (twice at most). Save and leave from the game's menu if it keeps happening. |
| `The VR view failed again, so the world stays hidden until the game is restarted; the menus still work. Save and leave from the game's menu.` | It failed a third time. | Save, leave, restart the game; attach the log. |

### A feature stopped working during play

| Log says | Cause | Fix |
|---|---|---|
| `<feature>: 3 exceptions in N s, the last <type>: <message>; switched off, back in 30 s` | A feature threw repeatedly and the plugin turned it off to protect the game. After the next failures it is off for 120 s, then for the rest of the session: `switched off for the rest of the session`. | It comes back by itself. If it does not, restart the game. Attach the log. |
| `Headset frame failed; VR display stops` | The headset frame threw an error. | Restart the game; attach the log. |
| `Drawing the GUI failed` | The menus could not be drawn into the headset. | Restart; attach the log. |
| `Headset lost (<reason>); playing in the window until it reconnects` | The runtime lost the session (headset asleep, cable out, WiVRn server stopped). | Reconnect it. VR comes back by itself. Meanwhile the window shows the game's own picture at its size. |
| `OpenXR floor lost (xrLocateSpace ..., flags ...); height cannot be corrected until it is back` | The headset lost its floor. | Redo the headset's boundary or play area setup. |
| `OpenXR STAGE space could not be set up; the floor is not known` | No play area. | Set up the play area or boundary in the headset; then calibrate height. |

### Picture problems

| Symptom | Cause | Fix |
|---|---|---|
| The headset picture freezes for seconds at a time (Link) | Meta has advised that NVIDIA drivers newer than 591.86 can make Link freeze. Not traced to this plugin. | Try driver 591.86, or Air Link or Virtual Desktop. |
| `The runtime has no <format> swapchain; using <format>, the headset image will look washed out` | The runtime offers no sRGB swapchain. | Report it with the headset and runtime. |
| `The runtime refused a mutable-format swapchain (<result>); asking without it ...` | The runtime does not take mutable formats. | Report it with the runtime. |
| Judder or a soft picture | The GPU cannot keep up. | See [performance settings](#runtimes-headsets-and-performance): lower the headset's render resolution or refresh rate, keep **Dynamic resolution** on, turn off **External view** and **Headset layers in the desktop window**. |
| The game opens on the wrong monitor, or minimized | The game saves its monitor as a place in Windows' list of monitors, and that list can change order. Exclusive fullscreen also minimizes the game whenever another window takes the focus. | With **Keep the game on its monitor** on, choose the monitor once in the game's Options > Graphics: it is kept by name from then on (`Monitor: ...` in the log). For a game that stays up when you click elsewhere, use the game's **Fullscreen window** mode. |
| The desktop window is black at the corners | **Lens mask** skips the corners the lenses never show. | Normal. Turn **Lens mask** off to see them. |
| The desktop window is black (Linux) | **Desktop view** is off. | Turn **Desktop view** on. |
| The desktop window takes no keyboard or mouse | **Desktop view** is off, or the window is hidden. | Turn **Desktop view** on, or play by the controllers. |
| The picture is too dark | Visor tint, the sun visor or the HUD colour filter. | Options > VR > Display > Helmet: **Visor tint** to 0, lift the sun visor, or turn **Helmet view** off. |
| Frost or drops on the visor | The weather and temperature effects. | Wipe it with a fingertip, or turn the effect off under Display > Helmet. |
| The view fades to black when I lean | The wall, canopy and dashboard fade. | Normal. It stops when you lean back. |
| A mod's HUD is too close to read | The plugin moves a mod's HUD onto the VR HUD when the mod draws it within 45 cm of the camera, as Text HUD API and Rich HUD Framework do. A mod that draws its HUD another way is not moved (WeaponCore's has not been tried). | Report the mod with the log. The rest of the HUD is unaffected. |

### Controller problems

| Symptom | Cause | Fix |
|---|---|---|
| Hands with no buttons, arms drop when hands are out of view (Quest) | The Quest is tracking your hands, not controllers. The log says `OpenXR hands: the Quest is tracking hands, not controllers (controllers asleep or hand tracking on): pick up the controllers`. | Pick up the controllers. |
| A button does nothing | A button is not on your controller, or not bound. | Check the log for `Input: ...` lines when you press it. Check [bindings](#controller-bindings) for your controller. The log lists `OpenXR bound sources (active after a sync): ...`. |
| Valve Index: no Menu button | The Index has no Menu button; the left trackpad pressed firmly is Menu. | Press the left trackpad firmly: a tap is the game menu, a hold recentres. |
| Vive wand: no X | Wands have no X button. | Its jobs are on the right Menu: a tap is the dampeners, and held with the right trigger it aims a ping ([Vive wands](#vive-wands)). |
| `The runtime did not take the bindings for <profile>: ...` or `The runtime did not take all the bindings for <profile> (...); it took N of M, without ...` | The runtime refused some bindings. | Only the controls named are lost. Report your controller and runtime. |
| `OpenXR controller input could not be set up; continuing without controllers` | Controller input could not be set up. | Restart the game; attach the log. |
| `OpenXR controller input failed (N in a row); controllers off, trying again in N s` | Reading the controllers failed. | The plugin tries again by itself, waiting twice as long each time, up to 30 s. If it keeps failing, restart the game and attach the log. |
| `OpenXR <left/right> controller not tracked` | The controller lost tracking. | Keep it in view of the headset. |
| `Haptics are off for this headset session: <message>` | The runtime refused haptics. | Only the controller buzz is lost. |
| The view drifts or is facing the wrong way | The view needs a recentre. | Hold the left Menu button for 0.6 seconds (Options > VR > **Recentre view**). |
| Your view height is wrong | The calibration is missing or the floor is wrong. | Options > VR > **Calibrate height**; if wrong, **Clear**, redo your play area, calibrate again; **Height offset** adjusts it. |
| A calibration message: `Measured N m, outside 0.6 to 2.3 m: try again` | Eye height outside the allowed range. | Stand straight, redo the floor, try again. |
| `Arm span: hands N m apart, need 1.2 to 2.3 m; none saved` or `kept N m` | Your hands were too close or too far apart. | Hold both arms straight out to the sides. |
| `Arm span: a controller is not tracked`, `hands moved while measuring`, `hands N cm off level` | The measurement was not steady. | Hold both arms out level, still, with both controllers in view. |
| `Height calibration: No floor known: set up the play area or boundary in the headset` (shown on screen) | The headset does not know where the floor is. | Set up the play area or boundary first. |

### Sound problems

| Symptom | Cause | Fix |
|---|---|---|
| No sound in the headset | **Sound through the headset** (Display > Headset) needs a restart after a change, and the runtime must have the headset connected at start. | Start Link before the game. The log says `Sound: playing through <name> (the headset's audio device from the runtime)` or `Sound: the Windows default output (<reason>)`. |
| No sound at all (Linux) | OpenAL missing. | Install `libopenal1`, `openal-soft` or `openal`. |
| Sound stays on the speakers (Linux) | PipeWire's tools are missing. The log says `Sound: pw-dump (PipeWire) is not installed; the game's sound stays on the default output` or the same for `pw-metadata`. | Install them. |
| Voice chat | The game's voice activation works; there is no push-to-talk gesture. | Options, Audio. |

### Settings problems

| Symptom | Cause | Fix |
|---|---|---|
| Settings are not kept | The folder cannot be written. The log says `Could not save the settings to <path>`. | Make the folder writable. |
| A setting changed by hand is ignored | The line is wrong. The log says `Settings file line N: ...; skipped`. | Fix the line, or delete the file to start over. Edit the file only while the game is closed. |
| Want everything back to defaults | | See [Settings file](#settings-file), Reset. |

### Linux only

| Log or symptom | Cause | Fix |
|---|---|---|
| `DXVK lacks the Vulkan extensions OpenXR needs (stock linux-compat DXVK); run tools/linux/setup.sh to install the patched one` | linux-compat's DXVK is stock; WiVRn and Monado need Vulkan extensions it does not enable. | Run `tools/linux/setup.sh`. Run it again after every linux-compat update. |
| `...; MISSING: <extensions> - the runtime may fail without them` | DXVK lacks extensions the runtime asked for. | As above. |
| `OpenXR graphics: Direct3D 11 on DXVK's device, as no Vulkan extension of the runtime is enabled; a runtime cannot normally use it` | The runtime offers no usable Vulkan extension. | Use WiVRn or Monado with `XR_KHR_vulkan_enable`. |
| `the system has no libopenxr_loader.so.1 and the plugin folder has none. Install the OpenXR loader: ...` | No OpenXR loader. | Install `libopenxr-loader1` (Debian, Ubuntu), `openxr` (Arch) or `openxr-libs` (Fedora). |
| `OpenXR runtime: none chosen and none found (looked for WiVRn, Monado and SteamVR). ...` | No runtime installed. | Install WiVRn, or set `XR_RUNTIME_JSON` to a runtime's `.json`. |
| `The OpenXR runtime is not running (start the WiVRn server); ...` | The WiVRn server is not running. | Start it. |
| `No OpenXR answer from the runtime after 15 s (WiVRn waits for the headset to connect); starting with the game's own picture, and VR starts when the headset connects` | WiVRn waits for the headset app. | Open the WiVRn app in the headset and connect. VR starts by itself. |
| The headset connects but the picture stutters | WiVRn's defaults are tuned for Wi-Fi. | Use the headset settings in [Linux](#linux): a cable, 90 Hz, bitrate 200 Mbit/s. |
| The game stays on the window after the WiVRn server restarts | The session was lost with the instance. | VR returns when the headset reconnects. If it does not, restart the game. |

If nothing here matches, [report it](#reporting-a-bug).

## Known issues

True at version 0.6.0:
- One tested setup (Quest 3 over Link with an RTX 3060). Other headsets, connections and graphics cards should work but have not been tried.
- Link freezes on recent NVIDIA drivers: see [Picture problems](#picture-problems).
- Minor: the drill's tip moves slightly while you walk.
- Mod HUDs drawn by Text HUD API or Rich HUD Framework show on the VR HUD, in the same 16:9 band as the game's HUD.
  WeaponCore's HUD has not been tried yet.
- Multiplayer has not been tested between two PCs yet. In a game hosted by a player who has the plugin, other players
  with the plugin, in VR or flat, see your head and hands move. Players without it see your character as the game animates
  it. Your shots go where your hand points.
- Seated, your fingertip works the cockpit's own switches and screens, not button panel blocks in reach of the seat.
- Left-handed: the status screen stays on the left gauntlet.
- Game updates can change the code the plugin hooks. A hook that fails is logged and skipped. If the renderer cannot be hooked,
  the game starts without VR.
- Newer and less tested, with little or no time in a headset yet: the camera, turret and searchlight view screen and its
  zoom; the death screen view and the respawn menu; the on-screen keyboard moving to the top of a text editor; typing
  amounts at a store block or ATM; the actions wheel's Voxel material page; the wrist panel's Heal tile; the HUD on the
  visor and on the glass; the helmet's inside, visor tint, sun visor, glare, HUD power-on and helmet breathing; feeling
  the jetpack, magnetic boots and hard landings; each gun's shot and the empty click; the artificial horizon in a cockpit;
  zero-g pitch and roll staying off in gravity; the view steadied on magnetic boots; pitch and yaw from the held flight
  stick; the Vive wand layout.
- Linux: only WiVRn has been run. Monado and SteamVR for Linux are untested.
- Windows with Pulsar's Interim launcher is untested.

## Conflicts and the mod API

Conflicts:
- Do not run the plugin from two places at once. On Linux, `setup.sh` without `--no-plugin` registers a clone as a
  development folder; enable only one of the PluginHub version and the clone.
- Mods that draw their HUD just ahead of the camera (Text HUD API, Rich HUD Framework) have it moved onto the VR HUD; a
  mod that places its HUD with the API's `HudToWorld` is never moved twice.
- On Linux the plugin works with CometWorks' linux-compat and its DXVK. A linux-compat update replaces the patched DXVK
  files; run `tools/linux/setup.sh` again.

The plugin does not patch other mods or plugins, and does not read their private fields. A mod that wants to show correctly
in a headset uses the plugin's API.

The API (version 2; the whole reference is [MOD_API.md](MOD_API.md)) is for plugins, mods and programmable block scripts:
- Plugins call `SpaceEngineersVR.Api.SevrApi.GetApi()` by reflection. Mods get it over the game's mod messaging on channel
  `6000296989824141617` ("SEVRAPI1" in ASCII, as a long): the plugin sends a `Dictionary<string, Delegate>` when a world
  starts, and again whenever anyone sends the string `"SEVR.ApiRequest"` on the channel. Scripts read the programmable
  block's hidden terminal property `"SevrApi"`, which has the HUD members only.
- The HUD members (version 1): `Version`, `IsActive` (a headset is showing the game), `HudVisible`, `HudDepth`,
  `HudTangents`, `HudToWorld` (not offered to scripts). Version 2 adds controller actions the player binds, items on the
  actions wheel and the wrist panel, panel screens, the hands' and head's poses, the controllers' state, haptics and
  events.
- Without the plugin or a headset nothing arrives or `IsActive` returns false: draw as you always do.
- The API only describes how things are shown and what the player does with the controllers. It says nothing the game
  does not already show a flat player.
- A mod that draws its HUD just ahead of the camera needs no change: the plugin moves it onto the VR HUD (Conflicts, above).
  A mod that places its HUD itself can go through `HudToWorld` while `IsActive` is true.

## FAQ

**Does it work on Space Engineers 2?** No. It is for Space Engineers 1 only.

**Which headsets work?** The tested one is a Meta Quest 3 over Meta Link. Air Link, Virtual Desktop and SteamVR headsets
should work but are untested. Bindings are offered for Index, Vive, Windows Mixed Reality, HP Reverb G2, Steam Frame, Vive Cosmos,
Vive Focus 3, PICO 4, PICO 4 Ultra and PICO Neo3, none of them tried yet.

**Does it work on Linux?** Yes, early, on native Pulsar with WiVRn. See [Linux](#linux). Not with Proton.

**Do I need VorpX or ReShade?** No. The game's own renderer draws each eye.

**How do I turn VR off?** Disable the plugin in Pulsar's Plugins menu, or start the game with `-novr`.

**Where are my settings and logs?** `%AppData%\SpaceEngineers\` on Windows, `~/.config/SpaceEngineers/` on Linux.
Settings are `SpaceEngineersVR.cfg`; logs are `SpaceEngineersVR.log` and `SpaceEngineersVR.prev.log`.

**I started the game before the headset. Is VR lost?** No. VR starts when the headset connects, also after the game has
started. Check that your headset's runtime is the active OpenXR runtime.

**How do I recentre?** Hold the left Menu button for 0.6 seconds, until the firm tick. A tap opens the game menu.

**My view height is wrong.** Set up the play area, then Options > VR > Calibrate height.

**How do I play seated?** Options > VR > Display > Play position: Seated. Recentre with your head where your eyes should be.

**Can I play left-handed?** Yes: Options > VR > Controls > Left-handed.

**Can I remap buttons?** There is no remapping screen. Left-handed swaps the main hand.

**How do I get more performance?** Lower the headset's render resolution in its PC software, keep Dynamic resolution on, turn off
Headset layers in the desktop window and External view, and see [performance settings](#runtimes-headsets-and-performance).

**I get no sound in the headset.** Start Link before the game; Sound through the headset needs a restart after a change.
On Linux it needs PipeWire's `pw-dump` and `pw-metadata`.

**Does multiplayer work?** It is made for a game a player hosts, and has not been tested between two PCs yet. Other
players with the plugin, in VR or flat, see your head and hands move; players without it see your character as the game
animates it.

**Do other players see that I use VR, and does it give me an advantage?** Nothing it shows is more than a flat player could
see in the same place. Immersive dressing such as the helmet rim and visor tint sits outside the game's field of view or only darkens.

**How do I use the keyboard and mouse?** They keep working alongside the controllers. A hidden desktop window (Desktop view
off) takes no keyboard or mouse.

**How do I ping?** Hold X, point, let go. See [Using it in play](#using-it-in-play).

## Questions this guide doesn't answer

If a question is not covered here, or it looks like a bug, open an issue at
https://github.com/cheezecake-dev/CheezecakeSEVR/issues and include what the reporting section below lists. For anything
else, ask Cheezecake, the plugin's author, directly in the Pulsar Discord by mentioning them in the message: a mention
from you is what notifies them.

## Reporting a bug

Open an issue at https://github.com/cheezecake-dev/CheezecakeSEVR/issues/new/choose. Use the bug report form. Send someone
there when:
- the troubleshooting above does not fix it;
- the log has an `[ERROR]` line or says "attach the log";
- a game update turned VR or a feature off (the main-menu message box);
- a controller, headset or runtime that is untested misbehaves. Reports from other headsets and graphics cards help most.

Include:
- The plugin version: the plugin list in Pulsar shows `0.6.0.0` for this release, and the first line of
  `SpaceEngineersVR.log` shows `SpaceEngineersVR 0.6.0.0 starting via ...`.
- Your headset and how it is connected: Link cable, Air Link, Virtual Desktop, SteamVR, or WiVRn on Linux.
- The OpenXR runtime in use (the Link app, SteamVR or WiVRn and its version).
- Your graphics card and driver version.
- Your operating system (Windows version, or Linux distribution and version) and whether you start with Pulsar's Legacy or Interim.
- Both logs, `SpaceEngineersVR.log` and `SpaceEngineersVR.prev.log`, from the game's user folder (above).
- Steps to reproduce: what you did, what happened and what you expected.
