using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;

namespace SpaceEngineersVR
{
    /// <summary>
    /// The plugin's settings: declared below, read from the settings file at start-up (then the command line, which wins
    /// for that run) and written back by <see cref="Save"/>. The options screen edits them live.
    /// </summary>
    internal static partial class VRSettings
    {
        private static readonly object Gate = new object();
        private static readonly List<Setting> all = new List<Setting>();
        private static readonly Dictionary<string, Setting> byKey = new Dictionary<string, Setting>(StringComparer.OrdinalIgnoreCase);

        // ---- The settings. One declaration each; the options screen follows this list, in this order, and sections
        // ---- appear in the order they are first used here.

        private static readonly NumberSetting hudDistance = Add(new NumberSetting("hudDistance", "HUD distance", "Display",
            1.5f, 0.5f, 5f, 0.05f, unit: "m", decimals: 2,
            tooltip: "How far ahead of your eyes the HUD floats during play (-hudDistance). You see the change once the menu is closed."));

        private static readonly NumberSetting hudWidth = Add(new NumberSetting("hudWidth", "HUD width", "Display",
            55f, 20f, 120f, 1f, unit: "deg", decimals: 0,
            tooltip: "How wide the HUD looks, in degrees of your view (-hudWidth). You see the change once the menu is closed."));

        private static readonly ChoiceSetting refreshRate = Add(new ChoiceSetting("refreshRate", "Refresh rate", "Display",
            new[] { "Runtime default" }, 0, open: true,
            tooltip: "The rate the headset's screen runs at. Runtime default leaves it as the headset has it; the others are the rates the headset offers, listed once VR is running. A saved rate the headset does not offer is replaced by the nearest it does; a headset whose runtime has no refresh-rate control ignores this. A lower rate gives the GPU more time for each frame. Takes effect at once while a game runs."));

        private static readonly FlagSetting dynamicResolution = Add(new FlagSetting("dynamicResolution", "Dynamic resolution", "Display", true,
            tooltip: "Draw the eyes at a lower resolution, down to 70% each way, while the GPU cannot keep up with the headset; back up when it can. A frame the GPU misses is shown again, which can feel like judder."));

        private static readonly FlagSetting lensMask = Add(new FlagSetting("lensMask", "Lens mask", "Display", true,
            tooltip: "Skip lighting and shading the corners of each eye the headset's lenses never show (their shape comes from the headset's runtime), which saves GPU time. Those corners show black in the desktop window. Off draws every pixel."));

        private static readonly NumberSetting comfortVignette = Add(new NumberSetting("comfortVignette", "Comfort vignette", "Display",
            0f, 0f, 1f, 0.05f, decimals: 2,
            tooltip: "Darkens the edges of your view while the thumbsticks move or turn you, which eases motion sickness. 0 turns it off. Moving your head never triggers it."));

        private static readonly FlagSetting comfortFade = Add(new FlagSetting("comfortFade", "Fade on camera cuts", "Display", true,
            tooltip: "When the game puts your view somewhere else at once - getting into or out of a seat, a camera block or a turret, a respawn, a jump drive's jump, a teleport, or standing upright as you come into gravity - the view goes black and the world fades back in over a moment. Off: the view cuts straight to the new place."));

        private static readonly ChoiceSetting cameraShake = Add(new ChoiceSetting("cameraShake", "Camera shake", "Display",
            new[] { "Off", "Low", "Full" }, 0,
            tooltip: "How much the game moves your view when you are not moving your head: the shake of the drill, grinder and rifle, of damage and explosions, of a ship under thrust, the ship camera's sway, and the head bob when you start and stop walking. Off: the view moves only with your head. Low: a quarter of the game's. Full: as the game has it. It never changes what the tools or the ship do."));
        private static readonly FlagSetting levelChaseCamera = Add(new FlagSetting("levelChaseCamera", "Level chase camera", "Display", true,
            tooltip: "Third person in a ship: the camera follows the ship but keeps the horizon level, and only turns with its heading, so rolling and pitching the ship does not turn the world around you. Off: the game's own camera, locked to the ship. Needs \"Allow third person\"."));

        private static readonly FlagSetting thirdPerson = Add(new FlagSetting("thirdPerson", "Allow third person", "Display", false,
            tooltip: "Lets the camera-mode key switch your own view to third person (with the level-horizon chase camera). A world, a saved game or a respawn always starts in first person in VR; this only lets you switch yourself. Turning it off while in third person puts you back in first person. The external view screen works either way."));

        // ---- Interface: one switch for each part of the game's interface the VR player sees, each hiding only its own part.
        // A part added later is one more line here.

        private static readonly FlagSetting wristStatus = Add(new FlagSetting("wristStatus", "Status on the wrist", "Interface", true,
            tooltip: "A screen on the gauntlet of your astronaut's left forearm shows your suit and body status, on foot and in a seat: the helmet, jetpack, broadcast and lamp icons, radiation, the health, food, energy, oxygen and hydrogen bars, the medicine and cryo marks, the inventory fill, and the oxygen and temperature outside. It brightens when you look at it, and the HUD leaves these out (the speed, dampeners and gravity stay on the HUD). Off: no screen on the wrist, and the HUD shows them as the game does."));
        private static readonly NumberSetting hudTextSize = Add(new NumberSetting("hudTextSize", "HUD text size", "Interface",
            1f, 0.8f, 2f, 0.05f, unit: "%", decimals: 0, displayScale: 100f,
            tooltip: "How large the HUD's parts on the visor are drawn, text with its plates and bars, from the middle of the view outward: each grows away from its own edge or corner. The parts are drawn at the larger size, so the text stays sharp (the game's own fonts, as it draws them at a larger UI scale); only when the visor image is full are the largest parts drawn smaller and sampled up. 100% is as wide as the HUD width gives. Needs HUD on the visor; the flat panel is sized by HUD width. You see the change once the menu is closed."));
        private static readonly NumberSetting hudTextBacking = Add(new NumberSetting("hudTextBacking", "HUD text backing", "Interface",
            0f, 0f, 0.9f, 0.05f, unit: "%", decimals: 0, displayScale: 100f,
            tooltip: "A dark plate behind each block of HUD text on the visor, so it can be read against a bright sky or planet. The number is how solid the plate is; 0 draws none. Needs HUD on the visor. You see the change once the menu is closed."));
        private static readonly FlagSetting hudVisor = Add(new FlagSetting("hudVisor", "HUD on the visor", "Interface", true,
            tooltip: "The HUD's parts sit on your helmet's visor: the toolbar, suit and ship status, block info, chat, notifications and the rest each move out toward their own edge or corner, curve round your head and face your eyes, so the middle of your view stays clear. Markers on things in the world (ore, antennas, target locks, the selection box) and the crosshair stay where they are. Off: the whole HUD on one flat panel ahead of you, as before. The distance and width under HUD set how far and how wide the visor is."));
        private static readonly FlagSetting showToolbarOnFoot = Add(new FlagSetting("showToolbarOnFoot", "Toolbar on the HUD", "Interface", false,
            tooltip: "The HUD's bottom toolbar (the hotbar, its page numbers, the Customize Toolbar and Color lines, and the plate behind them) on foot and on the jetpack. Off: left out there, since the tools wheel and the wrist panel do its job, and a short label names the tool you pick; in a seat, a camera view, the G menu and the terminal it is shown as the game shows it."));
        private static readonly FlagSetting controlHints = Add(new FlagSetting("controlHints", "Control hints", "Interface", true,
            tooltip: "Short reminders on the HUD: \"B: back, right stick: scroll\" the first time a menu opens, where the tools wheel and the wrist panel are the first time you walk, and how to move an item to the other inventory the first time the inventory opens. Off: none. B and the left Menu button close a menu either way."));
        private static readonly FlagSetting externalView = Add(new FlagSetting("externalView", "External view", "Interface", false,
            tooltip: "A small screen on the dash showing your ship (or you, on foot) from behind and above, or the view from one of the ship's cameras, while you stay in the cockpit. On the desktop it is in the lower left of the picture. Costs a third scene pass. The actions wheel's External view entry turns it on and off and chooses what it shows; its size and source are under Display."));

