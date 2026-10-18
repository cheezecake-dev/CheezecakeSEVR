# Space Engineers VR on native Linux

Space Engineers 1 runs in VR on Linux with no Proton and no Wine. The stack is native Linux Pulsar (Interim flavour,
.NET 10), CometWorks' linux-compat (SDL3 window and input, OpenAL), DXVK Native for Direct3D 11 to Vulkan, and OpenXR
through WiVRn or Monado. This plugin does the VR rendering inside the game's own renderer, so the game is unmodified.
Status: tested on one setup, a Meta Quest 3 through WiVRn over USB, Ubuntu 26.04, RTX 3060. The session reaches Focused, the menu shows in the headset with the laser on it, and the game's sound
reaches the headset. It is early: one machine, one runtime, and not every feature has had a headset pass on Linux.

Every claim below names the file or class it comes from.

## Architecture

On Windows the plugin hands the runtime the game's D3D11 device. A Linux runtime cannot use a D3D11 device, and DXVK's
device is not one the runtime made, so the session is created on DXVK's own Vulkan objects instead, as Proton's
wineopenxr does.

```
 game renderer (D3D11, per-eye draws)
        |  DXVK Native: libdxvk_d3d11 / libdxvk_dxgi
        v
 DXVK VkInstance / VkDevice / queue ---- IDXGIVkInteropDevice1 ---> DxvkInterop.cs
        ^                                                              |
        |  VkImages wrapped as D3D11 textures                          |  GetVulkanHandles, GetSubmissionQueue1,
        |  (CreateTexture2DFromVkImage)                                |  Lock/ReleaseSubmissionQueue, Flush
        |                                                              v
 eye copies into those textures  --- xrEndFrame --->  XR_KHR_vulkan_enable session (XrSession.cs)
                                                              |
                                                       WiVRn / Monado -> headset
```

- **Extension choice** (`XrRuntime.ChooseVulkan`): if the game's DXGI is DXVK, the runtime's `XR_KHR_vulkan_enable` is
  enabled. `_enable2` is only a fallback with a warning, because it expects an instance and device the runtime created,
  which DXVK's are not.
- **Session** (`XrSession.CreateVulkanSession`): `IDXGIVkInteropDevice::GetVulkanHandles` gives DXVK's instance, physical
  device and device, `GetSubmissionQueue1` the queue. The runtime names the physical device it wants for that instance
  (`xrGetVulkanGraphicsDeviceKHR`); if DXVK is not on it, the session throws. `HeadsetGpu` makes the game pick that device
  (matching `IDXGIVkInteropAdapter` physical devices, in place of the Windows LUID match).
- **Swapchains** (`XrSession.CreateVulkanSwapchain`): the runtime's `VkImage`s become D3D11 render-target textures with
  `CreateTexture2DFromVkImage`, so the existing eye copies are unchanged. Mutable format is requested, as wineopenxr does.
  DXVK returns a render target to `COLOR_ATTACHMENT_OPTIMAL` after each use, which is the layout the runtime hands over and
  takes back, so the plugin does no layout transitions. Format pairs (DXGI to VkFormat) are a table in `XrSession.cs`.
- **Queue locking** (`DxvkInterop.LockQueue`): DXVK submits to its queue from its own thread, and the runtime may submit to
  the same queue inside `xrAcquireSwapchainImage`, `xrReleaseSwapchainImage`, `xrBeginFrame` and `xrEndFrame`. Those four
  calls run between `LockSubmissionQueue` and `ReleaseSubmissionQueue`. Swapchain teardown waits for the queue
  (`vkQueueWaitIdle`) because DXVK's textures do not own the runtime's images.

## Problems and fixes

### 1. DXVK Native never enables what an OpenXR runtime needs 
In DXVK 3.0.2 the OpenXR and OpenVR extension providers sit under `#ifdef _WIN32` in `dxvk_instance.cpp`. On Linux, WiVRn
asks for `VK_KHR_external_memory_fd`, `_semaphore_fd` and `_fence_fd` on the device; DXVK never enables them (gdb on
`vkCreateDevice`: 42 device extensions stock, 45 patched, with the three present).
The second half was found by a crash. WiVRn also asks for four instance extensions that are core since Vulkan 1.1
(`VK_KHR_get_physical_device_properties2` and the external memory, semaphore and fence capabilities) but looks up their
KHR-named entry points. The loader returns those only when the extension is enabled on the instance, so they came back
null and were called unchecked: `xrGetVulkanGraphicsDeviceKHR` jumped to address 0 on the render thread (gdb frames in
`libopenxr_wivrn.so`). The patch (`tools/linux/dxvk-native-xr-external-fd.patch`) enables the four instance extensions and,
per adapter that lists them, the three device extensions, all under `#ifndef _WIN32`. See the last section. With stock DXVK
the plugin does not start the session: `XrRuntime.VulkanInstanceProblem` probes the instance for
`vkGetPhysicalDeviceProperties2KHR`, and the log names `tools/linux/setup.sh`.

