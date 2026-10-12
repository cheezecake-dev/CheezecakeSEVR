# Diagnostics

Test and automation tooling. None of it does anything in normal play; each piece is inert unless its control file or
environment variable is present.

| File | What it is |
| --- | --- |
| `Drive.cs` | The drive: runs commands from a file, with no OS keyboard or mouse input and without the window's focus. |
| `DriveShot.cs`, `DriveEyes.cs` | Screenshots for the drive: the whole window, and the two eye images as submitted to the headset. |
| `TextEntryTest.cs` | The drive's `keyboard` command: exercises the on-screen keyboard. |
| `ActionsWheelDrive.cs`, `*.Drive.cs` | The drive's `wheel`, `quickmenu` and `content` commands, and the members of the product classes they use. |
| `VRControls.TestSeams.cs` | Stand-ins that let the controls mapping run without a game. |

## The drive

`%APPDATA%\SpaceEngineers\SpaceEngineersVR.drive` (on Linux, the same name under the per-user application data folder) is
read about ten times a second. With no file the drive does nothing. Each line is one command, optionally after an id
(`#12 control USE`). Lines appended to the file run once each, in order; a file rewritten with other text runs from its top.
Each command logs one line when it ends: `Drive: #12 control USE done (...)` or `... failed (reason)`. Debug switch:
`RenderDebug Drive=0` turns the drive off.

Commands:

- `load <world>`, `quit-to-menu`, `wait <seconds>`
- `control <MyControlsSpace id> [seconds]`, `key <MyKeys>[+<MyKeys>...] [seconds]`, `escape`
- `move x,z[,up] [seconds]`, `look yaw,pitch`
- `gui click x,y`, `gui hover x,y`, `gui press x,y` (hover and hold the button down; a hover or click at the same place
  lets it go there, so the press clicks), `gui release`, `type <text>`
- `shot [both] <path>` (`both`: the whole window, both eyes on the desktop), `shot eyes <path>` (the two eye images as
  submitted to the headset, side by side in one PNG; fails with a reason when no headset session is drawing)
- `recentre` (as the Options button; reports where the eye then is), `calibrate` (height calibration as the Options button
  starts it, then the arm span, with the hands as they are), `head` (where the eye is, and the play position)
- `sit [name]` (into the nearest empty cockpit), `view [name]` (through the nearest working camera block), `turret [name]`
  (control of the nearest turret, searchlight or turret control block)
- `respawn` (presses the respawn screen's button once it is up; die first with `control SUICIDE`), `answer yes|no|ok`
  (the focused message box, by its own button's handler; reports its text)
- `edit text|data|code [name|lines]` (a multi-line text editor, as the game opens it)
- `keyboard [state|press <label>|where <label>]` (the on-screen keyboard)
- `wheel open [left|right] | state | pick <name> | close` (the actions wheel)
- `setting key=value` (a VR setting for this run, not saved), `mouth` (where push to talk's mouth is in tracking space)
- `quickmenu`, `content`: the quick actions panel and a cockpit screen's content (see `Drive.cs`)

Input goes in where the game reads it, after it has read (or, without focus, cleared) the real devices for the frame: the
controls through the same questions the hands answer (`InputPatches`), keys into the game's keyboard state, typed text into
its text input, and the cursor and left button through the laser's path. An unfocused window would also have the game move
the real cursor when a screen with a cursor opens (`MyDX9Gui.SetMouseCursorVisibility`); while the drive is on and the
window has no focus that is dropped (`Drive.KeepsRealMouse`).