        private static readonly NumberSetting menuDistance = Add(new NumberSetting("menuDistance", "Menu distance", "Menus",
            2f, 0.5f, 6f, 0.05f, unit: "m", decimals: 2,
            tooltip: "How far ahead of your head a menu is put (-menuDistance)."));

        private static readonly NumberSetting menuWidth = Add(new NumberSetting("menuWidth", "Menu width", "Menus",
            60f, 20f, 120f, 1f, unit: "deg", decimals: 0,
            tooltip: "How wide a menu looks, in degrees of your view (-menuWidth)."));

        private static readonly NumberSetting mainMenuWidth = Add(new NumberSetting("mainMenuWidth", "Main menu width", "Menus",
            85f, 40f, 110f, 5f, unit: "deg", decimals: 0,
            tooltip: "How wide the main menu looks, in degrees of your view. Menus in a world use Menu width."));

        private static readonly FlagSetting vrKeyboard = Add(new FlagSetting("vrKeyboard", "On-screen keyboard", "Menus", true,
            tooltip: "Click a text box with the laser and a keyboard opens on the menu to type into it. Off: only a real keyboard types."));

        private static readonly NumberSetting externalViewSize = Add(new NumberSetting("externalViewSize", "External view size", "Display",
            0.3f, 0.15f, 0.6f, 0.01f, unit: "m", decimals: 2,
            tooltip: "How wide the external view screen is in the headset, in metres (on the desktop its picture grows and shrinks with it). You see the change as the next frame is drawn. The wrist panel's External view page sets it too."));

        private static readonly FlagSetting externalViewHalfRate = Add(new FlagSetting("externalViewHalfRate", "External view at half rate", "Display", true,
            tooltip: "Draw the external view every other frame. Saves most of its cost; the screen looks slightly less smooth."));

        private static readonly ChoiceSetting externalViewSource = Add(new ChoiceSetting("externalViewSource", "External view source", "Display",
            new[] { "Third person", "Camera" }, 0, hidden: true,
            tooltip: "What the external view shows: your ship from behind and above (Third person), or the view from one of its camera blocks (Camera). Chosen on the actions wheel."));

        private static readonly TextSetting externalViewCamera = Add(new TextSetting("externalViewCamera", "External view camera", "Display", hidden: true,
            tooltip: "The camera block the external view shows when its source is Camera: its entity id, a bar, and its name. Chosen on the actions wheel."));

        private static readonly NumberSetting externalViewOrbit = Add(new NumberSetting("externalViewOrbit", "External view orbit", "Display",
            0f, -180f, 180f, 15f, unit: "deg", decimals: 0, hidden: true,
            tooltip: "Third person: how far the camera has gone round what you control from behind it, degrees, positive to its left. Set on the wrist panel's External view page."));

        private static readonly NumberSetting externalViewElevation = Add(new NumberSetting("externalViewElevation", "External view elevation", "Display",
            0f, -100f, 70f, 10f, unit: "deg", decimals: 0, hidden: true,
            tooltip: "Third person: how far the camera has gone over (positive) or under what you control from where it starts, degrees. Set on the wrist panel's External view page."));

        private static readonly NumberSetting externalViewDistance = Add(new NumberSetting("externalViewDistance", "External view distance", "Display",
            1f, 0.25f, 16f, 0.01f, decimals: 2, hidden: true,
            tooltip: "Third person: the camera's distance from what you control, as a multiple of the one that suits its size. Set on the wrist panel's External view page."));

        private static readonly TextSetting externalViewPlace = Add(new TextSetting("externalViewPlace", "External view placement", "Display", hidden: true,
            tooltip: "Where the external view screen stands after it was moved by hand, from the head's heading when it appears: right, up and back in metres, then its turn as a quaternion. Empty: on the dash."));

        private static readonly ChoiceSetting playPosition = Add(new ChoiceSetting("playPosition", "Play position", "Height",
            new[] { "Standing", "Seated" }, 0,
            tooltip: "Standing: your real height drives your view (calibrate it below), and crouching for real crouches your character. Seated: your head's height when you switch, and wherever your head is when you recentre, becomes your character's eyes, standing tall on foot or at the seat's eyes in a seat, and leaning down never crouches."));

        private static readonly NumberSetting eyeHeight = Add(new NumberSetting("eyeHeight", "Eye height", "Height",
            null, 0.3f, 3f, 0.01f, unit: "m", decimals: 2, hidden: true, optional: true,
            tooltip: "Your eye height above the floor, set by height calibration."));

        private static readonly NumberSetting heightOffset = Add(new NumberSetting("heightOffset", "Height offset", "Height",
            0f, -0.5f, 0.5f, 0.01f, unit: "m", decimals: 2,
            tooltip: "Raises (+) or lowers (-) your view on top of the calibration."));

        private static readonly FlagSetting armSpanScaling = Add(new FlagSetting("armSpanScaling", "Scale arms to arm span", "Height", true,
            tooltip: "Calibrate height also measures your arm span (your arms straight out to the sides for a few seconds), and your character's arms stretch as far as your own reach needs, so its hands stay on your controllers with your arms fully out. Off: the arms stretch as far as they always have, and Calibrate height only measures your height."));

        private static readonly NumberSetting armSpan = Add(new NumberSetting("armSpan", "Arm span", "Height",
            null, 1f, 3f, 0.01f, unit: "m", decimals: 2, hidden: true, optional: true,
            tooltip: "The distance between your hands with your arms straight out to the sides, set by height calibration."));

        private static readonly FlagSetting bodyClearance = Add(new FlagSetting("bodyClearance", "Keep eyes out of the body", "Height", true,
            tooltip: "On foot, lean or step so your head would be inside your character's chest, shoulders or backpack and the view stops at the surface instead. Off: the view goes where your head goes, through the body."));

        private static readonly FlagSetting crouchWithHead = Add(new FlagSetting("crouchWithHead", "Crouch when you crouch", "Height", true,
            tooltip: "On foot, in Standing play, crouch for real (head 30 cm or more below standing for a moment) and your character crouches, standing up when you do. Off, and always in Seated play: only the crouch key crouches."));

        private static readonly NumberSetting seatReach = Add(new NumberSetting("seatReach", "Seat head reach", "Height",
            0.25f, 0.1f, 1f, 0.05f, unit: "cm", decimals: 0, displayScale: 100f,
            tooltip: "In a seat, how far forward, up or down your head can move from your character's head before the view slows to a stop, in centimetres. Sideways it is four fifths of this. Backwards it is only as far as the body allows, so you cannot lean back into your own head and body."));

        private static readonly FlagSetting headLock = Add(new FlagSetting("headLock", "Lock head to body", "Height", true,
            tooltip: "On foot, your head can only move so far from your character's head. Step back, forward or sideways in the room and the view stops at the reach below instead of going out through the back of the body, and follows your head again as soon as you come back. Off: the view goes where your head goes."));

        private static readonly NumberSetting headReach = Add(new NumberSetting("headReach", "Head reach on foot", "Height",
            0.25f, 0.1f, 1f, 0.05f, unit: "cm", decimals: 0, displayScale: 100f,
            tooltip: "With the head locked to the body, how far forward your head can move from your character's head, in centimetres. Sideways it is four fifths of this. Backwards it is only as far as the body allows."));

