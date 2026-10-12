# Diagnostics

Logging and test seams that are part of the plugin. None of it does anything in normal play; each piece is inert unless
its switch is set.

| File | What it is |
| --- | --- |
| `HeldTrace.cs` | The held tool's trace log (renderdebug `HeldTrace=1`). |

The tools that drive or fake the game (the drive, the fake hands and head) are not part of the plugin and are never
compiled into it.
