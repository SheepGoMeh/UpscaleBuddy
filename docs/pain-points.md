# Pain points and proposed fixes

Goal: the same behaviour with less overhead.

Reviewed at `bbeb90a` (upstream `main`). Line numbers refer to that commit.

P1, P2, P3, P5, P6 and P7 are applied on `dev/housekeeping`, on top of `bbeb90a`. P4 needs measurement first. P8, P9 and P10 need more information.

The costs below come from reading the code. The applied fixes were then measured in game for one configuration (see [Measured results](#measured-results)). The review covered the per-frame paths, not every file. The local Dalamud logs have no `HITCH` line that names UpscaleBuddy, so there is no evidence that any of this causes visible stutter. Dalamud's hitch check does not time hook detours, so the logs cannot show the per-draw or render-thread costs either way. To measure a change, compare with the commit that adds the Debug logging, which is `bbeb90a` plus the probes (see [How to measure](#how-to-measure)).

## Summary

Ordered by suggested priority. "Gain" is an estimate.

| ID | Pain point | Path | Gain | Effort | Risk | Status |
|----|------------|------|------|--------|------|--------|
| P1 | A string is allocated for each NGX parameter call | Shim (game set to FSR) | Medium | Small | Low | Applied |
| P2 | Closures and delegates are allocated each frame | Built-in FSR 3.1, AMD FSR and XeSS on D3D12, frame generation | Medium | Medium | Low | Applied |
| P3 | Status strings are rebuilt each frame | Supersampling, frame generation | Small | Small | Low | Applied |
| P4 | Per-draw hooks stay enabled while their feature is off | All | Unknown, possibly the largest | Medium | Medium | Needs measurement |
| P5 | FSR 3.1 resolves bindings by string each pass | Built-in FSR 3.1 | Small | Medium | Low | Applied |
| P6 | Constant buffers are uploaded when unchanged | Effect mask, FSR 3.1 | Small | Small | Low | Applied |
| P7 | Texture descriptions are read when not needed | Frame generation | Very small | Small | Low | Applied |
| P8 | D3D12 descriptors are recreated each frame | Async frame generation | Small | Small | Low | Needs information |
| P9 | The effect mask is cleared while the mask is off | All upscalers | Small (GPU) | Small | Medium | Needs information |
| P10 | NGX key lookups take a global lock | Native DLSS (NVIDIA only) | Small | Small | Low | Needs information |

## Measured results

One run of each build on 2026-11-28, in game under Wine, with AMD FSR 4.1.1, Native AA and FSR 4.0.1 async frame generation. Both were Debug builds with `-p:Optimize=true`. "Before" is `bbeb90a` with the probes, "after" has the six applied fixes. Each figure is the mean of three 10 s windows at about 90 fps.

| Measure | Before | After | Reading |
|---------|--------|-------|---------|
| Bytes allocated per frame on the measured paths | 4,632 | 0 | Lower |
| NGX parameter calls, time per frame (56 calls) | 13.7 µs | 10.7 µs | Slightly lower |
| Frame generation submit | 12.3 µs | 11.0 µs | Slightly lower |
| Upscale | 98.0 µs | 95.5 µs | Within the variation between windows |
| Frame generation worker | 251 µs | 255 µs | Within the variation between windows |
| Frames per second | 89.4 | 90.0 | No real difference |
| Gen0 collections per 10 s, whole process | 26.0 | 24.7 | Slightly lower |

- The 4,632 bytes were: NGX parameter calls 3,688, worker 544, upscale 248, submit 152.
- The time saved is a few microseconds of an 11.1 ms frame. No change in speed is expected to be visible.
- Both runs are assumed to show the same scene. The log can't confirm that.
- Built-in FSR 3.1, synchronous frame generation and supersampling were not measured. P5 and the FSR 3.1 part of P6 are not confirmed in game.

## Runtime overhead

### P1. A string is allocated for each NGX parameter call

**Applied.**

- **Where:** `Game/NgxParameters.cs:100-113`
- **Cost before:** The game reads and writes parameters through the fake vtable. Each call ran `Marshal.PtrToStringAnsi`, which allocates a new string. The getters did this inside `storeLock`. The number of calls per frame is not measured.
- **Change:** The name is widened into a 128 character stack buffer, outside the lock, and looked up with `Dictionary.GetAlternateLookup<ReadOnlySpan<char>>()`. A string is made only for a new key. Names that are not ASCII, or longer than the buffer, still go through `PtrToStringAnsi`.
- **Behaviour:** The old and new classes returned identical results for the same calls through the vtable, including empty, long and non-ASCII names. One difference: a null name pointer used to throw, and now reads as an empty name.
- **Measured:** 200,000 calls on an existing 36 character key allocated 96 bytes per call before and none after (.NET 10 on Linux, not in game).

### P2. Closures and delegates are allocated each frame

**Applied.**

- **Where:**
  - `D3D12/D3D12Upscaler.cs:108-109`: the `Bridge.Execute` lambda captured the backend, five inputs and a copy of `DispatchParams`.
  - `Ffx/FfxFrameGenerator.cs:182`: the lambda captured `FrameInputs` and the resources.
  - `FrameGeneration/AsyncGenerator.cs:177`: the worker lambda captured the job's data.
  - `Fsr3/Fsr3Upscaler.cs:327-388`: the local functions `Srv` and `Uav` captured locals. Each of the 7 or 8 `Run` calls converted both to new delegates.
- **Cost before:** Several short-lived heap objects per frame. The built-in FSR 3.1 path made the most.
- **Change:** `Bridge.Execute` and `SubmitAsync` are generic: they take the caller's state by reference and pass it to a `static` lambda. Each caller defines its own `RecordState` struct. The compiler rejects a capture in such a lambda. P5 removed the FSR 3.1 delegates.
- **Behaviour:** The recorded commands, their order and the exception handling are unchanged. `slot.Failed` is still set inside the callback, before `SubmitAsync` signals the output.

### P3. Status strings are rebuilt each frame

**Applied.**

- **Where:** `Game/DlssPath.cs:931`, `Game/FrameGenerationRunner.cs:116` and `:169`
- **Cost before:** One interpolated string per frame for each active line, although the text rarely changes.
- **Change:** The frame generation texts are built when the generator is created. The supersampling text is built when the output size changes.
- **Behaviour:** `Status` holds the same text at the same times. It is still assigned on every frame, because other code paths overwrite it.

### P4. Per-draw hooks stay enabled while their feature is off

**Not applied.** Needs measurement.

- **Where:** `Game/UiLayer.cs:156-157` (detours at `:376` and `:393`), `Game/EffectMask.cs:105` (detour at `:207`)
- **Cost:** `SetBlendState`, `SetTarget` and `SetRenderTargets` run through managed detours very often, `SetBlendState` for every draw. This happens even with frame generation, ReShade-under-UI and the effect mask all off. The detour bodies are cheap. The cost is the native-to-managed transition.
- **Fix:** Enable each hook only while its feature needs it. `DlssPath.EnableHooks` already does this.
  - Enable before the first frame that queues a redirect.
  - Disable only after a few frames without use, so that queued render commands still find the hook. `DlssPath` uses `ReleaseFrames` for the same reason.
  - Leave the other three `EffectMask` hooks enabled. They run rarely, and they restore the game's depth of field state.
- **Why behaviour stays the same:** While a feature is off, its detour only forwards to the original. The risk is the transition: a wrong order gives one or more frames without the UI redirect. Test toggling each setting, plus zone changes and cutscenes.

### P5. FSR 3.1 resolves bindings by string each pass

**Applied.**

- **Where:** `Fsr3/Fsr3Upscaler.cs:327-373` and `:457-480`
- **Cost before:** Each frame, every pass looked up `passes[name]`, switched on each binding kind and name, and called `StartsWith` for the SPD mips.
- **Change:** `LoadPass` turns each binding into a kind and an index. Each frame, `Dispatch` fills a table of views (which includes the ping-pong textures), and `Run` binds by index. The passes are fields instead of a dictionary.
- **Behaviour:** A script compared every old binding name with its new view, and all match. One difference on the failure path: an unknown binding name now fails when the upscaler is created, not at the first dispatch.

### P6. Constant buffers are uploaded when unchanged

**Applied.**

- **Where:** `Game/EffectMask.cs:285`, `Fsr3/Fsr3Upscaler.cs:322-323`
- **Cost before:** A `Map` and `Unmap` per buffer per frame. The effect mask scale changes only when the user moves the slider. The SPD and RCAS constants change only with the render size and the sharpness.
- **Change:** `Dx.UploadIfChanged` compares the bytes with the last upload and skips the map when they are equal. The main FSR 3.1 constants change every frame and are still uploaded every frame.
- **Behaviour:** On the immediate context, a D3D11 dynamic buffer keeps its content until the next map. A deferred context still uploads every time.

### P7. Texture descriptions are read when not needed

**Applied.**

- **Where:** `FrameGeneration/AsyncGenerator.cs:97-99`, `Ffx/FfxFrameGenerator.cs:163-164`
- **Cost before:** Two or three `GetDesc` calls per frame. The results were used only when a twin texture was first created.
- **Change:** The new `Bridge.CreateTwinLike` reads the description, and it runs only when a twin is created.
- **Behaviour:** Twins are created once with the same size and format.

### P8. D3D12 descriptors are recreated each frame

**Not applied.** Needs more information.

- **Where:** `D3D12/FinishPass.cs:122-164`
- **Cost:** Four shader resource views, one unordered access view and several `GetDesc` and heap-start calls for each generated frame, on the worker thread.
- **Fix:** Cache the heap start handles. For each slot, remember the resource pointers and the output size, and write descriptors only when a pointer changes.
- **Why behaviour stays the same:** The resources live as long as the pass. Confirm this for the colour filter LUTs before caching, because a reused address would leave a stale descriptor.

### P9. The effect mask is cleared while the mask is off

**Not applied.** Needs more information.

- **Where:** `Game/EffectMask.cs:251-280`, clear at `:272`
- **Cost:** After the mask has been created, `Resolve` clears it on each upscaled frame, even when responsiveness is 0 and nothing draws into it.
- **Fix:** After the mask turns off, keep clearing for a few frames, then stop. The grace period covers draws that were queued while the mask was on.
- **Why behaviour stays the same:** The mask stays clean, so the first frame after reactivation converts the same content. Without the grace period it could hold stale coverage.

### P10. NGX key lookups take a global lock

**Not applied.** Needs more information.

- **Where:** `Game/NgxParameterReader.cs:89-97`, `Ngx/NgxParameter.cs:87-95` (the same cache twice; the second serves only `DlssPresets`, at feature creation)
- **Cost:** Each lookup takes a lock and reads a dictionary.
- **Fix:** Pass keys as `"name\0"u8` literals and pin them directly. This removes both caches.
- **Why behaviour stays the same:** NGX receives the same bytes. `NgxParameterReader` runs only on the native DLSS path, so it does not affect AMD GPUs.

## Check by measurement first

- **Frame generation start.** The first 10 s window after loading shows about 218 MB allocated on the submit path, and a longest frame of 183 ms before the fixes and 188 ms after. The fixes don't change this. The finish calibration and the colour filter LUT loading in `FrameGenerationRunner.FitAsync` are likely causes, not confirmed.
- **`Game/DlssPath.cs:376`** and `:337` call `GameConfig.TryGet` on each framework tick while the mode is not Off: `GameSettingIsFsr` on the shim path, `GameSettingIsDlss` on the native DLSS path. If Dalamud allocates in this call (not checked), cache the value and refresh it from the game config change event.
- **`Game/ScanOut.cs`, `TryReadVBlank`** repeats a kernel call while the display is inside the vblank, up to 4096 times, and the pacer calls it at most once per paced frame. Count the attempts before changing anything, because pacing depends on the timing of this read.

## Considered, not proposed

These would reduce work but can change behaviour.

| Idea | Why not |
|------|---------|
| Remove full-frame `CopyResource` calls in `Game/Presenter.cs` | Each copy keeps a frame valid while another thread presents or the game writes the next one. |
| Shorten the 1 ms spin in `Game/HighResolutionTimer.cs:14` | It changes frame pacing accuracy. |
| Replace the `Sleep(0)` loop in `Presenter.YieldToCarrier` | It orders two Presents. |
| Cache `GetFullscreenState` (`Presenter.cs:122`) | A mode change must take effect at once. |
| Remember which NGX getter type worked for each name | NGX can store a value under a different type later. |
| Remove the `Flush` calls in `D3D12/Bridge.cs` | The fence handoff to D3D12 depends on submission. |
| Replace the sort in `Game/DueTimeLearner.cs` | At most 120 values, no allocation. Not worth the added code. |

## How to measure

1. **CPU time and allocations:** Debug builds write a `PERF` line to `dalamud.log` every 10 s (`PerfLog.cs`). Build with `-c Debug -p:Optimize=true`. The line holds:
   - rendered frames and frame intervals
   - time and allocated bytes per call for the upscale (`EvaluateDetour`), synchronous frame generation, the async submit and the async worker job
   - NGX parameter calls, time and bytes per frame (P1). The time includes the probe's own cost
   - gen0 collections of the whole process, which other plugins affect

   Compare the same scene and settings, and ignore the first line after a load or a settings change. The UI layer hooks, the effect mask hooks, the frame pacer and the presenter are not measured.
2. **Hook cost (P4):** Count detour calls per frame, and compare CPU frame time with the hooks on and off in the same scene.
3. **CPU and GPU time:** Use the plugin's "Show timings" option for the upscaler.
4. **Behaviour:** The repository has no test project. For each change, check in game: every upscaler, supersampling, native AA, frame generation (synchronous and async), ReShade under the UI, resize, alt+tab, zone change and plugin unload.

## Other pain points

These are outside the runtime goal. They cost development time.

| Pain point | Proposed fix |
|------------|--------------|
| `UpscaleBuddy.csproj:25` checks only for `accumulate.cso`. A build succeeds with newer shaders missing and fails at runtime. This happened with `effect_reactive.cso`, and `finish.cso` was also missing when the code was reviewed. | List every expected `.cso` file and fail the build for each missing one. |
| `Shaders/Build-Shaders.ps1` needs `fxc` from the Windows SDK, so shaders cannot be built on Linux with the repository's tools. | Document a Wine route, or keep the shader artifact longer. `release.yml` uploads it only on tags, for one day. |
| `Game/EffectMask.cs:314` stores the device before the shader and buffer exist. One failure there then appears as an unrelated `Map` error (`E_INVALIDARG`). | Set the fields only after all objects exist. This changes only the failure path. |
| The only workflow runs on tags, so a broken build shows up at release time. | Add a build workflow for pushes and pull requests. |
| `Game/DlssPath.cs` has 1135 lines and covers hooks, lifecycle, both DLSS paths and camera sampling. | Split by responsibility in a change that only moves code. |

## Not covered

Crashes inside `d3d11.dll`, including one on DXVK's `dxvk-cs` thread, are in the local Dalamud logs. Their cause is not known, and nothing in this document is meant to fix them.