        private static readonly NumberSetting snapTurn = Add(new NumberSetting("snapTurn", "Snap turn", "Controls",
            30f, 15f, 90f, 15f, unit: "deg", decimals: 0,
            tooltip: "How far one flick of the right stick turns you. With Zero-g pitch and roll on Snap, also how far one flick pitches or rolls you."));

        private static readonly FlagSetting smoothTurn = Add(new FlagSetting("smoothTurn", "Smooth turn", "Controls", false,
            tooltip: "Turn steadily while the right stick is held sideways, instead of in snap-turn steps. Some people find it less comfortable."));

        private static readonly NumberSetting smoothTurnSpeed = Add(new NumberSetting("smoothTurnSpeed", "Smooth turn speed", "Controls",
            120f, 30f, 360f, 15f, unit: "deg/s", decimals: 0,
            tooltip: "How fast the body turns with the right stick pushed all the way over, when Smooth turn is on. Pushed half way, half as fast. Zero-g pitch and roll on Smooth goes at the same speed."));

        private static readonly ChoiceSetting zeroGTurn = Add(new ChoiceSetting("zeroGTurn", "Zero-g pitch and roll", "Controls",
            new[] { "Smooth", "Snap", "Off" }, 0,
            tooltip: "On the jetpack in zero g, hold the left stick click and the right stick pitches you (up and down) and rolls you (sideways), turning about your eyes. Smooth: steadily, at the Smooth turn speed. Snap: one step of the Snap turn angle a flick. Off: the left stick click does nothing on the jetpack. In gravity the jetpack never pitches or rolls you."));

        private static readonly NumberSetting stickDeadzone = Add(new NumberSetting("stickDeadzone", "Stick deadzone", "Controls",
            0.15f, 0.05f, 0.4f, 0.01f, decimals: 2,
            tooltip: "How far a thumbstick has to be pushed before it counts. Raise it if you drift when the sticks are at rest."));

        private static readonly NumberSetting shipStickSensitivity = Add(new NumberSetting("shipStickSensitivity", "Ship stick sensitivity", "Controls",
            1f, 0.25f, 3f, 0.05f, decimals: 2,
            tooltip: "How fast the right stick and the grips turn a ship, a turret or a camera. 1 matches a gamepad pushed all the way."));

        private static readonly FlagSetting handsOnControls = Add(new FlagSetting("handsOnControls", "Hands on the controls to fly", "Controls", true,
            tooltip: "In a cockpit or rover seat the ship flies only while you hold its controls: close your grip with the dominant hand where the astronaut's hand holds the stick, and the other where it holds the throttle. Anywhere on that hand's side between your hips and chin counts, your lap included. The throttle hand gives the left stick's move and A/B up and down; the stick hand the right stick's turn and pitch, the triggers' fire, and a flight stick's tilt: sideways to roll, forward to put the nose down, back to pull it up, twist to yaw. Dampeners, lights, landing gear, Y and Menu work without holding. Off: the sticks and grips fly the ship as on a gamepad."));

        private static readonly FlagSetting yawFromTwist = Add(new FlagSetting("yawFromTwist", "Yaw from twisting the stick hand", "Controls", true,
            tooltip: "While you hold the stick in a ship seat, twisting your hand to the right or left yaws the ship that way, as a flight stick's twist does. Off: the twist adds no yaw. Tilting the hand forward and back (pitch) and sideways (roll) are always on."));

        private static readonly FlagSetting yawFromThumbstick = Add(new FlagSetting("yawFromThumbstick", "Yaw from the thumbstick", "Controls", true,
            tooltip: "While you hold the stick in a ship seat, the right thumbstick's left and right yaws the ship. Off: it gives no yaw there (up and down still pitches), so the stick hand's twist is the only yaw. The thumbstick is unchanged in a turret, on foot, and with Hands on the controls to fly off."));

        private static readonly FlagSetting handFlying = Add(new FlagSetting("handFlying", "Hand flying", "Controls", false,
            tooltip: "In a ship seat, press a grip with your hand by the stick (dominant hand) or throttle (other hand) to take hold of it, and fly by moving the controller. Off: thumbsticks and grips only."));

        private static readonly NumberSetting stickMaxAngle = Add(new NumberSetting("stickMaxAngle", "Stick full deflection", "Controls",
            25f, 10f, 45f, 1f, unit: "deg", decimals: 0,
            tooltip: "How far you tilt or twist the held stick for full pitch, roll or yaw. Smaller is twitchier."));

        private static readonly NumberSetting throttleTravel = Add(new NumberSetting("throttleTravel", "Throttle travel", "Controls",
            0.10f, 0.05f, 0.25f, 0.01f, unit: "cm", decimals: 0, displayScale: 100f,
            tooltip: "How far you push the held throttle for full thrust in that direction. Smaller is twitchier."));

        private static readonly NumberSetting handFlyingSensitivity = Add(new NumberSetting("handFlyingSensitivity", "Hand flying sensitivity", "Controls",
            1f, 0.25f, 3f, 0.05f, decimals: 2,
            tooltip: "How fast the held stick turns a ship. 1 matches a gamepad pushed all the way."));

        private static readonly FlagSetting leftHanded = Add(new FlagSetting("leftHanded", "Left-handed", "Controls", false,
            tooltip: "Your left hand is the main one. It points the menu laser (its trigger clicks, its grip right-clicks, its thumbstick scrolls) and holds tools and guns, with the right hand on the front grip. Its trigger fires, welds, grinds and drills, the right trigger is the secondary, and its stick click reloads. The datapad stows on your right forearm. Building and seats keep their buttons. Off: the right hand."));

        private static readonly TextSetting gripFit = Add(new TextSetting("gripFit", "Grip fit", "Controls", hidden: true,
            tooltip: "How your character's hand sits on each kind of controller, one entry per OpenXR interaction profile, separated by semicolons: profile:pitch,roll,yaw,x,y,z (degrees, then millimetres), as the RenderDebug keys HandIKPitch, HandIKRoll, HandIKYaw, HandIKX, HandIKY and HandIKZ set them for the controllers in use. A controller with no entry gets the plugin's own placement."));

        private static readonly NumberSetting hapticStrength = Add(new NumberSetting("hapticStrength", "Haptic strength", "Controls",
            1f, 0f, 1f, 0.05f, unit: "%", decimals: 0, displayScale: 100f,
            tooltip: "How hard the controllers buzz when you fire, weld, grind or drill, place a block or get hit, and for the heartbeat. 0 turns all of it off."));
        private static readonly FlagSetting hapticHeartbeat = Add(new FlagSetting("hapticHeartbeat", "Heartbeat", "Controls", true,
            tooltip: "A slow double pulse in both controllers while your health or your suit's oxygen is low, until it recovers. Off: hits and tools still buzz."));
        private static readonly FlagSetting grabToUse = Add(new FlagSetting("grabToUse", "Grab to use", "Controls", true,
            tooltip: "Closing the grip with your hand on anything the Use key works on uses it, as the key does: seats, terminals, screens, cargo and connectors, medical rooms (heals while held), stores and the rest. A door or hatch has to be pulled or pushed a hand's width while held; a vending machine's Buy held for a moment. In a seat it works on what is within arm's reach, not the seat itself. With nothing in reach the grip keeps its usual job. Ladders and buttons have their own grab and fingertip. Not while that hand holds a tool, a menu is up or you are on a ladder."));

        private static readonly NumberSetting wristPanelSize = Add(new NumberSetting("wristPanelSize", "Wrist panel size", "Controls",
            0.3f, 0.2f, 0.5f, 0.01f, unit: "m", decimals: 2,
            tooltip: "How wide the quick-actions panel is when it opens on your left wrist, in metres. A larger one is easier to read and to point at, and covers more of your view. You see the change as the panel opens."));

