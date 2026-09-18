# SharpEmu Integration — Merged Pending Commits & Improvements

This branch (`improvements/stability-graphics-playability`) is based on `main` @ `dfb7efa` and integrates:

- **6 pending remote branches** that had not landed in `main` (restored `integration/pending-merges` lineage),
- **7 improvement areas** (stability, graphics, audio, network, playability) from the first pass,
- **4 more improvement batches** from a second deep-analysis pass (§5): kernel timing/sync,
  audio/pad formats, 9 Vulkan correctness+perf fixes, stencil-plane backing + HLE polish,
- **a round-3 GUI build-integrity fix + boot smoke-test suite (§7)** after the round-2 Windows
  build regressed to a startup crash — root-caused to a stale XAML-compiled artifact, fixed
  with a fully clean rebuild, and locked in with 13 new headless GUI boot tests,
- **a round-4 game-launch fix (§8)**: the raw FreeBSD `_umtx_op` syscall (NID `04AjkP0jO9U`)
  is now fully implemented — the missing compare-and-wait primitive that had two game
  worker threads busy-looping at 100% CPU (no video, wall-of-warnings), plus unresolved-import
  log throttling, anti-spin pacing, and robust `sce_sys` metadata discovery for game icons.

Full solution builds with **0 errors**; full test suite **1446/1446 passing**
(Libs 1097 · ShaderCompiler 240 · Metal 60 · SourceGenerators 36 · Gui 13).

---

## 1. Merged pending branches

Audited all 79 remote branches (`git cherry` patch-equivalence + `merge-tree` conflict testing).
Most July-era branches already landed in `main` via cherry-picks/squashed PRs. Six branches
carried useful unmerged work and were merged in this order:

| # | Branch | Merge | What it brings |
|---|--------|-------|----------------|
| 1 | `shader-cfg-ssa` | `81d564b` | Scalar CFG, reaching definitions, gen5 branch resolver, dataflow tests (10 commits) |
| 2 | `fix_debugger_frontend` | `befe996` | Debugger frontend API security hardening (2 commits: `5930463`, `f63882c`) |
| 3 | `render-backend-rewrite` | `a6e3156` | Native C Vulkan backend (`sharpemu_gpu_vulkan` DLL), GUI backend selector, Metal/Vulkan/native unification in `GuestGpu.cs`, Linux aligned-mapping retention fix (19 commits, 4 conflicts resolved) |
| 4 | `game-compat-updates` | `8fd1784` | AGC indirect draws, render-target view matching, IME default profile, savedata dialog reinit (5 commits) |
| 5 | `avplayer_improvements` | `f700916` | FFmpeg runtime bump, font glyph/teardown exports, media tests — resolved to main's evolved versions where newer (11 commits) |
| 6 | `gpu-runtime-stalls` | `f8b3707` | AMR host-file LRU cache, bounded guest data pool; queue-stall work already evolved in main (10 commits) |

### Conflict-resolution highlights
- `GuestGpu.cs` — unified vulkan/metal/native backend selector (env `SHARPEMU_GPU_BACKEND`:
  empty/`vulkan` → managed Silk.NET Vulkan (default), `native` → native C backend, `metal` → macOS).
- `MainWindow.axaml` / `.axaml.cs` — kept main's localized `SettingRow`/`LocalizedChoice` pattern;
  dropped a duplicate `StrictToggle`; the new rendering-backend row is fully localized
  (`Options.RenderingBackend.*` + `Launch.NativeBackendMissing` keys added to **all 17 languages**).
  GUI sets `SHARPEMU_GPU_BACKEND=native|vulkan` on launch and warns when the native DLL is missing.
- `AvPlayerExports.cs` / `VulkanVideoPresenter.cs` — took main's newer evolved versions
  (2076-line AvPlayer, evolved presenter).
