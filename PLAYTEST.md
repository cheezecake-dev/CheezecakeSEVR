# Open bugs found in playtests

Reproducible, still open. One section per bug: Repro / Expected / Actual / Evidence.
Linux findings are from 2026-10-12 (Ubuntu 26.04, RTX 3060, Quest 3 through WiVRn 26.9, Pulsar for Linux, linux-compat 68cd6e19).

## No game sound in a world without the distribution's OpenAL

Repro:
1. Use a machine without `libopenal.so.1` in the library path (`ldconfig -p | grep openal` is empty; `libopenal1` not installed).
2. Start the game through Pulsar, load any world.

Expected: the game's sound plays, through the headset when WiVRn is connected.
Actual: the log of the game says "Exception during loading audio engine. Game continues, but without sound ... Could not load from any of the possible library names" (Silk.NET.OpenAL looks for `libopenal.so.1` and `libopenal.so` with dlopen; linux-compat's own `libopenal.so` is in its Bin folder, which dlopen does not search). The only sound heard is the intro video, which linux-compat plays itself. 23 of 30 game logs on the test box show the exception.
Evidence: `SpaceEngineers_*.log` in ~/.config/SpaceEngineers (line after "MyAudio.LoadData - START"). With `LD_LIBRARY_PATH` pointing at a folder holding a `libopenal.so` link to linux-compat's copy, the exception is gone and a second PipeWire stream appears. `tools/linux/setup.sh --check` now reports the missing package (working tree); installing it was not tried.

## VR does not start when the headset connects after the game started

Repro: start the WiVRn server without a headset, start the game, then connect the headset.

Expected: VR starts once the headset is there.
Actual: the game waits 15 s for the runtime (`Bootstrap.FindHeadset`, on its own thread, so the game no longer hangs in `xrCreateInstance`), logs that it starts without VR, and runs on the desktop. The instance is created in the background when the headset connects, but its result is dropped; the session is only built at startup, so VR stays off until the next launch. The same holds when the server was not running at launch.
Evidence: SpaceEngineersVR.log: "No OpenXR answer from the runtime after 15 s", then "OpenXR instance created" at the connect, then "Perf ... [no headset; window only]".

## Game sound stays on the silent WiVRn sink while the headset is disconnected

Repro: with the game in a world and sound routed to the headset, stop the WiVRn app on the headset (`adb shell am force-stop org.meumeu.wivrn`).

Expected: (design question) sound returns to the speakers, or the user is told.
Actual: the WiVRn server keeps its `wivrn.sink` node while the server runs, so `HeadsetAudio` does not see a change; the game goes on playing into the sink, which nobody hears, until the headset is back. The OpenXR session goes Focused -> Visible -> Synchronized and the game survives. Reconnecting restores sound with no action.
Evidence: `pw-link -l` shows `Interim.bin:output_FL -> wivrn.sink:playback_FL` after the disconnect; Interim log lines "OpenXR session Focused -> Visible -> Synchronized".

## WiVRn app on the headset stays on "Cannot connect ... Socket shutdown" after a failed attempt

Repro: start the WiVRn server, start the headset's connection (`am start -a android.intent.action.VIEW -d wivrn+tcp://localhost:9757 org.meumeu.wivrn`) before an OpenXR application is running, or do it twice.
Expected: the next attempt connects.
Actual: the app keeps showing the error and never connects until `adb shell am force-stop org.meumeu.wivrn` is run. Seen three times. WiVRn's behaviour, not the plugin's; listed because the setup guide's connection step hits it.
Evidence: headset screenshot testpass/q1.jpg; the server log shows no connection attempt.