        private static readonly FlagSetting wristPanel = Add(new FlagSetting("wristPanel", "Wrist panel", "Controls", true,
            tooltip: "On foot and on the jetpack, Y on the left controller opens the quick-actions panel on your left wrist (inventory, terminal, G menu, lights, helmet, dampeners, broadcast, symmetry, colour picker); Y again closes it. Off: Y does nothing there."));

        private static readonly FlagSetting datapad = Add(new FlagSetting("datapad", "Datapad", "Controls", true,
            tooltip: "A datapad stowed on your left forearm (right forearm when left-handed): grip the forearm with the other hand to pull it off, and hold it to read. It has chat (the channel, recent messages, quick phrases, and the on-screen keyboard to type), your GPS list with a button to share one to chat, and your terminal. Touch it with the other hand's fingertip, or point the laser. Let go near the forearm to put it back; let go elsewhere and it stays there a few seconds before it returns. Grip it with the free hand to swap hands. A new chat line while it is stowed lights the forearm and ticks the controller. Off: chat through the actions wheel only."));
        private static readonly NumberSetting datapadWidth = Add(new NumberSetting("datapadWidth", "Datapad size", "Controls",
            0.34f, 0.22f, 0.5f, 0.01f, unit: "m", decimals: 2,
            tooltip: "How wide the datapad is. Larger is easier to touch and read, smaller is lighter to hold up."));

        private static readonly FlagSetting fingerPress = Add(new FlagSetting("fingerPress", "Press with fingertip", "Controls", true,
            tooltip: "On foot, touching a push-button with the tip of a controller's index finger presses it, as the Use key does: the buttons of a button panel, and the next, previous and pause buttons of a jukebox. Doors, seats, terminals and cargo are not touched. Not while that hand holds a tool, a menu is up or you are on a ladder or seated. Off: only the Use key and the hand's ray."));

        private static readonly FlagSetting helmetAndLamp = Add(new FlagSetting("helmetAndLamp", "Suit lamp by hand", "Controls", true,
            tooltip: "On foot, grip your helmet with either hand, or tap the side of your head with a fingertip, to switch the suit lamp. A grip anywhere else keeps its old job (Use, the toolbar wheel). The helmet itself is on the wrist panel and the actions wheel. Off: the key and the wrist panel only."));
        private static readonly FlagSetting moveViewByHand = Add(new FlagSetting("moveViewByHand", "Move the external view by hand", "Controls", true,
            tooltip: "Grip the external view screen with either hand to pick it up and put it anywhere; it stays where you let go, the next time too. The External view page's Reset puts it back on the dash. Off: it stays on the dash."));
        private static readonly FlagSetting touchToolbar = Add(new FlagSetting("touchToolbar", "Touch toolbar on the cockpit screen", "Controls", true,
            tooltip: "Set any cockpit screen to the script \"Touch Toolbar\" in its screen settings and, piloting, that screen shows the ship's toolbar: tap a slot to use it, as its number key does, and the arrows to turn the page. A screen out of reach is tapped with the laser and the trigger. With no screen set to it, this puts it on the cockpit's most central screen while you pilot; only your view of that screen changes, and it gets its own picture back when you leave the seat. Off: only screens set to Touch Toolbar show it."));
        private static readonly FlagSetting grabItems = Add(new FlagSetting("grabItems", "Grab items", "Controls", true,
            tooltip: "On foot, close a hand's grip on a floating item (ore, ice, components, a dropped tool) to take it out of the air and hold it; open the hand and it flies on at the hand's speed. Let go of it at your chest and it goes into your inventory. Heavy stacks come along slowly. Off: a grip near an item means what it does in empty air."));
        private static readonly FlagSetting grabHulls = Add(new FlagSetting("grabHulls", "Grab hulls", "Controls", true,
            tooltip: "With no gravity, press a grip with your hand on a ship's or station's hull, or on an asteroid, to take hold of it, and pull or push to move along it, as in Lone Echo; with both hands you climb hand over hand. Let go while your hand is moving and you fly on at its speed, up to the push-off speed below; the jetpack's dampeners stop you only if they are on. Off: a grip on a hull is Use or the toolbar wheel, as in empty air."));

        private static readonly NumberSetting pushOffMaxSpeed = Add(new NumberSetting("pushOffMaxSpeed", "Push-off top speed", "Controls",
            6f, 1f, Hands.HullGrabMath.HoldMaxSpeed, 0.5f, unit: "m/s", decimals: 1,
            tooltip: "The fastest you fly off when you let go of a hull or asteroid with your hand moving: a hard throw-off leaves at this speed, a gentle one at your hand's. Lower is easier to control, higher crosses a big ship quicker."));

        private static readonly FlagSetting grabLadders = Add(new FlagSetting("grabLadders", "Grab ladders", "Controls", true,
            tooltip: "Press a grip with your hand on a ladder to get on it, then pull your hand down to climb a step up, or push it up to climb down. Off: ladders by the Use key and the move stick only."));
        private static readonly FlagSetting handsStopOnGlass = Add(new FlagSetting("handsStopOnGlass", "Hands stop on glass", "Controls", true,
            tooltip: "Your hands stop on window glass and cockpit canopies as they stop on hulls: from outside on foot, and from inside in a seat. Off: they pass through glass, and stop only where the block's own collision is."));

        private static readonly FlagSetting twoHandedTools = Add(new FlagSetting("twoHandedTools", "Two-handed tools", "Controls", true,
            tooltip: "On foot, close your other hand's grip on the front of the tool or gun you hold, where the astronaut's other hand goes, to hold it with both hands: it then points along the line from your tool hand to your other hand. A light tick tells you the hand is close enough. Let go and it goes back to one hand. A pistol is steadied but still aims with the tool hand. A grip anywhere else keeps its old job (the toolbar wheel). Off: tools in one hand only."));

        private static readonly FlagSetting twoHandGripStays = Add(new FlagSetting("twoHandGripStays", "Two-hand grip stays on", "Controls", true,
            tooltip: "Once your other hand has taken the tool's front grip, the astronaut's hand stays on it for as long as you hold that grip button, however far your real hand moves. Up to 15 cm off the grip your other hand still aims the tool; further off, its pull eases away and the tool points where your tool hand points. Let go of the button to go back to one hand. Off: pulling the hand 15 cm off the grip lets go as well."));

        private static readonly FlagSetting physicalReload = Add(new FlagSetting("physicalReload", "Reload by hand", "Controls", true,
            tooltip: "Reloading a hand weapon is done with your hands. Click the right stick (or close your other hand at the magazine) and the magazine drops out. Once it is out, reach to your other hand's hip and close the grip to take a fresh one: a tick says you have it, two light ticks that there are none for this weapon. Push it up into the well under the gun and it clicks home; the weapon is loaded then. Let go of it anywhere else and it falls, or back at the hip and it goes back; neither costs a magazine. The rounds left in a dropped magazine are lost, as with the game's own reload. An empty weapon drops its magazine by itself. R on the keyboard still reloads the game's way. Off: the click reloads on the game's timer."));

        private static readonly ChoiceSetting kickback = Add(new ChoiceSetting("kickback", "Tool and weapon kickback", "Controls", new[] { "Off", "Light", "Full" }, 2,
            tooltip: "A gun or tool made for two hands (the rifles, rocket launchers, grinders and hand drills) kicks hard when you use it in one hand: a gun climbs, snaps and shoves back with every shot while the trigger stays down. A grinder or drill fights you whenever it runs, cutting or not: it yanks as it spins up, climbs and pulls to the side more the longer you hold it, and jerks hard; the drill twists your wrist against its spin. A pistol in one hand flips up with each shot and comes back on its own: time your shots to it, or it walks up. Let go of the trigger and it settles back into your hand. Hold it in both hands and it stays steady. Only the tool and where it points move; the game's own recoil, which turns your view, is left out of a shot only when this kick is at least as strong (see Game recoil in VR). Light: half as hard. Off: no kick."));