### 2. DXGI detection through kernel32, and the distro's SDL3
`DxvkInterop.DxgiIsDxvk` read the dxgi module path through kernel32, which throws `DllNotFoundException` on Linux. It
answered "not DXVK" and the plugin stopped at "runtime does not support Direct3D 11". Under `LINUX` it now answers yes
without creating a factory. The factory was the second problem: created that early, it loaded DXVK before linux-compat had
loaded its SDL3, DXVK's window-system code bound the distribution's SDL3 instead, and the game's window was "Invalid
window" to it, so swap-chain creation failed. On Linux DXGI is always linux-compat's DXVK, so nothing is lost.
`vkQueueWaitIdle` is imported from `libvulkan.so.1` there.

### 3. linux-compat also prefixes `RenderMainSprites`
linux-compat patches `MyRender11.RenderMainSprites` with a prefix that draws the GUI to the backbuffer and returns false,
so Harmony skipped ours. The GUI panel was never drawn, the menu never began a headset frame, `xrPollEvent` never ran and
the session stayed `Unknown`. Under `LINUX` our prefix takes `Priority.First` (`EyeRenderer.cs`). Measured: the session
then goes Idle to Focused at once with about 900 of 900 frames per 10 s, and the menu shows in the headset. With the change
reverted the session never reaches READY.

### 4. linux-compat redraws the cursor at the SDL mouse
linux-compat's `CursorRenderRatePatch` moves the cursor sprite to the SDL mouse position on the render thread, after the
laser has placed it. While the laser holds the cursor, a postfix on `SdlGameWindow.TryGetFreshInWindowMousePosition` reports
no fresh position, so the sprite stays where the game queued it (`PanelCursor.cs`). If the type is not found it logs and
carries on.

### 5. OpenXR runtime and loader discovery
With neither `XR_RUNTIME_JSON` nor an `active_runtime.json`, the plugin picks the first installed of WiVRn (Flatpak, then
system), Monado and SteamVR and logs the choice (`XrRuntimeDiscovery.cs`). An explicit choice always wins. The
distribution's OpenXR loader is tried before a copy in the plugin folder; a missing loader names the package to install.
The hub file lists no Linux loader asset.

### 6. Audio through PipeWire
While WiVRn is connected, the game's output stream is moved to WiVRn's sink with `pw-metadata` (`target.object`) and
released when the sink goes away or the game exits (`HeadsetAudio.cs`). Until a headset is found (`HeadsetWatcher.HasHeadset`)
no PipeWire tool runs at all: the thread only waits, and polls every 3 s once there is a headset or a moved stream to
release. SDL's PipeWire stream node carries no process id, so
the stream is matched through its PipeWire client's `application.process.id` in `pw-dump`. Same setting and `RenderDebug`
switch as the Windows MMDevice routing.

### 7. Stall watchdog from /proc
The Windows watchdog reads thread state through kernel32. On Linux it reports a render or main thread that stops making
progress with kernel state, CPU time, process I/O and GC counts from `/proc` (`StallWatchdog.Linux.cs`). CoreCLR cannot sample
another thread's stack, so there is no stack. No false stalls in a menu run.

### 8. Dynamic resolution on a GPU-bound frame

The controller steps the eye resolution down only when the GPU is the bottleneck, judged by GPU time against the render
thread's time in the frame. Under DXVK the render thread blocks on the GPU inside the frame, so its wall time equals the
GPU time and the scale never left 100%. On Linux the thread's CPU time (`CLOCK_THREAD_CPUTIME_ID`, `ThreadCpu.Linux.cs`)
is used instead: it excludes the blocking, about 4 ms against 18 ms of GPU. Checked both ways: with wall time the scale
stays at 100%; with CPU time it steps to 70% after load, and from a steady world once a held 100% is released.

### 9. The headset may connect after the game starts
WiVRn's `xrCreateInstance` blocks until a headset connects. The instance and the headset are looked for on a thread of
their own (`HeadsetWatcher.cs`) for the whole run; startup waits 15 s for its first answer, so the game picks the
headset's GPU when it can, then goes on with the game's own picture. When the headset turns up later the window becomes
the VR mirror, the GUI is laid out again at 1920x1080, and the render thread starts the session and remakes the screen
resources at the headset's eye size. When the runtime loses the session or the instance (the WiVRn server stopped), the game goes back to the window and VR returns with the
headset; the same code runs on Windows. Going back to the game's own picture while the headset is away (asleep or taken off) is in the code but off for now:
on Windows the picture's return to VR reset the GPU's driver. If the game picked another GPU than the headset needs, VR stays off until the
game is restarted with the headset connected.

Measured: started without the headset, the session reached Focused 4 s after the headset app was started. Stopping the
headset app leaves WiVRn's session alive (Focused -> Visible -> Synchronized; 120 frames/s in the window), and it comes
back to Focused about 2 s after the app reconnects. Stopping the WiVRn server is a loss (`XR_ERROR_INSTANCE_LOST`):
the game went on at 120 frames/s in the window and VR came back after the server restart and reconnect, four times in
one run, with VRAM (2.55 GB), file descriptors (518-522) and threads (124-125) flat.