- `AgcExports.cs` — kept main's coalesced-drain wait monitor; restored main's `madeProgress`
  backoff (the branch's `resumed` variable was out of scope).
- `cde5437` — the branches' own history contained committed conflict markers; resolved throughout
  (AvPlayer/presenter → main's versions; everything else → HEAD's evolved content).
- `6696fa4` — adapted `NativeVulkanGuestGpuBackend` + `NativeGpuInputSource` (written against the
  old `IGuestGpuBackend` seam) to the evolved interface: `BackendName`, work ordering
  (`EnterGuestQueue`/`SubmitOrderedGuestAction`/`WaitForGuestWork` with anti-wedge close semantics),
  image-lifecycle bookkeeping (extent records, content-identity cache, 65,536-entry cap),
  perf counters, `pixelInputCntl`/`baseVertex` parameters; input source now implements
  `IHostWindowInputSource` (was referencing a non-existent `IPosixWindowInputSource`).

### Skipped branches (verified superseded or broken)
- `deadcell_fix` — calls non-existent `ctx.SetReturn`/`TryWriteUInt32` (does not compile); its
  resource-registration + locale work already landed in main.
- `fix_ajm` — already in main via PR #679. `fix_mitigated_child` — PR #96 + #529.
- `sdl-backend` — all pending commits landed in evolved form (pthread mutex alias fix `f36ce40`,
  bink audio clock, ajm stackalloc hoist, guest-image sync toggle).
- `feat/gui-native-vulkan-host` — July architecture superseded by child-process + SDL host window.
- `ffmpeg_bink*`, `avplayer-implements` — main has `FfmpegVideoDecoder` + `HostMovieBridge` and the
  2076-line AvPlayer (branch: 77 lines).
- `gui_*` branches — localization/atrac9/SndPreview work all in main. `fixes-assets` — glfw gone.
- `release/*`, `ci/*`, `docs/*` — infra only.

---

## 2. Implemented improvements

Deep analysis (CPU/memory stability, shader/GPU graphics, HLE playability) produced seven
improvement areas, each implemented, tested, and merged:

### 2.1 Stability — host-memory sandbox (`imp/stability-mem`, `34538af` → merge `7e38b27`)
- **Hole:** `KernelMprotect`/`KernelMtypeprotect` and the POSIX `mprotect` alias (used by
  libcohtml's embedded V8 for JIT page permissions) passed guest `(addr, len)` straight into raw
  host `VirtualProtect`/`mprotect`; a guest could retag *any* host-committed page (Windows: every
  committed page; POSIX: every `HostMemory`-tracked mapping incl. PLT stubs, TLS handler,
  intrinsic thunks, abort stacks). `IGuestAddressSpace.TryProtect` had the same ungated path.
- **Fix:** new `MprotectCore` shared by all three mprotect spellings — the range must be fully
  guest-owned (no-gap coverage by the kernel mapping table or backing-memory region list) before
  any host protection change; partial overlap is rejected outright (never clamped); host-committed
  non-guest pages → `PERMISSION_DENIED`, unmapped → `NOT_FOUND`. New
  `IGuestAddressSpace.IsRangeGuestMapped` region query; `PhysicalVirtualMemory.TryProtect` gated
  the same way. `TryProtectHostRange` routes through the address-space seam.
- **+4 tests** (valid range protect + protection query, real host-alloc rejection with host
  protection verified unchanged, POSIX-alias equivalence, zero/partial-overlap edges).

### 2.2 Stability — diagnostic races + signal path (`imp/stability-diag`, `07664b6` → merge `0bd0e65`)
- **Hole:** the import-diagnostic rings (recent-import trace, distinct-NID history, import-loop
  signature rings, AV trace) live on the shared `DirectExecutionBackend` and were raced by
  concurrent executors (8–12 runners observed); concrete crash mode: count drift past 64 + C#
  negative modulo → `IndexOutOfRangeException` inside the fault dump. The fault path also did
  per-fault environment lookups (a lock + allocation in the hottest error path).
- **Fix:** all rings/counters serialized behind reentrant gates; snapshots for dumps;
  `SHARPEMU_LOG_POSIX_SIGNALS`, `SHARPEMU_DISABLE_GUEST_ALLOCATOR_HOLE_RECOVERY`,
  `SHARPEMU_LOG_LAZY_COMMIT` cached once per process.
- **+1 stress test** (8 and 12 concurrent runners × 100k records: no exceptions, bounded count,
  untorn/ordered/distinct entries, watchdog still functional).

### 2.3 Graphics — full stencil + MSAA plumbing (`imp/graphics-vulkan`, `8dea864` → merge `2635466`)
- **Before:** stencil was decoded nowhere and the Vulkan pipeline hardcoded
  `StencilTestEnable = false`; no MSAA plumbing at all.
- **Now:** AGC decode of GFX10 `DB_DEPTH_CONTROL` enables/funcs/ops + `DB_STENCILREFMASK(_BF)`
  per-face ref/masks → new `GuestStencilState`/`GuestStencilFace` in the GPU seam →
  Vulkan dynamic stencil state (`vkCmdSetStencil{CompareMask,WriteMask,Reference}` both faces,
  pipeline key carries ops-only identity, GCN↔Vulkan stencil ops 1:1) → Metal
  `MTLStencilDescriptor` front/back + `setStencilFrontReferenceValue:backReferenceValue:` +
  `stencilAttachmentPixelFormat: Stencil8`. Depth attachment now also attaches for stencil-only
  draws. MSAA: `CB_COLOR0_ATTRIB` NUM_SAMPLES (log2) decoded → pipeline `RasterizationSamples` /
  `setRasterSampleCount:` plumbed end to end (backing clamped to 1x and documented until
  multisample backings land).
- **+22 tests** (decode 12, Vulkan mapping 7, Metal mapping 3).

### 2.4 Graphics — shadow sampling, atomics, MIMG C-variants (`imp/graphics-shader`, `6e934d0` → merge `e58f263`)
- **Dref/shadow sampling:** `OpImageSampleDrefImplicitLod`/`ExplicitLod` + `OpImageDrefGather`
  with image types declared `Depth=2` for all shadow-compare MIMG variants (reference after
  coordinates, Bias/Lod/Grad operands preserved, scalar ref broadcast, integer-compare in-shader
  fallback).
- **Full FLAT/GLOBAL atomic set:** decoder rows 0x30–0x3D (Swap, CmpSwap, Add, Sub, SMin, SMax,
  UMin, UMax, And, Or, Xor, Inc, Dec; GLC selects return form) + SPIR-V `OpAtomic*`
  (Device scope, AcqRel|UniformMemory) + MSL `atomic_fetch_*_explicit` / `exchange` /
  `compare_exchange_weak` (acquire failure path).
- **2 pre-existing MSL FLAT bugs fixed:** Flat→Global opcode mapping (previously died as
  "unsupported memory opcode FlatLoadDword") and FLAT address rebase `(v[a] - s[b])` instead of
  the raw 64-bit guest pointer.
- **~40 MIMG C-variant opcodes** (IMAGE_SAMPLE_C, _C_O, _C_CL, IMAGE_GATHER4_C, … per gen5 ISA)
  + `ParseImageOpcodeFlags` tokenizer (DCl/BCl/Lz/Cl/C/S/D/B/L/O with grammar-order enforcement —
  fixes misreading `ImageSampleCl` (LOD clamp) as compare and missing LOD on `ImageSampleCL`).
- **+144 tests** (tokenizer 65, FLAT/GLOBAL atomics 28, MSL flat+atomics 17, Dref 17, C-variant
  decode 17).

### 2.5 Audio — AJM MP3 + bed mixing (`imp/audio`, `607f7b7` → merge `286952a`)
- **AJM MP3:** the `BatchJobRun` decode path had no MP3 branch — MP3 jobs (e.g. GTA V Enhanced
  menu music) decoded to silence. Root-cause fix: MP3 branch → `DecodeMp3Scattered` via NLayer
  (persistent decoder, split/non-split input gathering, PCM scattered across output descriptors
  with unwritten room zeroed, whole-input-consumed on no-op jobs so the guest cannot spin),
  MP3-aware run sideband, decoder reset on clear.
- **`sceAudioOut2ContextBedWrite`:** was a no-op stub — now buffers interleaved float bed PCM per
  context and mixes it as the non-additive stereo base at the next `ContextPush`/`Advance` with
  port PCM on top; degenerate payloads clear a stale bed; unreadable pointer → memory-fault errno.
- **+10 tests** (non-silent MP3 samples, fragment/remainder across jobs, scatter+zeroing, garbage
  input, unknown instance, sideband, bed no-ops, bed mixed exactly once at push, zero-length
  clear, memory fault).

### 2.6 Network — 19 socket/DNS exports (`imp/network`, `f313437` → merge `6a8c580`)
- **Before:** no `recv`/`recvfrom`/`sendto`/DNS — networked titles could not exchange data.
- **Now (all NIDs verified against the aerolib catalog — `scripts/ps5_names.txt`, calibrated on
  the existing `send` NID `fZOeZIOEmLw`):** `recv`, `recvfrom`, `sendto`, `connect`, `bind`,
  `listen`, `accept`, `shutdown`, `sceNetSend/Recv/Recvfrom/Sendto/Connect/Shutdown`,
  `sceNetResolverStartNtoa/Aton` (DNS via bounded `Dns.*Async`), plus `fcntl`/`poll`/`select`
  made socket-aware (completing the 19). Sockets are born non-blocking (EAGAIN on idle
  recv/accept, EINPROGRESS on pending connect); MSG_DONTWAIT/MSG_PEEK; live poll/select
  readiness; `fcntl` F_GETFL/F_SETFL ↔ O_NONBLOCK; `SO_ERROR` (0x1007) → live socket error →
  errno; kernel `connect`/`bind` delegation retains the loopback-only egress policy
  (`SHARPEMU_NET_REDIRECT`); received payloads are copied back into guest memory.
- **+7 loopback tests** (TCP round-trip, EAGAIN + errno, poll readiness, O_NONBLOCK round-trip,
  UDP sendto/recvfrom source address, `localhost` DNS resolution, shutdown → peer EOF).

### 2.7 Playability — savedata, trophies, fonts (`imp/playability`, `1e7c6da` → merge `32ff3e7`)
- **16-slot savedata mount table:** replaced the single hardcoded `/savedata0` with
  `/savedata0`–`/savedata15` registered through the kernel guest-path mount table (idempotent
  remount keeps its slot, lowest-free-slot otherwise, `BUSY` on exhaustion, per-slot registration
  with rollback, `ConfigureApplicationInfo` now unregisters kernel mounts — was a leak).
- **Trophies:** new `NpTrophyStore` — persistent JSON at `user/trophies/<titleId>/trophies.json`
  (`SHARPEMU_TROPHY_DIR` override, first-unlock-wins timestamps, corrupt-file fallback) +
  `sceNpTrophyUnlockTrophy`/`sceNpTrophyGetTrophyUnlockState` exports (NIDs catalog-verified).
- **Fonts:** `sceFont*` library open/close, glyph metrics (advance/bearing, ascent/descent/line
  gap), kerning queries, and `/system_resources/fonts` routing to the host font directory
  (read-only marks), merged cleanly alongside the mprotect hardening.
- **+34 tests** (mount table 13, trophy store 11, font library 10).

---

## 3. Verification

| Check | Result |
|-------|--------|
| Full solution build (`SharpEmu.slnx`) | **0 errors** (11 pre-existing catalog/doc warnings) |
| `SharpEmu.Libs.Tests` | **951/951** |
| `SharpEmu.ShaderCompiler.Tests` | **240/240** |
| `SharpEmu.ShaderCompiler.Metal.Tests` | **60/60** |
| `SharpEmu.SourceGenerators.Tests` | **36/36** |
| **Total** | **1287/1287** |

---

## 4. Remaining gaps (documented, deliberate)

- **MSAA backing** — sample counts are fully plumbed but backing attachments are clamped to 1x
  until multisample render-target backings land.
- **Stencil plane** — stencil state is fully plumbed and legal, but the depth backing
  (D32Sfloat / Depth32Float) has no stencil plane, so stencil writes have no storage yet.
- **Metal Dref** — Metal keeps manual in-shader PCF (needs Metal-runtime depth-texture creation
  changes for `sample_compare`/`depth2d`).
- **Trophy callbacks** — unlock/query are stored and exported; NP trophy callbacks are not
  delivered yet; `GetTrophyInfo` layouts beyond unlock state remain stubbed.
- **Font rasterization** — library lifecycle/metrics/kerning/routing implemented; glyph
  rasterization remains a gap.
- **Native Vulkan backend** — the native library still lacks guest-image upload/fill/write,
  base-vertex and partial-write entry points (managed bookkeeping only); texture-identity cache
  assumes the native side retains address-keyed images from draw packets.
- Interpreter-style SPIR-V codegen (PC-dispatcher loop) remains; the merged CFG/SSA infra
  (shader-cfg-ssa) is used by the scalar evaluator but not yet for emission.

Per-agent implementation logs: [`docs/agent-worklogs.md`](docs/agent-worklogs.md).

---

## 5. Round 2 — second deep-analysis pass (this session)

A fresh analysis pass over the restored tree (GPU/Vulkan + HLE/kernel subsystems) surfaced **23 more
concrete findings**; the CRITICAL/HIGH/MEDIUM ones were implemented across four batches, each built,
tested, and merged with **+135 new tests (1422/1422 total)**.

### 5.1 Kernel timing & synchronization (`imp2/kernel-sync`, `693da91` → merge `a015a72`)
All four fix game hangs/deadlocks:
- **libc `clock_gettime` ignored `clockid`** — every call returned UTC wall time; `CLOCK_MONOTONIC`
  callers (libc++ `steady_clock`) saw ~1.7e9 s deltas and NTP-corrected wall time could run backwards.
  Now delegates to the shared `ReadClock` helper used by `sceKernelClockGettime` (id 0 = wall,
  non-zero = process-relative Stopwatch).
- **`sceKernelSyncOnAddressWait` ignored the expected value and timeout** — was a blind 100 ms poll;
  now a true compare-and-wait (1/2/4/8-byte compare against `expectedValue`, immediate OK on mismatch,
  `TIMED_OUT` + remaining-0 writeback on deadline, memory-fault errno on bad pointers, 5 ms host
  fallback slices). Lock-free guest queues no longer crawl.
- **`sceKernelDeleteSema` never woke blocked waiters** — waiters parked forever after a scene-unload
  delete; now flags the state deleted before removal, wakes on the wake key, and all five wait paths
  return `DELETED` (0x8002000D, previously defined but never used).
- **`sceKernelCancelSema` never returned ECANCELED** — waiters needing more units stayed parked
  forever; a cancel epoch now wakes all waiters with `CANCELED` (0x80020055) while keeping the
  semaphore re-usable (post-cancel waits behave normally), and the accounting is decremented
  naturally instead of being zeroed.

### 5.2 Audio formats & pad (`imp2/audio-pad`, `8fc8925` → merge `a015a72`)
- **`sceAudioOutOpen` format table was wrong for everything but stereo** — real PS5 modes:
  0/4=1ch, 1/5=2ch, 2/6=6ch, 3/7=8ch, float = 4..7. The old mapping corrupted 5.1/7.1/mono-float
  ports and mis-sized every buffer. `BufferByteLength`, the classic-AudioOut downmix and both mix
  pipelines now share the corrected geometry (5.1 fold-down mirrors the 7.1 coefficients — Center
  and LFE no longer vanish).
- **`sceAudioOutOutput` was Gen5-only** — NID `QOQtbeDqsT4` is the shared PS4/PS5 NID; Gen4-layout
  titles got NOT_IMPLEMENTED on the most common per-buffer call. Now `Gen4 | Gen5`.
- **`sceAudioOutSetVolume` collapsed per-channel volumes to `max()`** — per-channel `float[8]` gains
  (copy-on-write) now survive to the mix; pan/mute masks work.
- **Six pad exports added** (all NIDs derived + catalog-verified): `scePadReadStateExt` (mirrors
  `scePadReadState` + zeroed 8-byte extension), `scePadIsValidHandle`, `scePadGetIdleCount`,
  `scePadGetCapability`, `scePadResetOrientation`, `scePadGetVersionInfo` — titles using the
  extended read path no longer poll 0x80020002.

### 5.3 Vulkan correctness & performance (`imp2/gpu-vulkan`, `aa6c87f`)
Nine fixes in the presenter:
1. **`DualSrcBlend` was used but never enabled** (VUID-dualSrcBlend-01506 violation — pipeline
   creation fails on strict drivers; MoltenVK lacks it entirely). Now enabled when supported, with
   a documented single-source fallback + one-shot warning.
2. **Depth-to-sample barrier missed early-Z reads** — `EarlyFragmentTests` + `DepthStencilAttachmentRead`
   now included (was validation-error/stale-depth).
3. **Post-pass color transition omitted blend reads** — `ColorAttachmentReadBit` added.
4. **Guest image fill assumed UNORM8** — format-aware `UnpackGuestFillValue` (uint/int raw
   components, f16/f32 bit patterns, correct UNORM denominators, packed formats).
5. **Legitimate zero-descriptor compute dispatches were dropped** — a module that declares no
   image/buffer bindings (pure-ALU compute) is now dispatched (SPIR-V storage-class scan; declared-
   but-address-0 still skipped + logged).
6. **Render-scale reinterpret compared physical vs logical dims** — the format-reinterpretation fast
   path now compares `LogicalWidth/Height` (was a full image recreate + lost pixels at any scale ≠ 1).
7. **Sampler in the texture-cache key duplicated uploads** — `TextureContentIdentity` excludes the
   sampler from equality/hash (content identity); same texture under point AND linear filtering is
   one image + one upload instead of two full device-memory allocations.
8. **Per-draw pipeline-key string building + unbounded digests** — `RenderTargetLayoutKey`/
   `BlendLayoutKey` cached on `TranslatedDrawResources`; `_shaderDigests` bounded (4096 FIFO).
9. **Per-guest-image queue submit for the initial transition** — recorded into the shared batch
   command buffer (no more submit+fence per new image under churn).

### 5.4 Stencil-plane backing + HLE polish (`imp2/stencil-hle`, `8ff1a40` + `48606c8`)
- **Guest depth targets now back with `D32SfloatS8Uint`** where the device supports it (queried once;
   `D32Sfloat` fallback keeps legacy behavior) — the final piece of the stencil pipeline: stencil
   writes finally have storage. All seven format sites audited: attachment views carry
   Depth|Stencil, sample views stay depth-aspect, every layout transition covers both aspects
   (VUID-image-03319), first-use clears zero stencil, explicit depth clears preserve stencil.
- **Timed `sceKernelWaitEqueue` no longer blocks a host thread** — converted to the cooperative
   block-with-deadline pattern (Monitor.Wait loop kept only for non-cooperative callers).
- **SaveData metadata writes are atomic** — `param.json.tmp` + `File.Move(overwrite: true)`; a
  crash mid-write no longer reverts the slot to default metadata.
- **`sceKernelWaitSema` immediate-success path writes 0 remaining time** (matches the blocked path;
  Orbis stores remaining, not original).
- **Two boot-adjacent stubs** (catalog-verified NIDs): `sceSystemServiceGetAppType`,
  `sceScreenShotDisable`.

---

## 6. Round-2 remaining gaps

- **CPU-write texture tracker** (`SHARPEMU_GUEST_IMAGE_CPU_SYNC`) remains opt-in — defaulting it on
  trades CPU for upload correctness; revisit with a per-mip hash probe.
- **Metal stencil plane** — Vulkan has depth+stencil backings; Metal still uses `Depth32Float`
  (documented gap from round 1, unchanged).
- **Metal Dref** — unchanged from round 1.
- `scePadGetCapability`/`GetVersionInfo` write neutral/zeroed data (real query plumbing when the
  shapes are reverse-engineered).
- Round-1 gaps (§4) that round 2 did not retarget: trophy callbacks, font rasterization, native
  Vulkan guest-image upload/fill/write entry points.

---

## 7. Round-3: GUI build-integrity incident, fix, and regression guard

### 7.1 The incident

The round-2 Windows build (`SharpEmu.exe` in `SharpEmu-Windows-x64-Build.zip`) crashed before
showing a window:

```
System.NullReferenceException: Object reference not set to an instance of an object.
   at SharpEmu.GUI.MainWindow.InitializeLocalizedChoiceBoxes()
   at SharpEmu.GUI.MainWindow..ctor()
   at SharpEmu.GUI.App.OnFrameworkInitializationCompleted()
```

The previous (round-1) build booted the GUI fine, so this was a build-to-build regression with
unchanged GUI sources (`git diff b89fb2a..deff771 -- src/SharpEmu.GUI` is empty).

### 7.2 Root cause — stale compiled XAML embedded in the shipped exe

Avalonia compiles `.axaml` to IL (the `Populate` method that builds the control tree and
registers every `x:Name` into the window namescope) via an MSBuild task with its own
incremental up-to-date check, separate from the Roslyn source generator that emits the
`Find("name")` field wiring.

During the round-2 session the repo's working tree was restored from a source archive
(sandbox recovery) whose extracted file timestamps were **older** than the artifacts left in
`artifacts/obj`. The XAML task's up-to-date check therefore skipped recompilation, and the
publish embedded a `SharpEmu.GUI.dll` whose compiled XAML **predated the `RenderingBackendRow`
addition** (from the render-backend merge). Result:

- the name `"RenderingBackendBox"` was never registered in the namescope, so the generated
  `Find` call left the field null, while
- the fresh code-behind dereferenced it in `InitializeLocalizedChoiceBoxes` → the exact NRE
  above, 100% reproducible at every GUI start.

Forensic confirmation: the `SharpEmu.GUI.dll` was extracted out of the shipped single-file
bundle (bundle manifest parsed, entry decompressed) and swapped into a known-good Linux
build — it reproduces the identical startup NRE under Xvfb, while a clean rebuild of the
**identical source tree** boots. Same source, different compiled artifact → build-artifact
staleness, not a source bug.

### 7.3 The fix

- Full `artifacts/` wipe + clean rebuild of the entire solution (0 errors, no new warnings).
- Windows exe republished from the clean state; the embedded `SharpEmu.GUI.dll` was then
  extracted back out of the new `SharpEmu.exe` and verified **byte-identical** to the dll that
  passed the test suite and booted the GUI under Xvfb.
- Verification chain for this delivery: clean build → 1435/1435 tests → GUI boots headless and
  under Xvfb → embedded dll byte-identical → embedded dll boots under Xvfb.

### 7.4 The regression guard — `tests/SharpEmu.Gui.Tests` (13 tests, new)

A new test project runs the real GUI startup under the **Avalonia headless platform**
(`Avalonia.Headless.XUnit` 12.1.0 on xunit.v3):

- `MainWindow_RegistersAllNamedSettingsControls` (12 cases) — constructs `MainWindow` (the
  exact `InitializeComponent` → namescope-registration path that broke) and asserts each named
  settings control (`CpuEngineBox`, `RenderingBackendBox`, … `DefaultProfileBox`) resolves from
  the namescope.
- `MainWindow_BootsAndWiresLocalizedChoiceBoxes` — constructs the window and asserts every
  localized choice combo has a non-empty `ItemsSource` plus the launcher panel/backend row
  exist.

Validated in both directions: against the extracted broken dll **all tests fail with the exact
user-reported NRE**; against the clean rebuild all 13 pass. A stale-XAML artifact can never
again reach a shipped exe without failing CI.

Also pinned `Avalonia.Headless`/`Avalonia.Headless.XUnit` 12.1.0 and `xunit.v3` 3.2.2 in
`Directory.Packages.props`.

---

## 8. Round 4 — `_umtx_op` (NID `04AjkP0jO9U`): the launch blocker

### 8.1 The symptom

Round-3 exe ran, but launching a game produced **nothing but a flood of identical
warnings** (hundreds of thousands of lines), no video output, and a game window that
froze within a second — with two guest worker threads visibly stuck in an import
retry loop past import #190,000:

```
[LOADER][WARN] Import#192533 unresolved: nid=04AjkP0jO9U ret=0x... rdi=0x6006D04F8 rsi=0x2 ...
[LOADER][WARN] Import#191083 unresolved: nid=04AjkP0jO9U ret=0x... rdi=0x6006CFD18 rsi=0x2 ...
```

Two threads (two import counters interleaved, two distinct `rdi` wait objects), the
same return address (one shared runtime helper), `rsi=2`, `rdx=0`, `rcx=0`, `r9=0xFFFFFFFF`.

### 8.2 Root cause

Reverse lookup through the build-time NID catalog (`scripts/aerolib_catalog.py`) maps
`04AjkP0jO9U` → **`_umtx_op`** — the raw FreeBSD umtx syscall multiplexer exposed by the
PS5 C runtime (the PS5 kernel is FreeBSD-derived). `rsi=2` is **`UMTX_OP_WAIT`**: a
compare-and-wait ("sleep until the 64-bit word at `rdi` no longer equals `rdx`=0"),
with `rcx=0` (no timeout = infinite).

Every libthr lock (mutexes, condvars, rwlocks, semaphores) and every lock-free runtime
queue bottoms out in these primitives. The unresolved-import stub returned "not found"
**instantly**, so the callers re-checked the still-unchanged memory word and called again —
a multi-million-call busy loop at 100% CPU on both workers. That busy loop starved every
other guest thread (including the ones that were supposed to change the waited-on values
and issue the matching `UMTX_OP_WAKE`): total freeze, no video, wall-of-warnings. The
import-loop watchdog never fired because it deliberately excludes guest worker threads.

### 8.3 The fix

**`KernelUmtxCompatExports` (new, `SharpEmu.Libs/Kernel`)** — the full FreeBSD 11 umtx
op table (op codes verified against `sys/sys/umtx.h`):

- `UMTX_OP_WAIT` / `WAIT_UINT` / `WAIT_UINT_PRIVATE` — real futex semantics: value already
  differs → `EWOULDBLOCK` (35) immediately; equal → **parks the guest thread** on the
  cooperative-block scheduler (the same machinery as `sceKernelSyncOnAddressWait`), with
  wake-predicate re-checks, resume re-checks, and a bounded self-heal deadline for
  infinite waits. Parked threads consume no CPU, so the value-writing threads finally
  get scheduled.
- `UMTX_OP_WAKE` / `WAKE_PRIVATE` / `NWAKE_PRIVATE` / `MUTEX_WAKE(2)` — wake waiters
  through the **same wake-key space as `sceKernelSyncOnAddress*`**, so an address-wait
  wake releases an umtx waiter and vice versa (matching the PS5 kernel, where both sit
  on the same sleep queues).
- `umutex` ops (`MUTEX_TRYLOCK`/`LOCK`/`UNLOCK`/`WAIT`) — the m_owner state machine in
  guest memory (unowned/tid/contested-bit), gated per-address on the host, lenient on
  unlock; the lock-park's resume handler completes the acquisition.
- `ucond` ops (`CV_WAIT`/`CV_SIGNAL`/`CV_BROADCAST`) — c_seq compare-and-wait plus the
  paired-mutex release/reacquire protocol of `pthread_cond_wait`.
- `urwlock` ops (`RW_RDLOCK`/`RW_WRLOCK`/`RW_UNLOCK`) and `_usem2` ops (`SEM2_WAIT`/
  `SEM2_WAKE`) — the reader-count/write-owner state machine and the count+waiters-bit
  state machine, same park-and-complete shape.
- `SET_CEILING`, `ROBUST_LISTS`, `SHM`, legacy `LOCK`/`UNLOCK`, `NANOSLEEP`, unknown ops
  (logged once, success).

Returns are raw **FreeBSD 11 errnos** (positive), the raw-syscall convention the guest's
libc wrappers expect — no ORBIS_GEN2 translation at this layer.

**DirectExecutionBackend (Core)** — the unresolved-import path is now hardened:

- **Throttled logging**: the full WARN prints on the first unresolved call per NID;
  afterwards only a one-line summary every 10,000th/1,000,000th call. A game can no
  longer produce a wall of identical warnings.
- **Anti-spin pacing**: past 4096 unresolved calls to the same NID, each retry sleeps
  1 ms — an unimplemented import can no longer starve every other guest thread of CPU
  (a busy loop becomes a ~1 kHz poll). Legitimately rare imports never notice.

**GUI** — game-library metadata discovery (`sce_sys/`) now walks the eboot's directory
**and up to three ancestors**, checking both `sce_sys/<file>` and a flat `<file>`:
cover art (`icon0.png`/`pic0.png`), backdrop (`pic0.png`/`pic1.png`) and `param.json`.
Fixes the initials-placeholder ("ZC") regression seen after a game re-extraction with
different folder nesting.

### 8.4 Verification

- 11 new tests (`KernelUmtxCompatExportsTests`): `EWOULDBLOCK`-on-differs for WAIT/WAIT_UINT,
  timespec timeout → `ETIMEDOUT`, WAIT_UINT compares only the low 32 bits, host-path park
  released by another thread's value change + `UMTX_OP_WAKE` (cross-key interop), wake with
  no waiters, cooperative waiter stays parked while the word matches and resumes `OK` when
  it differs, mutex trylock/EBUSY/unlock round trip, mutex lock parks until unlock and the
  resume completes the acquisition, rwlock writer blocks a reader until unlock.
- Full suite: **1446/1446 passing** (Libs 1097 · ShaderCompiler 240 · Metal 60 ·
  SourceGenerators 36 · Gui 13), clean build 0 errors.
- Forensic artifact check on the shipped single-file exe (bundle manifest parsed, embedded
  assemblies extracted): `KernelUmtxCompatExports` + NID `04AjkP0jO9U` + `_umtx_op` present
  in `SharpEmu.Libs.dll`, `FindSceSysFile` in `SharpEmu.GUI.dll`, `_unresolvedImportCallCounts`
  in `SharpEmu.Core.dll` — the round-3 stale-artifact failure mode is ruled out.