        private static readonly ChoiceSetting gameRecoil = Add(new ChoiceSetting("gameRecoil", "Game recoil in VR", "Controls", new[] { "Auto", "Off (comfort)" }, 0,
            tooltip: "The game's own gun recoil turns your view with each shot. Auto: it is left out of a shot only when the tool and weapon kickback kicks that shot at least as hard, and applies otherwise (kickback Off or weaker, your hand not tracked). Off keeps the view still when firing (single player only; in multiplayer it works as Auto, so no one gains an edge)."));

        private static readonly FlagSetting vrButtonNames = Add(new FlagSetting("vrButtonNames", "VR button names", "Controls", true,
            tooltip: "The game's prompts name the controller buttons instead of the keys: \"Press [Right grip] to open\", \"[A] to jump\", \"[Y > Inventory]\" for what the wrist panel opens. A control the controllers have no way to press keeps its key. Off: the keyboard's names."));

        private static readonly FlagSetting vrRadialMenu = Add(new FlagSetting("vrRadialMenu", "VR radial menus", "Controls", true,
            tooltip: "The tools wheel and the game's actions wheel say which controller buttons work them, and the game's actions wheel leaves out what has no use in VR: the keyboard help screen, the spectator camera, and the camera switch while third person is off. Off: the game's wheels as they are on a gamepad."));

        private static readonly FlagSetting vrActionsWheel = Add(new FlagSetting("vrActionsWheel", "VR actions wheel", "Controls", true,
            tooltip: "Clicking the left stick on the tools wheel opens the game's actions (inventory, dampeners, lights, helmet, HUD, broadcasting, and the Menu, View and Blueprint pages) as a wheel at your left hand: point at an entry with the hand or the stick, let go of the grip or pull the trigger to use it, B to close. Off: the game's own gamepad radial screen."));

        private static readonly FlagSetting pingHoldX = Add(new FlagSetting("pingHoldX", "Hold X to ping", "Controls", true,
            tooltip: "Hold X and point (in a seat, look) at anything: ore, a block, a ship, a player, a spot on a planet. The laser shows what it is on, named on the HUD. Let go of X to ping it: a GPS marker for a while, and a line in the chat with the GPS in it, which every player's game adds to their GPS list. Pull the trigger while holding X to keep it as your own GPS instead. A tap of X is still the dampeners, given as you let go. Off: X is the dampeners as you press it, and the actions wheel's Ping and Mark GPS still work."));

        private static readonly NumberSetting pingSeconds = Add(new NumberSetting("pingSeconds", "Ping lasts", "Controls",
            60f, 10f, 180f, 10f, unit: "s", decimals: 0,
            tooltip: "How long your own marker for a ping stays on the HUD. Other players get the GPS from the chat line, in their GPS list, for as long as their game keeps it (three minutes, unless they keep it)."));

        private static readonly ChoiceSetting pingChat = Add(new ChoiceSetting("pingChat", "Ping chat", "Controls",
            new[] { "Faction, else global", "Faction only", "Off" }, 0,
            tooltip: "Where a ping's chat line goes: your faction's channel, or the global one when you have no faction; your faction's only; or nowhere (the marker is then only yours)."));
        private static readonly FlagSetting shipModel = Add(new FlagSetting("shipModel", "Ship model in your hand", "Interface", true,
            tooltip: "The actions wheel's Ship model entry holds a small hologram of your ship above your off hand: seated, the ship you are in; on foot, the one your hand points at or the nearest. Grip it with either hand to turn it over, with both to make it bigger or smaller; let go and it stays there. Damaged blocks show red, blocks lost while it is out show dark, and pointing at a block on it outlines the real block with its name and integrity. The same entry puts it away. Off: no entry on the wheel."));
        private static readonly FlagSetting shipModelThrusters = Add(new FlagSetting("shipModelThrusters", "Ship model: thrusters", "Interface", true,
            tooltip: "The ship model shows the thrusters in orange. Also switched on the actions wheel's Model overlays page."));
        private static readonly FlagSetting shipModelPower = Add(new FlagSetting("shipModelPower", "Ship model: power", "Interface", true,
            tooltip: "The ship model shows what makes power (reactors, batteries, solar panels, wind turbines, engines) in yellow, inside the hull too. Also switched on the actions wheel's Model overlays page."));
        private static readonly FlagSetting shipModelDoors = Add(new FlagSetting("shipModelDoors", "Ship model: doors", "Interface", true,
            tooltip: "The ship model shows doors, airlocks and hangar doors in green. Also switched on the actions wheel's Model overlays page."));
        private static readonly FlagSetting shipModelWeapons = Add(new FlagSetting("shipModelWeapons", "Ship model: weapons", "Interface", true,
            tooltip: "The ship model outlines guns and turrets in red. Also switched on the actions wheel's Model overlays page."));

        private static readonly FlagSetting headsetAudio = Add(new FlagSetting("headsetAudio", "Sound through the headset", "Sound", true, restart: true,
            tooltip: "The game's sound plays through the headset's own audio device, the one the headset's runtime names for VR (with a Quest on Link, Headphones (Oculus Virtual Audio Device)), whatever the Windows default output is. Without a headset, or with a runtime that names no device, it plays through the Windows default. Off: always the Windows default. Takes effect the next time the game starts."));

        private static readonly FlagSetting enabled = Add(new FlagSetting("enabled", "VR enabled", "Advanced", true, restart: true, hidden: true,
            // Not in the menu: turned off there, VR would have no menu to turn it back on. enabled=false in the file, or -novr.
            tooltip: "Start the game with VR."));

        private static readonly NumberSetting ipd = Add(new NumberSetting("ipd", "Fallback IPD", "Advanced",
            0.064f, 0.04f, 0.09f, 0.001f, unit: "mm", decimals: 0, displayScale: 1000f,
            tooltip: "Distance between the eyes, used when no headset gives the real one (-ipd)."));

        private static readonly NumberSetting mirrorWidth = Add(new NumberSetting("mirrorWidth", "Mirror width", "Advanced",
            1920f, 640f, 3840f, 40f, unit: "px", decimals: 0, restart: true, integer: true,
            tooltip: "Width of the game window on your monitor (-vrmirror 1920x1080). Your own resolution is left alone."));

        private static readonly NumberSetting mirrorHeight = Add(new NumberSetting("mirrorHeight", "Mirror height", "Advanced",
            1080f, 360f, 2160f, 40f, unit: "px", decimals: 0, restart: true, integer: true,
            tooltip: "Height of the game window on your monitor."));

        // ---- What the rest of the plugin reads. ----

        /// <summary>-novr: load the plugin but leave the game untouched.</summary>
        public static bool Enabled => enabled.Value;

        /// <summary>Interpupillary distance used when no headset supplies eye poses (-ipd 0.064), metres.</summary>
        public static float FallbackIpd => ipd.Value;

        /// <summary>Size of the desktop mirror window (-vrmirror 1920x1080).</summary>
        public static int MirrorWidth => (int)mirrorWidth.Value;

        public static int MirrorHeight => (int)mirrorHeight.Value;

        /// <summary>The game's sound goes to the headset's audio device, when the runtime names one (<see cref="Rendering.HeadsetAudio"/>). Read at the next game start.</summary>
        public static bool HeadsetAudio => headsetAudio.Value;