### 10. Where the plugin coordinates with linux-compat
Two workarounds in this plugin exist because of linux-compat patches. They are suggestions for CometWorks only where a hook
in linux-compat would be cleaner than ours.

| linux-compat patch | What it does | What the plugin does |
|---|---|---|
| `RenderMainSpritesPatch` (prefix, returns false) | Draws the GUI to the backbuffer | We run first at `Priority.First` and draw the GUI to a headset panel. A hook or a skip condition ("headset panel active") would avoid ordering by priority. |
| `CursorRenderRatePatch` | Redraws the cursor sprite at the SDL mouse each render | We postfix `TryGetFreshInWindowMousePosition` to return false while the laser holds the cursor. A flag or event for "cursor position overridden" would replace the postfix. |

## What Windows keeps

The Windows path is unchanged by the Linux work. The mechanism:
- Source differences are under `#if LINUX` with the original line in `#else` (for example `EyeRenderer.cs` priority,
  `DxvkInterop.DxgiIsDxvk`, the `vkQueueWaitIdle` library name). Pulsar defines `LINUX` (with `NETCOREAPP` and `PULSAR`)
  when it compiles a plugin on Linux; the net48 build was checked after each change (0 errors, 0 warnings).
- The Vulkan session path is taken only when the game's DXGI is DXVK, which also happens on Windows with DXVK's DLLs
  (the session path predates the port); without DXVK nothing changes.
- The DXVK patch is entirely inside `#ifndef _WIN32` and the MinGW build compiles with it.
- The hub file lists `Windows,Linux` and `CLR,CoreCLR`; the Windows OpenXR loader asset is `Platforms="Windows"`.
- The beam-over-panels change is shared code and has been checked on Linux/WiVRn only.

## Setup

`tools/linux/setup.sh --no-plugin --install-deps --launcher` with the plugin installed from the hub, or without `--no-plugin`
to register the clone as a development folder (see the Linux section of `README.md`). It finds
Pulsar and the game, installs the distribution's OpenXR loader and OpenAL, builds DXVK Native with the patch and copies it into
linux-compat (keeping the originals; run it again after a linux-compat update), registers the plugin in Pulsar as a
development folder unless `--no-plugin`, checks for WiVRn and `adb`, and can add a launcher entry. `--check` only reports.

WiVRn headset settings used: Extended configuration on, foveated encoding 30% or lower, render resolution 120%, refresh
rate 90, bitrate 200 Mbit/s; for a cable, `adb reverse tcp:9757 tcp:9757`, the USB option in the WiVRn dashboard and the
headset's Wi-Fi off. WiVRn's defaults are tuned for Wi-Fi; a cable has the bandwidth for the higher bitrate and resolution.

## Measured

RTX 3060, Ubuntu 26.04, WiVRn 26.9, Quest 3 over USB, the game and WiVRn's encoder on the same GPU, 90 Hz:
- Main menu: about 900 of 900 frames per 10 s, frame cycle 11.1 ms.
- In a world (Star System start, standing still): dynamic resolution settles at 70% of the runtime's eye size
  (1736x1816 of 2476x2594), 94-98% of frames submitted, GPU 10.2-10.6 ms, render thread CPU about 3 ms. Held at
  100% the same spot runs GPU 18 ms and 60% submitted, with GPU 0 at 98% SM; the 70% figure is in line with the
  70-85% the controller chooses on Windows over Link.

## Open items

- The Windows headset run of the beam-over-panels layer is outstanding.
- Restoring the audio route when the headset disconnects: with the WiVRn server stopped the sink goes and the sound
  returns to the default output, and moves back when the headset reconnects; with only the headset app stopped the sink
  stays and the sound goes on into it.
- Only WiVRn has been run in the headset. Monado and SteamVR for Linux are untested.

## For CometWorks

The DXVK change is one patch against DXVK v3.0.2, written to apply on top of the three linux-dependencies patches, in
`Patches/dxvk` format (`git am`-able, prepared separately from `tools/linux/dxvk-native-xr-external-fd.patch`).
- `dxvk_platform_exts.cpp`: the Platform WSI instance-extension list gains the four instance extensions; the device
  extension provider returns, per adapter, the three `_fd` extensions that adapter supports (probed with
  `vkEnumerateDeviceExtensionProperties`).
- Both are `#ifndef _WIN32`. Programs that do not use OpenXR see an enabled but unused extension; an adapter without the
  `_fd` extensions gets none.
- Tested: gdb extension counts above; a session on DXVK's device with WiVRn 26.9 (stock crashes at
  `xrGetVulkanGraphicsDeviceKHR`, patched does not); native and MinGW builds compile.
- Before it is sent, re-check that it applies to the DXVK version linux-compat currently builds.