        /// <summary>The game's toolbar stays on the HUD on foot too; off (the default) it is left out there (<see cref="Rendering.HudToolbar"/>).</summary>
        public static bool ShowToolbarOnFoot => showToolbarOnFoot.Value;

        /// <summary>The HUD's parts are on the visor, curved round the head, and its markers on the flat panel (<see cref="Rendering.HudVisor"/>); off, all of it is on the panel.</summary>
        public static bool HudVisor => hudVisor.Value;

        /// <summary>How far ahead of the eyes the GUI and HUD panel floats, metres (-hudDistance 1.5).</summary>
        public static float HudDistance => hudDistance.Value;

        /// <summary>How wide the GUI and HUD panel looks, degrees (-hudWidth 55); its height follows the GUI's aspect.</summary>
        public static float HudWidthDegrees => hudWidth.Value;

        /// <summary>The "Refresh rate" setting's value by name ("Runtime default", "72 Hz"), which says when it has changed.</summary>
        public static string RefreshRateName => refreshRate.Name;

        /// <summary>The refresh rate asked of the headset, Hz; 0 leaves it as the runtime has it.</summary>
        public static float RefreshRateHz =>
            float.TryParse(refreshRate.Name.Split(' ')[0], NumberStyles.Float, CultureInfo.InvariantCulture, out float hz) ? hz : 0f;

        /// <summary>Sets the refresh rates the options screen offers, in Hz, once the runtime has listed them.</summary>
        public static void OfferRefreshRates(IEnumerable<float> rates)
        {
            var names = new List<string>();
            foreach (float rate in rates)
                names.Add(rate.ToString("0.##", CultureInfo.InvariantCulture) + " Hz");
            refreshRate.Offer(names);
        }

        /// <summary>How large the HUD's parts on the visor are, 1 as the HUD width gives (<see cref="Rendering.HudVisor"/>).</summary>
        public static float HudTextSize => hudTextSize.Value;

        /// <summary>How solid the dark plate behind HUD text on the visor is, 0 (none) to 0.9.</summary>
        public static float HudTextBacking => hudTextBacking.Value;

        /// <summary>How wide the quick-actions panel is on the left wrist, metres (<see cref="Rendering.WorldMenu"/>).</summary>
        public static float WristPanelWidth => wristPanelSize.Value;

        /// <summary>How strongly the comfort vignette darkens the edges of the view while the sticks move or turn you, 0 (off) to 1 (<see cref="Rendering.Vignette"/>).</summary>
        public static float ComfortVignette => comfortVignette.Value;

        /// <summary>A cut of the view is faded from black (<see cref="Rendering.CameraCut"/>).</summary>
        public static bool ComfortFade => comfortFade.Value;

        /// <summary>
        /// The camera shake setting: 0 Off, 1 Low, 2 Full. RenderDebug CameraShake=0/1/2 takes its place (<see cref="CameraShakeOverride"/>).
        /// </summary>
        public static int CameraShakeLevel => CameraShakeOverride ?? cameraShake.Value;

        /// <summary>RenderDebug CameraShake=0 (Off), 1 (Low) or 2 (Full) in place of the setting; null: the setting.</summary>
        public static int? CameraShakeOverride
        {
            get
            {
                int level = cameraShakeOverride;
                return level < 0 ? (int?)null : level;
            }
            set => cameraShakeOverride = value ?? -1; // one int, so the game thread never reads half of it
        }

        private static volatile int cameraShakeOverride = -1;

        /// <summary>The share of the game's camera shake that reaches the view: 0 (Off), a quarter (Low), 1 (Full) (<see cref="Tracking.CameraShake"/>).</summary>
        public static float CameraShakeScale
        {
            get
            {
                int level = CameraShakeLevel;
                return level <= 0 ? 0f : level == 1 ? 0.25f : 1f;
            }
        }
        /// <summary>The external view screen is on. The actions wheel turns it on and off, and so does the options screen.</summary>
        public static bool ExternalViewOn
        {
            get => externalView.Value;
            set => externalView.Set(value);
        }

        /// <summary>The external view shows one of the ship's camera blocks (<see cref="ExternalViewCameraId"/>) instead of the chase camera.</summary>
        public static bool ExternalViewIsCamera
        {
            get => externalViewSource.Value == 1;
            set => externalViewSource.Set(value ? 1 : 0);
        }

        /// <summary>The camera block the external view shows when it is on a camera: its entity id; 0 for none.</summary>
        public static long ExternalViewCameraId
        {
            get
            {
                SplitExternalViewCamera(out long id, out _);
                return id;
            }
        }

        /// <summary>That camera's name, as it was when it was chosen.</summary>
        public static string ExternalViewCameraName
        {
            get
            {
                SplitExternalViewCamera(out _, out string name);
                return name;
            }
        }

        /// <summary>Remembers the camera block the external view shows: its entity id, and its name (for the label, and to find it again if the id is gone).</summary>
        public static void SetExternalViewCamera(long entityId, string name) =>
            externalViewCamera.Set(entityId.ToString(CultureInfo.InvariantCulture) + "|" + (name ?? string.Empty));

        private static void SplitExternalViewCamera(out long id, out string name)
        {
            id = 0L;
            name = string.Empty;
            string value = externalViewCamera.Value;
            int bar = value.IndexOf('|');
            if (bar <= 0 || !long.TryParse(value.Substring(0, bar), NumberStyles.Integer, CultureInfo.InvariantCulture, out id))
            {
                id = 0L;
                return;
            }
            name = value.Substring(bar + 1);
        }

        /// <summary>How wide the external view screen is in the headset, metres; its height follows the picture's 16:9.</summary>
        public static float ExternalViewSize
        {
            get => externalViewSize.Value;
            set => externalViewSize.Set(Clamp(value, externalViewSize.Min, externalViewSize.Max));
        }

        private static float Clamp(float value, float min, float max) => value < min ? min : value > max ? max : value;

        /// <summary>The narrowest and the widest the external view screen may be, metres.</summary>
        public static float ExternalViewSizeMin => externalViewSize.Min;

        public static float ExternalViewSizeMax => externalViewSize.Max;

        public static float ExternalViewSizeDefault => externalViewSize.DefaultValue ?? 0.3f;

        /// <summary>Third person: degrees the camera has gone round from behind, positive to the left (-180..180).</summary>
        public static float ExternalViewOrbit
        {
            get => externalViewOrbit.Value;
            set => externalViewOrbit.Set(value);
        }

        /// <summary>Third person: degrees the camera has gone over (positive) or under from where it starts.</summary>
        public static float ExternalViewElevation
        {
            get => externalViewElevation.Value;
            set => externalViewElevation.Set(Clamp(value, externalViewElevation.Min, externalViewElevation.Max));
        }

        /// <summary>Third person: the camera's distance as a multiple of the one that suits the size of what is followed.</summary>
        public static float ExternalViewDistance
        {
            get => externalViewDistance.Value;
            set => externalViewDistance.Set(Clamp(value, externalViewDistance.Min, externalViewDistance.Max));
        }

        public static float ExternalViewDistanceMin => externalViewDistance.Min;

        public static float ExternalViewDistanceMax => externalViewDistance.Max;

        /// <summary>Where the screen was moved by hand (see the setting's tooltip); empty for the dash.</summary>
        public static string ExternalViewPlace
        {
            get => externalViewPlace.Value;
            set => externalViewPlace.Set(value);
        }

        /// <summary>The eyes are drawn smaller while the GPU is over its time (<see cref="Rendering.DynamicResolution"/>).</summary>
        public static bool DynamicResolution => dynamicResolution.Value;

        /// <summary>The parts of the eyes the lenses never show are left out of the costly passes (<see cref="Rendering.LensMask"/>).</summary>
        public static bool LensMask => lensMask.Value;

        /// <summary>The external view is drawn every other frame.</summary>
        public static bool ExternalViewHalfRate => externalViewHalfRate.Value;
        /// <summary>Third person in a ship: the camera keeps the horizon level (<see cref="Tracking.ChaseCamera"/>) instead of rolling with the ship.</summary>
        public static bool LevelChaseCamera => levelChaseCamera.Value;

        /// <summary>Your own view may be third person (<see cref="Tracking.ThirdPersonGate"/>); off, it stays first person on foot and in every seat.</summary>
        public static bool ThirdPerson => thirdPerson.Value;

        /// <summary>How far ahead of the head a menu's panel is put, where it stays, metres (-menuDistance 2).</summary>
        public static float MenuDistance => menuDistance.Value;

        /// <summary>How wide a menu's panel looks from the head, degrees (-menuWidth 60); its height follows the GUI's aspect.</summary>
        public static float MenuWidthDegrees => menuWidth.Value;

        /// <summary>How wide the panel looks while no world is loaded (the main menu and the screens off it), degrees; the world's menus use <see cref="MenuWidthDegrees"/>.</summary>
        public static float MainMenuWidthDegrees => mainMenuWidth.Value;

        /// <summary>Clicking a text box (or the game asking for a virtual keyboard) opens the on-screen keyboard while the hands are active (<see cref="Gui.VRKeyboardHook"/>).</summary>
        public static bool VRKeyboard => vrKeyboard.Value;

        /// <summary>
        /// Seated play (the play position): the eye's height is taken at the recentre and the calibration is not used, and a
        /// real crouch does not crouch the character (<see cref="Tracking.HeadCentre"/>, <see cref="Tracking.BodyGuard"/>).
        /// False: standing play. RenderDebug SeatedPlay=0/1 overrides the setting (<see cref="SeatedPlayOverride"/>).
        /// </summary>
        public static bool SeatedPlay => SeatedPlayOverride ?? playPosition.Value == 1;

        /// <summary>RenderDebug SeatedPlay=1 (seated) or 0 (standing) in place of the setting; null: the setting.</summary>
        public static bool? SeatedPlayOverride { get; set; }

        /// <summary>
        /// The player's eye height above the floor when standing, metres, from height calibration
        /// (<see cref="Tracking.HeightCalibration"/>); in standing play the character's eyes are put there. Null: not
        /// calibrated, and the view's height is the headset's height relative to where it was at the last recentre (or where
        /// tracking started).
        /// </summary>
        public static float? EyeHeight
        {
            get => eyeHeight.Effective;
            set => eyeHeight.Set(value);
        }

        /// <summary>Raises (+) or lowers (-) the view, metres, on top of the calibration.</summary>
        public static float HeightOffset
        {
            get => heightOffset.Value;
            set => heightOffset.Set(value);
        }

        /// <summary>The character's arms stretch as far as the player's arm span needs (<see cref="Tracking.ArmSpan"/>); off: as they always have.</summary>
        public static bool ArmSpanScaling => armSpanScaling.Value;

        /// <summary>
        /// The distance between the player's hands with the arms straight out to the sides, metres, from height calibration
        /// (<see cref="Tracking.HeightCalibration"/>). Null: not measured.
        /// </summary>
        public static float? ArmSpan
        {
            get => armSpan.Effective;
            set => armSpan.Set(value);
        }

        /// <summary>The grip fit per controller profile, as the settings file holds it (<see cref="Tracking.GripFit"/>).</summary>
        public static string GripFit
        {
            get => gripFit.Value;
            set => gripFit.Set(value);
        }

        /// <summary>On foot, the eye is kept out of the character's own body (<see cref="Tracking.BodyGuard"/>).</summary>
        public static bool BodyClearance => bodyClearance.Value;

        /// <summary>On foot, a real crouch of the head crouches the character (<see cref="Tracking.BodyGuard"/>).</summary>
        public static bool CrouchWithHead => crouchWithHead.Value;

        /// <summary>
        /// In a seat, how far forward, up and down the head can go from the seated character's head, metres; sideways is four
        /// fifths of it, back is the body's (<see cref="Tracking.BodyVolume.LockSeat"/>, <see cref="Tracking.CockpitHead"/>).
        /// </summary>
        public static float SeatReach => seatReach.Value;

        /// <summary>On foot, the head is held within reach of the character's head (<see cref="Tracking.BodyGuard"/>).</summary>
        public static bool HeadLock => headLock.Value;

        /// <summary>On foot, how far forward the head can go from the character's head, metres; sideways is four fifths of it, back is the body's (<see cref="Tracking.BodyVolume.LockHead"/>).</summary>
        public static float HeadReach => headReach.Value;

        /// <summary>How far one flick of the right stick turns the character, degrees.</summary>
        public static float SnapTurnDegrees => snapTurn.Value;

        /// <summary>The right stick turns the body steadily (<see cref="SmoothTurnSpeedDegrees"/>) instead of in snap-turn steps.</summary>
        public static bool SmoothTurn => smoothTurn.Value;

        /// <summary>Degrees a second the body turns with the right stick pushed all the way over, when <see cref="SmoothTurn"/> is on.</summary>
        public static float SmoothTurnSpeedDegrees => smoothTurnSpeed.Value;

        /// <summary>Zero-g pitch and roll on the jetpack (<see cref="Input.VRControls"/>): 0 Smooth, 1 Snap, 2 Off.</summary>
        public static int ZeroGTurn => zeroGTurn.Value;

        public const int ZeroGTurnSmooth = 0, ZeroGTurnSnap = 1, ZeroGTurnOff = 2;

        /// <summary>How hard the controllers buzz, 0 (off) to 1 (the strengths in <see cref="Hands.Haptics"/> as they are).</summary>
        public static float HapticStrength => hapticStrength.Value;

        /// <summary>The slow pulse at low health or oxygen (<see cref="Hands.Haptics"/>).</summary>
        public static bool HapticHeartbeat => hapticHeartbeat.Value;

        /// <summary>How far a thumbstick has to be pushed before it counts, 0 to 1.</summary>
        public static float StickDeadzone => stickDeadzone.Value;

        /// <summary>How hard the right stick and the grips turn a ship, a turret or a camera, against a gamepad at full push (1).</summary>
        public static float ShipStickSensitivity => shipStickSensitivity.Value;

        /// <summary>In a pilot seat the ship flies only while its stick and throttle are held (<see cref="Input.ControlGrip"/>).</summary>
        public static bool HandsOnControls => handsOnControls.Value;

        /// <summary>The held stick's twist yaws the ship (<see cref="Input.ControlGrip.PitchAndYaw"/>).</summary>
        public static bool YawFromTwist => yawFromTwist.Value;

        /// <summary>The right thumbstick's left and right yaws the ship while the stick is held (<see cref="Input.ControlGrip"/>).</summary>
        public static bool YawFromThumbstick => yawFromThumbstick.Value;

        /// <summary>Grab the stick and throttle in a ship seat and fly by moving the controllers (<see cref="Input.HandFlight"/>).</summary>
        public static bool HandFlying => handFlying.Value;

        /// <summary>How far the held stick is tilted or twisted for full pitch, roll or yaw, degrees.</summary>
        public static float StickMaxAngle => stickMaxAngle.Value;

        /// <summary>How far the held throttle is pushed for full thrust, metres.</summary>
        public static float ThrottleTravel => throttleTravel.Value;

        /// <summary>How hard the held stick turns a ship against a gamepad at full push (1).</summary>
        public static float HandFlyingSensitivity => handFlyingSensitivity.Value;

        /// <summary>The hand that points the menu laser (<see cref="Input.Laser"/>). Right unless "Left-handed" is on.</summary>
        public static Input.Hand DominantHand => leftHanded.Value ? Input.Hand.Left : Input.Hand.Right;

        /// <summary>Y opens the quick-actions panel on the wrist (<see cref="Input.WristButton"/>).</summary>
        public static bool WristPanel => wristPanel.Value;

        /// <summary>The datapad on the off hand's forearm, pulled off and held to chat and read (<see cref="Hands.Datapad"/>).</summary>
        public static bool Datapad => datapad.Value;

        /// <summary>The datapad's width, metres.</summary>
        public static float DatapadWidth => datapadWidth.Value;

        /// <summary>The suit and body status is on a screen on the gauntlet, and not on the HUD (<see cref="Rendering.WristStatus"/>).</summary>
        public static bool WristStatus => wristStatus.Value;

        /// <summary>Closing the grip on anything the Use key works on uses it, on foot and from a seat (<see cref="Hands.GrabUse"/>).</summary>
        public static bool GrabToUse => grabToUse.Value;

        /// <summary>Touching a use object with a fingertip presses it (<see cref="Hands.FingerPress"/>).</summary>
        public static bool FingerPress => fingerPress.Value;

        /// <summary>The helmet and the lamp by hand (<see cref="Hands.HeadGestures"/>).</summary>
        public static bool HelmetAndLamp => helmetAndLamp.Value;

        /// <summary>A grip on the external view screen picks it up and moves it (<see cref="Hands.ViewScreenGrab"/>).</summary>
        public static bool MoveViewByHand => moveViewByHand.Value;
        /// <summary>The ship's toolbar on a cockpit screen, touched to use it (<see cref="Gui.TouchToolbar"/>).</summary>
        public static bool TouchToolbar => touchToolbar.Value;
        /// <summary>A grip on a floating item holds it, opening the hand throws it, letting go at the chest stows it (<see cref="Hands.ItemGrab"/>).</summary>
        public static bool GrabItems => grabItems.Value;
        /// <summary>With no gravity, a grip on a hull takes hold of it and the body follows the hand (<see cref="Hands.HullGrab"/>).</summary>
        public static bool GrabHulls => grabHulls.Value;

        /// <summary>The fastest a let-go of a hull or asteroid leaves it, m/s (<see cref="Hands.HullGrab"/>).</summary>
        public static float PushOffMaxSpeed => pushOffMaxSpeed.Value;

        /// <summary>The hands stop on window and canopy glass as on hulls (<see cref="Hands.GlassPanes"/>).</summary>
        public static bool HandsStopOnGlass => handsStopOnGlass.Value;

        /// <summary>A grip on a ladder gets on it and climbs it by the hand's pull (<see cref="Hands.HullGrab"/>).</summary>
        public static bool GrabLadders => grabLadders.Value;

        /// <summary>The other hand's grip on the held tool's front grip holds it in both hands, aimed along the line between them (<see cref="Hands.TwoHand"/>).</summary>
        public static bool TwoHandedTools => twoHandedTools.Value;

        /// <summary>The other hand stays on the tool's grip while its grip button is held, however far the hand moves (<see cref="Hands.TwoHand.Stays"/>).</summary>
        public static bool TwoHandGripStays => twoHandGripStays.Value;

        /// <summary>A hand weapon's reload press drops its magazine, and a fresh one is taken from the hip and pushed into the well by hand (<see cref="Hands.Reload"/>).</summary>
        public static bool PhysicalReload => physicalReload.Value;

        /// <summary>The kickback setting: 0 Off, 1 Light, 2 Full (<see cref="Hands.Kickback"/>).</summary>
        public static int KickbackLevel => kickback.Value;

        /// <summary>The game recoil setting is Off (comfort): the game's view recoil is left out in single player (<see cref="Hands.Kickback"/>).</summary>
        public static bool GameRecoilOff => gameRecoil.Value == 1;

        /// <summary>The share of the one-handed kick: none (Off), a half (Light), all (Full).</summary>
        public static double KickbackShare => KickbackLevel <= 0 ? 0.0 : KickbackLevel == 1 ? 0.5 : 1.0;

        /// <summary>The HUD reminders for backing out of a menu, and the first-time ones on foot and in the inventory (<see cref="Input.ControlHints"/>).</summary>
        public static bool ControlHints => controlHints.Value;

        /// <summary>Prompts name the controller buttons (<see cref="Input.VRPrompts"/>).</summary>
        public static bool VRButtonNames => vrButtonNames.Value;

        /// <summary>The radial menus are labelled for the controllers and leave out what VR has no use for (<see cref="Gui.VRRadialMenus"/>).</summary>
        public static bool VRRadialMenu => vrRadialMenu.Value;

        /// <summary>The left stick click on the tools wheel opens the actions wheel at the hand, not the game's radial screen (<see cref="Gui.ActionsWheel"/>).</summary>
        public static bool VRActionsWheel => vrActionsWheel.Value;

        /// <summary>Holding X aims a ping, a tap is the dampeners (<see cref="Hands.Ping"/>).</summary>
        public static bool PingHoldX => pingHoldX.Value;

        /// <summary>How long one's own marker for a ping lasts, seconds (<see cref="Hands.Ping"/>).</summary>
        public static float PingSeconds => pingSeconds.Value;

        /// <summary>Where a ping's chat line goes: <see cref="PingChatFactionOrGlobal"/>, <see cref="PingChatFactionOnly"/> or <see cref="PingChatOff"/>.</summary>
        public static int PingChat => pingChat.Value;

        public const int PingChatFactionOrGlobal = 0, PingChatFactionOnly = 1, PingChatOff = 2;
        /// <summary>The actions wheel offers the ship model (<see cref="Hands.ShipModel"/>).</summary>
        public static bool ShipModel => shipModel.Value;

        /// <summary>The ship model's thruster overlay. The actions wheel switches it, and the options screen.</summary>
        public static bool ShipModelThrusters
        {
            get => shipModelThrusters.Value;
            set => shipModelThrusters.Set(value);
        }

        /// <summary>The ship model's power overlay.</summary>
        public static bool ShipModelPower
        {
            get => shipModelPower.Value;
            set => shipModelPower.Set(value);
        }

        /// <summary>The ship model's door overlay.</summary>
        public static bool ShipModelDoors
        {
            get => shipModelDoors.Value;
            set => shipModelDoors.Set(value);
        }

        /// <summary>The ship model's weapon outlines.</summary>
        public static bool ShipModelWeapons
        {
            get => shipModelWeapons.Value;
            set => shipModelWeapons.Set(value);
        }

        // ---- The list, for the options screen. ----

        /// <summary>Every setting, in the order they were declared.</summary>
        public static IReadOnlyList<Setting> All => all;

        /// <summary>How many times <see cref="Save"/> has written the file. The options screen uses it to see whether anything saved while it was open.</summary>
        public static int SaveCount { get; private set; }

        /// <summary>The settings file, SpaceEngineersVR.cfg in the game's user data folder (<see cref="UserFiles"/>).</summary>
        public static string FilePath { get; set; } = UserFiles.File(FileName);

        internal const string FileName = "SpaceEngineersVR.cfg";

        private static T Add<T>(T setting) where T : Setting
        {
            if (byKey.ContainsKey(setting.Key))
            {
                // Programming error. Keep the plugin running: the first one declared wins.
                Log.Error($"Setting '{setting.Key}' is declared twice; the second is ignored");
                return setting;
            }
            all.Add(setting);
            byKey.Add(setting.Key, setting);
            return setting;
        }
    }
}
