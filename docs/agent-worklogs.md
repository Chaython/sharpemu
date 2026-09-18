# Agent Worklog — imp/audio (AJM MP3 decode + AudioOut2ContextBedWrite)

Reproduction of the "audio (dbec6e7)" improvement from the Task 3-integration
work-log: "AJM MP3 BatchJobRun root-cause fix (missing MP3 branch ->
DecodeMp3Scattered via NLayer); AudioOut2ContextBedWrite implemented
(interleaved float bed PCM mixed at next push); 10 new tests; 883/883".

## Blueprint recovery

- Read `/home/z/my-project/worklog.md` (Task 2 analysis finding + Task
  3-integration audio bullet) and the four reference excerpts in
  `/home/z/my-project/tool-results/` of the previous session's final code.
- The base excerpts (`read_1789499180621`, `read_1789468853818`) proved to be
  byte-identical to this worktree's `AjmExports.cs` / `AudioOut2Exports.cs`
  (minus a spurious trailing blank line from the excerpt capture), confirming
  the final excerpts (`read_1789504069769`, `read_1789504110971`) are the
  complete previous-session final files. The base already carried the earlier
  `fix_ajm` work (AjmMp3Decoder via NLayer, `DecodeMp3` for the legacy
  BatchJobDecode path, `AjmCodecMp3`, `PreferPcm16`); the missing piece was
  exactly the BatchJobRun MP3 branch plus the bed implementation.

## Part 1 — AJM MP3 decode through BatchJobRun (AjmExports.cs)

Applied the previous session's final file verbatim (verified byte-identical):

- `AjmBatchJobClearContext` now also calls `instance.Mp3?.Reset()`.
- `AjmBatchJobRunCore` (sceAjmBatchJobRun / RunSplit / RunBufferRa /
  RunSplitBufferRa) gained the missing MP3 branch routing to
  `DecodeMp3Scattered` — root cause of the MP3 silence: modern titles decode
  exclusively through the flag-driven Run path, so the legacy
  `sceAjmBatchJobDecode` MP3 branch never fired.
- New `DecodeMp3Scattered`: gathers split/non-split input descriptors into the
  contiguous stream NLayer needs, decodes via the persistent
  `AjmMp3Decoder` (bit reservoir + pending-fragment carry across jobs), and
  scatters PCM across the output descriptors, zeroing unwritten output room.
  The "decoded nothing and consumed nothing" job still reports the whole input
  consumed (with the output cleared) so the guest advances instead of spinning.
- `WriteRunSideband` format block is now MP3-aware: channels/sample rate are
  learned from the decoded stream, falling back to the instance channel bound
  until the first frame; MP3 reports S16 or float (never S32).

`AjmMp3Decoder.cs` (no reference excerpt survived, extended to match the final
excerpt's usage): added `StreamChannels` / `StreamSampleRate`, learned from
each frame that actually produces PCM, cleared by `Reset()`.

## Part 2 — sceAudioOut2ContextBedWrite (AudioOut2Exports.cs)

Applied the previous session's final file verbatim (verified byte-identical):

- `ContextState` carries `BedPcmAddress` / `BedByteLength` / `BedChannels` /
  `BedPending` (the bed is consumed exactly once per grain, like a port's PCM
  attribute).
- `AudioOut2ContextBedWrite` implemented: rdi = context handle, rsi = bed
  payload, rdx = payload bytes. Null/zero/misaligned/oversized payloads are
  "write nothing" and clear any stale bed; an unreadable payload pointer
  returns ORBIS_GEN2_ERROR_MEMORY_FAULT. Channels are inferred from the grain
  (length / (grainSamples * sizeof(float))) when that is an exact 1..16 count,
  otherwise the payload is read as stereo and clamped to one grain.
- `TrySubmitContextAudio` (the sceAudioOut2ContextPush/Advance submit path)
  mixes the pending bed first (interleaved float PCM, non-additive base) and
  then adds MAIN/BGM port PCM on top, so a bed-only context still makes noise;
  trace reason renamed no-ports -> no-sources.
- `ResolveContextBackend` refactored onto `TryOpenBackendStream` with a
  `_backendFactoryForTests` seam (mirrors AudioOutExports), plus
  `SetBackendFactoryForTests` and `ResetForTests` for the test suite.

## Tests (10 new, all in tests/SharpEmu.Libs.Tests/Audio)

- `AjmMp3ExportsTests.cs` (AjmState collection): a deterministic 2093-byte
  MP3 payload (five 128 kbps/44.1 kHz joint-stereo frames of a 1 kHz sine
  generated with ffmpeg, plus a 4-byte partial frame header so NLayer's reader
  emits the final frame; expected PCM16 values captured with a scratch harness
  that replicates AjmMp3Decoder byte-for-byte):
  1. `BatchJobRun_DecodesMp3ToNonSilentExpectedSamples` — full decode, expected
     per-frame sample anchors, peak/non-zero floors, stream+mframe sideband.
  2. `BatchJobRun_Mp3FragmentThenRemainderDecodesAcrossJobs` — 300-byte
     fragment reports whole-input-consumed with cleared output; the remainder
     resumes the stream and decodes 3 more non-silent frames.
  3. `BatchJobRunSplit_ScattersMp3PcmAcrossOutputDescriptors` — gathered input
     descriptors, scattered output descriptors, zeroed unwritten tails.
  4. `BatchJobRun_Mp3GarbageInputIsConsumedAndOutputCleared`.
  5. `BatchJobRun_Mp3UnknownInstanceReportsInvalidParameterStatus`.
  6. `BatchJobRun_Mp3SidebandFormatReportsStreamLayoutAndEncoding` — channels
     2, mask 0x3, 44100 Hz, S16 encoding in the format sideband block.
- `AudioOut2BedWriteExportsTests.cs` (new AudioOut2State collection, recording
  `IHostAudioStream` via the new factory seam):
  7. `BedWrite_NullZeroMisalignedAndOversizedPayloadsAreSuccessNoOps`.
  8. `ContextPush_MixesPendingBedIntoStereoOutputExactlyOnce` — one stereo
     grain of (0.25, -0.5) float becomes 1024 bytes of PCM16 (8192, -16384)
     and the bed is consumed exactly once.
  9. `BedWrite_ZeroLengthAfterValidBedClearsPendingBed`.
  10. `BedWrite_UnreadableGuestPointerReportsMemoryFault`.

## Verification

- `dotnet build src/SharpEmu.Libs/SharpEmu.Libs.csproj` — 0 errors (3
  pre-existing SHEM006 catalog warnings).
- `dotnet build tests/SharpEmu.Libs.Tests/SharpEmu.Libs.Tests.csproj` — 0
  errors.
- `dotnet test tests/SharpEmu.Libs.Tests/SharpEmu.Libs.Tests.csproj` —
  **883/883 passed** (873 pre-existing + 10 new), 0 failed, 0 skipped.
- Full solution (`SharpEmu.slnx`) — 0 errors, 0 warnings.

## Notes / deviations

- The toolchain here rejects adjacent C# string-literal concatenation
  (`"a" "b"` is CS1003 with this SDK's Roslyn), so the embedded MP3 hex
  constant is joined with `+` instead.
- NLayer quirk captured by the tests: the reader constructor rejects a stream
  that ends exactly at a frame boundary, so the embedded payload carries a
  4-byte partial frame header suffix (FF FB 90 00) after the fifth frame.
- Known edge inherited verbatim from the reproduced implementation (documented,
  not fixed, to stay faithful to the blueprint): a float-aligned bed payload
  shorter than one stereo grain mixes through `MixPortIntoStereo`, which slices
  a full grain out of a shorter source — such a payload would throw. The tests
  exercise misaligned/oversized/zero payloads (all no-ops) and exact/over-grain
  beds instead.

# Agent Worklog — imp/graphics-shader

Task: graphics-shader — Dref shadow sampling (OpImageSampleDref*/OpImageDrefGather
with Depth=2 image types), full FLAT/GLOBAL atomic set (decoder + SPIR-V + Metal,
2 pre-existing MSL FLAT bugs fixed), ~40 MIMG C-variant opcodes plus the
ParseImageOpcodeFlags tokenizer, 144 new tests (continuation after a previous
agent hit its context deadline mid-work; blueprint = worklog Task 3-integration
bullet 5474647 + Task 2 findings "shadow sampling without Dref" and "missing
FLAT/GLOBAL atomics + MIMG C-variants in decoder").

## Work inherited from the previous agent (recovered from the uncommitted diff)

The previous agent's partial work was functionally complete but uncommitted and
unverified. As recovered from the worktree diff:

1. `src/SharpEmu.ShaderCompiler/Gen5ImageOpcodeFlags.cs` (new) — flag struct
   (Gather/Compare/SignedCompare/Offset/Bias/Lod/Derivatives/LodClamp/LodZero,
   `UsesDepthReference => Compare`) with Equals/GetHashCode.
2. `Gen5ShaderTranslator.cs` —
   - `ParseImageOpcodeFlags` greedy longest-token tokenizer
     (`DCl, BCl, Lz, Cl, C, S, D, B, L, O`) with grammar-order enforcement
     (C before S before modifier before O; no derivative tokens on GATHER4);
     rejects non-sampling names (`ImageLoad`, `ImageAtomicAdd`, …) and
     malformed orders (`ImageSampleOS`, `ImageGather4CD`).
   - `DecodeFlat`: 13 new FLAT/GLOBAL atomic rows 0x30 Swap … 0x3D Dec
     (MUBUF-shared numbering; GLC selects the return form, no separate RTN
     opcode on FLAT/GLOBAL).
   - `DecodeMimg`: 41 new rows — the full IMAGE_SAMPLE modifier grammar
     (0x21 Cl, 0x23 DCl, 0x26 BCl, 0x28-0x2F C-family, 0x31-0x3E O-forms)
     and the IMAGE_GATHER4 grid (0x41-0x54, 0x58-0x5C) minus the previously
     present 0x40/0x47/0x48/0x4E/0x57/0x5F; ~25 of the rows are shadow-compare
     C-variants.
   - Flat `DwordCount`: `GlobalAtomicCmpswap` 2 (VDATA={new,cmp}),
     all other `GlobalAtomic*` 1.
3. `Gen5SpirvTranslator.cs` —
   - `SpirvImageResource` gains a `Depth` field; compare bindings with float
     component kind declare `OpTypeImage` with Depth=2 ("unknown"), so the
     same descriptor can appear both as a plain (Depth=0) and a compare
     (Depth=2) image type in one module.
   - IMAGE_SAMPLE_C*: `OpImageSampleDrefImplicitLod`/`ExplicitLod` with the
     reference operand right after the coordinates and the original image
     operands (Bias/Lod/Grad) preserved; scalar result broadcast to the
     (r,r,r,1) shape via the shared `BroadcastDepthCompareScalar`.
   - IMAGE_GATHER4_C*: `OpImageDrefGather` (Dref in place of the component
     selector, Offset operand kept).
   - Integer-compare fallback keeps the old in-shader `EmitDepthCompareScalar`
     PCF when the binding cannot be a depth image (uint/sint).
   - GLOBAL/FLAT atomic emission generalised from {Add, Umax} to the full 13-op
     set via the existing `TryGetAtomicOp`/`EmitAtomic` helpers (scope=Device,
     semantics=AcquireRelease|UniformMemory 0x48, CMPSWAP unequal-semantics
     downgrade to Acquire), GLC-gated VDATA return.
   - `ImageGatherExtended` capability now derived from the tokenizer's Offset
     flag instead of name-suffix matching.
   - Sample/Gather emission reads all modifier flags from the tokenizer,
     fixing e.g. `ImageSampleCL` (compare+LOD) previously failing the
     `Contains("SampleL")` LOD sniff.
4. `SpirvModuleBuilder.cs` — `TypeImage` depth operand `bool`→`uint`
   (0/1/2), type-cache key includes it; `SpirvFixedShaders.cs` call sites
   updated (`depth: 0u`).
5. `Gen5MslTranslator.cs` —
   - FLAT segment mapped onto the GLOBAL emission path (`"Global" +
     opcode["Flat"..]`) — bug 1: FLAT opcodes used to reach the shared
     emitter untranslated and died with "unsupported memory opcode
     FlatLoadDword".
   - FLAT addresses rebased against the binding's inferred SGPR base
     (`(v[addr] - s[base])`, mirroring the SPIR-V translator's ISub) — bug 2:
     the raw 64-bit guest pointer was previously used directly as the buffer
     offset.
   - `TryEmitAtomic` replacing the 2-opcode (Add/Umax) MSL atomic emitter:
     full 13-op set (`atomic_fetch_*_explicit` add/sub/and/or/xor/min/max,
     `atomic_exchange_explicit`, `atomic_compare_exchange_weak_explicit` with
     the failure path on Acquire; Smin/Smax via `device atomic_int` +
     `as_type`), `memory_order_acq_rel` matching the SPIR-V semantics, GLC
     return form, Inc/Dec as ±1 fetch (documented wrap-clamp approximation).
6. `Gen5MslTranslator.Pixel.cs` — sample/gather emission switched from
   substring sniffing to `ParseImageOpcodeFlags` (so `ImageSampleCl` is a
   LOD-clamp, not a compare); Metal keeps manual PCF select for real compare
   variants (the documented Metal Dref gap — MSL bindings are `texture2d<T>`,
   not `depth2d<float>`, and swapping kinds would require Metal-runtime
   texture-creation changes outside this branch's scope).
7. Tests (all 144 new, in `tests/SharpEmu.ShaderCompiler.Tests/`; the csproj
   gained a `SharpEmu.ShaderCompiler.Metal` project reference so MSL emission
   can be asserted next to the SPIR-V/decoder suites):
   - `Gen5ImageOpcodeFlagsTests` (65) — every SAMPLE/GATHER4 modifier
     combination, signed-compare token ordering, rejections.
   - `Gen5MimgCVariantDecodeTests` (17) — decode of 12 new C-variant opcodes
     + 3 pre-existing ones, control/sources/wiring.
   - `Gen5ShadowSamplingDrefTests` (17) — Dref opcode emission + operand
     shapes, Depth=2 type declaration, plain-vs-compare distinct types,
     integer fallback.
   - `Gen5FlatGlobalAtomicTests` (28) — decode of all 13 atomics on both
     segments (FLAT + GLOBAL word layouts), GLC/return-form, CMPSWAP 2-dword
     VDATA, SPIR-V OpAtomic* for both segments incl. the FLAT ISub rebase.
   - `MslFlatAndAtomicTests` (17) — MSL emission of both FLAT fixes and all
     13 atomics (global + flat paths), tokenizer-driven compare behaviour.

## Completion work (this session)

- Mapped the whole uncommitted diff (git diff + 6 untracked files) against the
  blueprint; cross-checked the FLAT atomic op numbering (0x30-0x3D, 0x34
  reserved in RDNA2), the MIMG opcode grid, and the SPIR-V opcode numbers
  (OpImageSampleDrefImplicitLod=89, ExplicitLod=90, OpImageDrefGather=97).
- Verified the FLAT uint.MaxValue (unresolved-base) path cannot reach MSL/SPIR-V
  emission with a valid binding: the scalar evaluator rejects it with
  `flat-address-base-unresolved` before any binding is recorded.
- Builds: `SharpEmu.ShaderCompiler`, `SharpEmu.ShaderCompiler.Vulkan`,
  `SharpEmu.ShaderCompiler.Metal`, and the full `SharpEmu.slnx` solution —
  **0 errors, 0 warnings**.
- Tests: `SharpEmu.ShaderCompiler.Tests` **240/240** (96 pre-existing + the
  144 new, verified with a targeted filter run: 144/144),
  `SharpEmu.ShaderCompiler.Metal.Tests` **60/60**,
  `SharpEmu.Libs.Tests` **888/888** — all green with no functional changes
  needed; the inherited diff was complete.
- Deviation from the blueprint text, kept deliberately: Metal shadow sampling
  stays on the manual in-shader PCF select (no `sample_compare`/`depth2d`
  plumbing) — this matches the integration state, which documents "Metal
  Dref" as a remaining gap, and the inherited tests encode exactly that
  behaviour. The Metal-side part of the Dref work is the tokenizer-driven
  compare detection (correct C-variant identification, `ImageSampleCl` no
  longer misread as compare) plus the full atomic set.
- Committed everything on `imp/graphics-shader`.

## Stage summary

- Files: 7 modified (Pixel.cs, MslTranslator.cs, Gen5SpirvTranslator.cs,
  SpirvFixedShaders.cs, SpirvModuleBuilder.cs, Gen5ShaderTranslator.cs,
  Tests.csproj) + 6 new (Gen5ImageOpcodeFlags.cs + 5 test files);
  +516/−134 lines in the tracked diff plus ~750 lines of new files.
- ShaderCompiler.Tests 240/240, Metal.Tests 60/60, Libs.Tests 888/888;
  solution build 0 errors / 0 warnings.

---

# Agent Worklog — imp/stability-mem

Task: stability-mem — close the mprotect host-memory sandbox hole (reproduction
of the dd833dc work-log bullet from Task 3-integration, "mprotect host-memory
sandbox hole closed via MprotectCore region validation; 4 new tests").

## Analysis

- Blueprint read from `/home/z/my-project/worklog.md` (Task 2 finding S1 +
  Task 3-integration stability-mem bullet) and the three reference excerpts in
  `/home/z/my-project/tool-results/`. Verified by diff that all three excerpts
  (KernelMemoryCompatExports.cs around lines 3740/60, PhysicalVirtualMemory.cs
  whole file) match the current worktree exactly — they are pre-fix context,
  so the final implementation was reconstructed in the same style.
- The hole: `KernelMprotect` / `KernelMtypeprotect` (and the POSIX `mprotect`
  alias used by libcohtml's embedded V8 for JIT page permissions) took the
  guest's (addr, len) pair, aligned it, and passed it straight to
  `TryProtectHostRange` → raw host `VirtualProtect`/`mprotect`. Guest
  addresses are identity-mapped onto host pages, so a guest could retag ANY
  host-committed page: on Windows every committed page; on POSIX every
  `HostMemory`-tracked mapping — which includes the execution backend's PLT
  stubs, TLS handler, intrinsic thunks, and abort stacks (emulator-critical
  executable memory). Same for `IGuestAddressSpace.TryProtect` in
  `PhysicalVirtualMemory`, which had NO region validation before its raw
  `_hostMemory.Protect`.
- Audited every raw-protect path for guest reachability: the only Libs call
  site of the private `VirtualProtect` wrapper is `TryProtectHostRange`; in
  Core, `SetProtection`/`ApplySegmentProtection` are loader-internal (ELF
  program-header driven, not guest-controlled), and `TryTemporarilyProtect*`
  operate on FindRegion-gated pages — `TryProtect` was the one ungated,
  guest-reachable seam.

## Changes

1. `src/SharpEmu.Libs/Kernel/KernelMemoryCompatExports.cs`
   - New `MprotectCore(ctx, address, length, protection, memoryType = null)`:
     shared body for `KernelMprotect`, `KernelMtypeprotect`, and (through the
     existing alias) `PosixMprotect`. After the existing zero/alignment
     checks it requires the aligned range to be fully guest-owned BEFORE any
     host protection change. Out-of-sandbox ranges return
     `ORBIS_GEN2_ERROR_PERMISSION_DENIED` (EACCES-flavoured) when the pages
     are host-committed but not guest-owned, and `ORBIS_GEN2_ERROR_NOT_FOUND`
     (the ENOMEM-flavoured code the raw path already returned for unmapped
     ranges) otherwise. Partial overlap is rejected outright (all-or-nothing,
     POSIX ENOMEM semantics), documented on the method.
   - `IsMprotectRangeGuestOwned`: no-gap coverage walk over the kernel
     mapping table (`_mappedRegions` — mmap/flexible/direct/HLE-data
     mappings) OR the backing memory's region table (new
     `IGuestAddressSpace.IsRangeGuestMapped` seam, loader segments and
     allocator ranges).
   - `IsHostRangeCommitted`: VirtualQuery walk (reusing `TryQueryHostPage`)
     to pick PERMISSION_DENIED vs NOT_FOUND for rejected ranges.
   - `TryProtectHostRange` now takes the `CpuContext` and routes host
     protection through the address-space seam
     (`KernelVirtualRangeAllocator.TryResolveAddressSpace` →
     `TryProtect` + new `ToGuestPageProtection`), keeping the raw
     `VirtualProtect` only as a fallback for memories without the seam
     (test fakes) — reachable only after MprotectCore validated the range.
   - POSIX `mprotect` doc comment extended to note the shared validation.
2. `src/SharpEmu.HLE/IGuestAddressSpace.cs` — new
   `IsRangeGuestMapped(address, size)` member (region-state query for
   protection exports) + doc on `TryProtect` requiring region validation.
   `PhysicalVirtualMemory` is the only implementer.
3. `src/SharpEmu.Core/Memory/PhysicalVirtualMemory.cs` — implemented
   `IsRangeGuestMapped` (read-locked, no-gap coverage walk over the sorted
   `_regions` list) and hardened `TryProtect` with the same gate so the seam
   is safe for any future caller, not just the kernel exports.
4. `tests/SharpEmu.Libs.Tests/Kernel/KernelMemoryCompatExportsTests.cs` —
   4 new tests (serialized `KernelMemoryCompatStateCollection`, distinct
   48 TB-range fixed mapping bases so static `_mappedRegions` leftovers from
   other tests cannot interfere):
   - `Mprotect_ValidGuestRangeSucceedsAndChangesAccessibility`: real
     `KernelMapNamedFlexibleMemory` fixed mapping over
     `PhysicalVirtualMemory` + recording `IHostMemory` fake; mprotect to
     PROT_READ returns OK, the host protect call is recorded as exactly
     `(base, 16 KiB, ReadOnly)`, and `KernelQueryMemoryProtection` reports
     the first 16 KiB page read-only while the second page keeps RW.
   - `Mprotect_NonGuestHostAddressIsRejectedWithoutTouchingHostProtection`:
     a REAL `HostMemory.Alloc`'d block (emulator-class memory, the same kind
     the backend's stubs live in) is rejected with PERMISSION_DENIED and the
     host protection is verified unchanged via before/after
     `HostMemory.Query` (the pre-fix code would have flipped it).
   - `PosixMprotect_MatchesKernelMprotectBehavior`: valid range → both OK
     with identical host protects; unmapped range → both NOT_FOUND, no extra
     host protects.
   - `Mprotect_ZeroLengthAndPartialOverlapAreRejected`: zero addr/len →
     INVALID_ARGUMENT; range starting inside the mapping but running past its
     end → NOT_FOUND with zero host protect calls (rejected, not clamped);
     interior range that 16 KiB alignment expands to the whole mapping → OK.

## Verification

- `dotnet build src/SharpEmu.Libs/SharpEmu.Libs.csproj`: **0 errors**
  (3 pre-existing SHEM006 catalog warnings only).
- `dotnet test tests/SharpEmu.Libs.Tests/SharpEmu.Libs.Tests.csproj`:
  **877/877 passed** (873 pre-existing + 4 new), 0 failed, 0 skipped.
- Extra safety (not mandated): full solution build `SharpEmu.slnx` 0 errors /
  0 warnings; ShaderCompiler 96/96, Metal 60/60, SourceGenerators 36/36.
- Reverted incidental `src/SharpEmu.CLI/packages.lock.json` churn caused by
  the full-solution restore pulling newer shared-cache package versions
  (unrelated to this change).

## Notes for integration

- `Mprotect_UnmappedRangeReturnsNotFound` (pre-existing) still passes: POSIX
  `HostMemory.Query` reports untracked host memory as free, so unmapped
  rejections keep returning NOT_FOUND exactly as before.
- libc-heap (`Marshal.AllocHGlobal`) blocks are not in either region table;
  on POSIX the old code already failed there (untracked → mprotect fails →
  NOT_FOUND), so behavior is unchanged; on Windows such an mprotect
  previously succeeded and is now denied — the deliberate security trade.

# Agent Worklog — imp/stability-diag

Task: stability-diag — diagnostic ring races + signal-path env caching
(reproduction of the previously-completed 6fdb884 improvement per the Task 3-integration work-log)

## Investigation

- Read the blueprint in `/home/z/my-project/worklog.md`: Task 2 finding
  "shared diagnostic rings raced by concurrent executors; signal-path
  locks/allocations" and the Task 3 bullet "stability-diag (6fdb884):
  import-diagnostic ring races locked (verified 8-12 concurrent runners);
  signal-path env-lookup-per-fault fixed; 873/873".
- Located the shared import-diagnostic rings: they live on the single
  `DirectExecutionBackend` instance shared by all concurrent guest executors
  (`CpuDispatcher._nativeCpuBackend ??= new DirectExecutionBackend(...)`),
  not in SharpEmu.Libs — `LoadProgressDiagnostics` and the `_perfHle*`
  counters in Libs/Core were already Interlocked/Volatile-safe:
  - recent-import trace ring `_recentImportTrace[64]` + count + writeIndex
    (`RecordRecentImportTrace` / `DumpRecentImportTrace`), mutated per import
    dispatch from every executor thread with no synchronization;
  - distinct-NID history ring `_distinctImportNidHistory[128]` + `_lastDistinctImportNid`
    dedup + strlen-burst counters (`TrackDistinctImportNid` /
    `TrackStrlenPrelude` / `GetRecentDistinctImportPrelude`);
  - import-loop signature rings `_importLoopSignatures/_importLoopNidHashes/
    _importLoopReturnRips[2048]` + pattern-hit counters
    (`RecordImportLoopSignature` / `HasRepeatingImportLoopPattern*` /
    `ShouldForceGuestExitOnImportLoop`) — these feed the watchdog that
    force-exits a looping guest, so a torn window could falsely kill a
    healthy one;
  - VEH access-violation dedup fields `_lastAvTrace*` in
    `LogAccessViolationTrace` (fault path).
- Confirmed the "8-12 concurrent runners" context: `DirectExecutionBackend`
  ctor comment "Cover the Astro TBB spawn storm (often 8–12 concurrent
  tbb_thead)".
- Confirmed the concrete pre-fix failure modes: check-then-increment lets
  `_recentImportTraceCount` drift past 64, and the old dump computed
  `(writeIndex - count + len) % len` with a count > len, which in C# yields a
  negative index (`(-1) % 64 == -1`) → `IndexOutOfRangeException` inside the
  fault-dump path → nested fault; plus lost updates / torn entries on all
  rings above.
- Located the per-fault environment lookups (each `GetEnvironmentVariable`
  is a global lock + string allocation in the hottest error path):
  - `SHARPEMU_LOG_POSIX_SIGNALS` read on every POSIX fault in
    `TryHandlePosixFault` (PosixSignals.cs) — the signal path proper;
  - `SHARPEMU_DISABLE_GUEST_ALLOCATOR_HOLE_RECOVERY` read on every access
    violation in `TryRecoverGuestAllocatorHole` (Exceptions.cs, both
    Windows VEH and the POSIX bridge);
  - `SHARPEMU_LOG_LAZY_COMMIT` read on every demand-paged (guard-page)
    fault in `ShouldTraceLazyCommit` (Exceptions.cs).

## Changes (src/SharpEmu.Core/Cpu/Native/)

- `DirectExecutionBackend.cs`: added gates `_recentImportTraceGate`,
  `_importNidHistoryGate`, `_importLoopGuardGate`, `_avTraceGate` (matching
  the existing `_importResultLogSampleGate` lock style); `RecentImportTraceEntry`
  made internal (test visibility via the existing
  `InternalsVisibleTo("SharpEmu.Libs.Tests")`); `TryExecute` ring resets now
  taken under the matching gates.
- `DirectExecutionBackend.Diagnostics.cs`: `RecordRecentImportTrace` mutates
  the ring under `_recentImportTraceGate`; new internal
  `RecentImportTraceCount` + `SnapshotRecentImportTrace()` (oldest-to-newest
  copy, count clamped to capacity so a drifted count can never produce a
  negative index); `DumpRecentImportTrace` now logs the snapshot instead of
  walking the live ring (readers can no longer observe torn entries).
- `DirectExecutionBackend.Imports.cs`: `TrackDistinctImportNid`,
  `TrackStrlenPrelude`, and the distinct-NID walk of
  `GetRecentDistinctImportPrelude` run under `_importNidHistoryGate`
  (export-name resolution moved outside the gate);
  `ShouldForceGuestExitOnImportLoop` runs its whole check under
  `_importLoopGuardGate` (reentrant with `RecordImportLoopSignature`,
  `HasRepeatingImportLoopPattern`, `ResetImportLoopPattern`, which each take
  the gate so they stay safe when called directly). Bounded capacities
  unchanged (64 / 128 / 2048).
- `DirectExecutionBackend.Exceptions.cs`: `LogAccessViolationTrace` dedup
  state serialized under `_avTraceGate`; per-fault env lookups cached once
  per process (`_disableGuestAllocatorHoleRecovery`, `_logLazyCommit`
  static readonly bools, documented "set before launching the emulator").
- `DirectExecutionBackend.PosixSignals.cs`: `SHARPEMU_LOG_POSIX_SIGNALS`
  cached in `_logPosixSignals` (same pattern as the neighbouring
  `_perfSignalCounter`), used in the per-fault trace decision.
- Behavior notes: the crash-dump-only env knobs (SHARPEMU_DUMP_FAULT_STACK_WINDOW,
  SHARPEMU_LOG_DISASM*, SHARPEMU_LOG_REFSCAN_ADDRS, ...) were left per-fault
  because they only run inside the already I/O-heavy unrecovered-fault dump,
  not the hot recovery chain.

## Test

- New `tests/SharpEmu.Libs.Tests/Cpu/ImportDiagnosticsRingStressTests.cs`:
  encodes the "verified 8-12 concurrent runners" observation. Uses the
  Gen5NativeReturnSmokeTests isolated-worker pattern (backend construction
  installs process-wide fault handlers, so the hammer runs in a child
  `dotnet test` process). The hammer runs twice — 8 and 12 concurrent
  runner tasks, 100k import records each — against the exact production
  entry points (`RecordRecentImportTrace`, `TrackDistinctImportNid`,
  `RecordImportLoopSignature`) with a concurrent reader plus writer-side
  snapshot checks asserting: no exceptions, ring count never exceeds
  capacity, every entry field cross-checks against its (runner, seq)
  encoding (torn-entry detection), per-runner append order preserved,
  entries distinct, distinct-NID prelude sane, and the import-loop
  watchdog still detects a strict severe pattern after the concurrent
  hammer (and stays quiet while signatures are diverse).
- Honest caveat: with the gate removed again as an experiment, the race did
  not reproduce on this 2-CPU box within the soak budget (the windows are a
  few instructions wide), so the test is a regression soak/invariant encoder
  rather than a deterministic reproducer; the lock is the fix.

## Verification

- `dotnet build src/SharpEmu.Libs/SharpEmu.Libs.csproj` — 0 errors.
- `dotnet build src/SharpEmu.Core/SharpEmu.Core.csproj` — 0 errors.
- `dotnet test tests/SharpEmu.Libs.Tests/SharpEmu.Libs.Tests.csproj` —
  **874/874 passed** (873 pre-existing + 1 new), 0 failed, 0 skipped;
  stress test re-run 3 extra times for stability (passed each time).
- `dotnet test tests/SharpEmu.SourceGenerators.Tests` (references HLE→Core
  transitively) — 36/36 passed. ShaderCompiler test projects do not
  reference Core.

# Agent Worklog — imp/audio (AJM MP3 decode + AudioOut2ContextBedWrite)

Reproduction of the "audio (dbec6e7)" improvement from the Task 3-integration
work-log: "AJM MP3 BatchJobRun root-cause fix (missing MP3 branch ->
DecodeMp3Scattered via NLayer); AudioOut2ContextBedWrite implemented
(interleaved float bed PCM mixed at next push); 10 new tests; 883/883".

## Blueprint recovery

- Read `/home/z/my-project/worklog.md` (Task 2 analysis finding + Task
  3-integration audio bullet) and the four reference excerpts in
  `/home/z/my-project/tool-results/` of the previous session's final code.
- The base excerpts (`read_1789499180621`, `read_1789468853818`) proved to be
  byte-identical to this worktree's `AjmExports.cs` / `AudioOut2Exports.cs`
  (minus a spurious trailing blank line from the excerpt capture), confirming
  the final excerpts (`read_1789504069769`, `read_1789504110971`) are the
  complete previous-session final files. The base already carried the earlier
  `fix_ajm` work (AjmMp3Decoder via NLayer, `DecodeMp3` for the legacy
  BatchJobDecode path, `AjmCodecMp3`, `PreferPcm16`); the missing piece was
  exactly the BatchJobRun MP3 branch plus the bed implementation.

## Part 1 — AJM MP3 decode through BatchJobRun (AjmExports.cs)

Applied the previous session's final file verbatim (verified byte-identical):

- `AjmBatchJobClearContext` now also calls `instance.Mp3?.Reset()`.
- `AjmBatchJobRunCore` (sceAjmBatchJobRun / RunSplit / RunBufferRa /
  RunSplitBufferRa) gained the missing MP3 branch routing to
  `DecodeMp3Scattered` — root cause of the MP3 silence: modern titles decode
  exclusively through the flag-driven Run path, so the legacy
  `sceAjmBatchJobDecode` MP3 branch never fired.
- New `DecodeMp3Scattered`: gathers split/non-split input descriptors into the
  contiguous stream NLayer needs, decodes via the persistent
  `AjmMp3Decoder` (bit reservoir + pending-fragment carry across jobs), and
  scatters PCM across the output descriptors, zeroing unwritten output room.
  The "decoded nothing and consumed nothing" job still reports the whole input
  consumed (with the output cleared) so the guest advances instead of spinning.
- `WriteRunSideband` format block is now MP3-aware: channels/sample rate are
  learned from the decoded stream, falling back to the instance channel bound
  until the first frame; MP3 reports S16 or float (never S32).

`AjmMp3Decoder.cs` (no reference excerpt survived, extended to match the final
excerpt's usage): added `StreamChannels` / `StreamSampleRate`, learned from
each frame that actually produces PCM, cleared by `Reset()`.

## Part 2 — sceAudioOut2ContextBedWrite (AudioOut2Exports.cs)

Applied the previous session's final file verbatim (verified byte-identical):

- `ContextState` carries `BedPcmAddress` / `BedByteLength` / `BedChannels` /
  `BedPending` (the bed is consumed exactly once per grain, like a port's PCM
  attribute).
- `AudioOut2ContextBedWrite` implemented: rdi = context handle, rsi = bed
  payload, rdx = payload bytes. Null/zero/misaligned/oversized payloads are
  "write nothing" and clear any stale bed; an unreadable payload pointer
  returns ORBIS_GEN2_ERROR_MEMORY_FAULT. Channels are inferred from the grain
  (length / (grainSamples * sizeof(float))) when that is an exact 1..16 count,
  otherwise the payload is read as stereo and clamped to one grain.
- `TrySubmitContextAudio` (the sceAudioOut2ContextPush/Advance submit path)
  mixes the pending bed first (interleaved float PCM, non-additive base) and
  then adds MAIN/BGM port PCM on top, so a bed-only context still makes noise;
  trace reason renamed no-ports -> no-sources.
- `ResolveContextBackend` refactored onto `TryOpenBackendStream` with a
  `_backendFactoryForTests` seam (mirrors AudioOutExports), plus
  `SetBackendFactoryForTests` and `ResetForTests` for the test suite.

## Tests (10 new, all in tests/SharpEmu.Libs.Tests/Audio)

- `AjmMp3ExportsTests.cs` (AjmState collection): a deterministic 2093-byte
  MP3 payload (five 128 kbps/44.1 kHz joint-stereo frames of a 1 kHz sine
  generated with ffmpeg, plus a 4-byte partial frame header so NLayer's reader
  emits the final frame; expected PCM16 values captured with a scratch harness
  that replicates AjmMp3Decoder byte-for-byte):
  1. `BatchJobRun_DecodesMp3ToNonSilentExpectedSamples` — full decode, expected
     per-frame sample anchors, peak/non-zero floors, stream+mframe sideband.
  2. `BatchJobRun_Mp3FragmentThenRemainderDecodesAcrossJobs` — 300-byte
     fragment reports whole-input-consumed with cleared output; the remainder
     resumes the stream and decodes 3 more non-silent frames.
  3. `BatchJobRunSplit_ScattersMp3PcmAcrossOutputDescriptors` — gathered input
     descriptors, scattered output descriptors, zeroed unwritten tails.
  4. `BatchJobRun_Mp3GarbageInputIsConsumedAndOutputCleared`.
  5. `BatchJobRun_Mp3UnknownInstanceReportsInvalidParameterStatus`.
  6. `BatchJobRun_Mp3SidebandFormatReportsStreamLayoutAndEncoding` — channels
     2, mask 0x3, 44100 Hz, S16 encoding in the format sideband block.
- `AudioOut2BedWriteExportsTests.cs` (new AudioOut2State collection, recording
  `IHostAudioStream` via the new factory seam):
  7. `BedWrite_NullZeroMisalignedAndOversizedPayloadsAreSuccessNoOps`.
  8. `ContextPush_MixesPendingBedIntoStereoOutputExactlyOnce` — one stereo
     grain of (0.25, -0.5) float becomes 1024 bytes of PCM16 (8192, -16384)
     and the bed is consumed exactly once.
  9. `BedWrite_ZeroLengthAfterValidBedClearsPendingBed`.
  10. `BedWrite_UnreadableGuestPointerReportsMemoryFault`.

## Verification

- `dotnet build src/SharpEmu.Libs/SharpEmu.Libs.csproj` — 0 errors (3
  pre-existing SHEM006 catalog warnings).
- `dotnet build tests/SharpEmu.Libs.Tests/SharpEmu.Libs.Tests.csproj` — 0
  errors.
- `dotnet test tests/SharpEmu.Libs.Tests/SharpEmu.Libs.Tests.csproj` —
  **883/883 passed** (873 pre-existing + 10 new), 0 failed, 0 skipped.
- Full solution (`SharpEmu.slnx`) — 0 errors, 0 warnings.

## Notes / deviations

- The toolchain here rejects adjacent C# string-literal concatenation
  (`"a" "b"` is CS1003 with this SDK's Roslyn), so the embedded MP3 hex
  constant is joined with `+` instead.
- NLayer quirk captured by the tests: the reader constructor rejects a stream
  that ends exactly at a frame boundary, so the embedded payload carries a
  4-byte partial frame header suffix (FF FB 90 00) after the fifth frame.
- Known edge inherited verbatim from the reproduced implementation (documented,
  not fixed, to stay faithful to the blueprint): a float-aligned bed payload
  shorter than one stereo grain mixes through `MixPortIntoStereo`, which slices
  a full grain out of a shorter source — such a payload would throw. The tests
  exercise misaligned/oversized/zero payloads (all no-ops) and exact/over-grain
  beds instead.


# Agent Worklog — imp/graphics-vulkan

Task: graphics-vulkan — full stencil support end to end + MSAA pipeline sample
plumbing (reproduction of the cff1386 work-log bullet from Task 3-integration:
"full stencil (AGC decode -> GuestStencilState -> Vulkan dynamic stencil state +
Metal MTLStencilDescriptor); MSAA pipeline samples plumbed (backing clamped 1x,
documented); 22 new tests; 895/895", and the Task 2 analysis findings "no
stencil", "hardcoded StencilTestEnable=false at VulkanVideoPresenter.cs:7896",
"no MSAA").

## Analysis

- Read the blueprint in `/home/z/my-project/worklog.md` (Task 2 GPU findings +
  Task 3-integration graphics-vulkan bullet; the follow-up Task 3 notes list
  "MSAA backing" and "stencil plane" as REMAINING gaps after this work — i.e.
  this change plumbs the state completely but keeps the 1x/no-stencil-plane
  backings, documented).
- Located the exact hardcoded `StencilTestEnable = false` (VulkanVideoPresenter.cs
  ~7896 in the pre-change file) inside `CreateTranslatedPipeline`'s
  `PipelineDepthStencilStateCreateInfo`; the pipeline key
  (`GraphicsPipelineKey`) already carried `GuestDepthState`, so stencil joined
  it in the same style.
- Traced the depth-state carrier for the seam design: `DecodeDepthState`
  (AgcExports, DB_DEPTH_CONTROL 0x200) -> `GuestRenderState` ->
  `TranslatedDrawResources.Depth` -> pipeline + per-draw recorders
  (`RecordTranslatedDrawInPass` binds dynamic viewport/scissor/blend-constants
  today). Stencil mirrors that exactly.
- Register layout pinned down from the AGC primary-register-defaults table
  (AgcPrimaryRegisterDefaults.cs, names Kyty-RE'd): DB_DEPTH_CONTROL = 0x200,
  DB_STENCIL_CONTROL = 0x10B, DB_STENCILREFMASK = 0x10C, DB_STENCILREFMASK_BF =
  0x10D, CB_COLOR0_ATTRIB = 0x31D. GFX10 DB_DEPTH_CONTROL carries the stencil
  enables/funcs/ops (STENCIL_ENABLE bit0, BACKFACE_ENABLE bit7, funcs
  [10:8]/[13:11], ops [16:14]..[31:29]); DB_STENCILREFMASK(_BF) carries
  per-face reference [7:0], compare mask [15:8], write mask [23:16]. Op and
  compare encodings match Vulkan StencilOp / MTLStencilOperation and
  MTLCompareFunction 1:1. CB_COLORn_ATTRIB.NUM_SAMPLES [16:14] holds log2 of
  the sample count (0=1x, 1=2x, 2=4x, 3=8x).
- Verified the depth attachment backings: Vulkan `DepthFormat = D32Sfloat`
  (no stencil aspect), Metal `EnsureGuestDepthImage` allocates
  MTLPixelFormat.Depth32Float — so stencil WRITES have no storage today on
  either backend; the state plumbing is complete and becomes effective when a
  stencil-capable backing (D32SfloatS8Uint / Depth32Float_Stencil8) lands.
  Both spots are documented with that limitation.
- Checked the native-Vulkan ABI (NativeGpuPacket/NativeVulkanApi): like depth
  state, it has no stencil/sample-count fields, so the native backend is
  unaffected (managed Vulkan/Metal presenters own this state).

## Part 1 — Full stencil, end to end

- `Gpu/GuestGpuTypes.cs`: new `GuestStencilFace` (CompareFunc/FailOp/
  DepthFailOp/PassOp + Reference/CompareMask/WriteMask), `GuestStencilState`
  (TestEnable, BackfaceEnable, Front, Back) with a `PipelineIdentity`
  projection (`GuestStencilPipelineState`) that carries ONLY the
  pipeline-baked fields (enables/funcs/ops) — reference and compare/write
  masks are deliberately excluded because backends bind them as dynamic
  per-draw state; `GuestRenderState` gains `Stencil` right after `Depth`
  (all construction sites updated).
- `Agc/AgcExports.cs`: new `internal DecodeStencilState(registers)` +
  `DecodeStencilFace(...)` decoding the GFX10 layout above; missing
  DB_STENCILREFMASK registers fall back to ref 0 with full 8-bit masks (the
  hardware-reset 0 would give a compare mask of 0 that can never pass);
  wired into both `CreateRenderState` overloads.
- `VideoOut/VulkanVideoPresenter.cs`:
  - `GraphicsPipelineKey` gains `GuestStencilPipelineState Stencil` (ops
    only — ref/mask churn can never rebuild a pipeline) and `SampleCount`.
  - `TranslatedDrawResources` gains `Stencil` and `SampleCount`; populated
    from the draw's render state / render targets; force-default-raster
    override resets stencil to default like depth.
  - Pipeline creation: `StencilTestEnable = EffectiveStencilTestEnable(...)`
    (guest enable AND a bound depth/stencil attachment — stencil test without
    an attachment is invalid Vulkan), `Front`/`Back` from
    `ToVkStencilOpState(face)` mapping GCN ops 1:1 to Vulkan StencilOp (back
    face mirrors front unless BackfaceEnable). D32Sfloat backing limitation
    documented at the state.
  - Dynamic state array grows 3 -> 6 with StencilCompareMask / StencilWriteMask
    / StencilReference; `RecordTranslatedDrawInPass` now issues
    `vkCmdSetStencilCompareMask/WriteMask/Reference` for both faces after
    the blend constants (back face reusing front values unless separate
    back-face state is enabled).
  - `ShouldAttachGuestDepth` gains an optional stencil parameter: a
    stencil-only draw (depth test+write off) still attaches the DB surface,
    because stencil ops run against the same attachment. The offscreen path
    passes the draw's stencil.
- `Gpu/Metal/MetalVideoPresenter.Draws.cs`:
  - `EncodeRenderState` builds MTLStencilDescriptor front/back
    (`CreateStencilDescriptor(face)`: stencilCompareFunction,
    stencilFailure/depthFailure/depthStencilPassOperation, readMask,
    writeMask), sets them on the MTLDepthStencilDescriptor
    (setFrontFaceStencil:/setBackFaceStencil:) and programs the encoder's
    stencil references via setStencilFrontReferenceValue:backReferenceValue:
    (encoder state, mirroring the Vulkan dynamic reference).
  - `CreateDrawPipeline` sets `setStencilAttachmentPixelFormat:
    MTLPixelFormat.Stencil8` when the guest draw enables stencil and a depth
    attachment is bound (new Stencil8/Depth32FloatStencil8 enum values in
    MetalGuestFormats.cs); Depth32Float backing limitation documented.
  - `TryGetDrawPipeline` mixes the stencil pipeline identity (enables,
    funcs, ops — not ref/masks) into the state hash so stencil pipelines
    cache separately; depth attachment gating now includes stencil-only
    draws (matches the Vulkan presenter).

## Part 2 — MSAA pipeline samples plumbed (backing clamped 1x, documented)

- `Agc/AgcExports.cs`: `RenderTargetDescriptor` + `GuestRenderTarget` gain
  `SampleCount`, decoded from CB_COLORn_ATTRIB.NUM_SAMPLES [16:14] (log2
  encoding, malformed/absent -> 1) in `GetRenderTargets`; the offscreen
  guest-targets list carries it across the seam.
- Vulkan: `CreateTranslatedDrawResources` resolves the draw's sample count
  (`GetGuestRenderTargetSampleCount` = max across bound targets) and clamps
  it via `ClampRenderTargetSamples` -> 1, because every backing the presenter
  allocates (guest color images, transient targets, the D32Sfloat depth
  attachment) is created `SampleCountFlags.Count1Bit`. The pipeline's
  `RasterizationSamples` comes from `ToVkSampleCount(resources.SampleCount)`
  instead of the hardcoded Count1Bit, and the clamped count joins the
  pipeline key. The full decode -> seam -> pipeline path is in place so
  multisample backings only need to stop clamping.
- Metal: same shape — `TryGetDrawPipeline` clamps the resolved guest count
  with its own documented `ClampRenderTargetSamples`, mixes it into the state
  hash, and `CreateDrawPipeline` sets `setRasterSampleCount:` via
  `ToMetalSampleCount`.

## Part 3 — 22 new tests

- `tests/SharpEmu.Libs.Tests/Agc/AgcStencilDecodeTests.cs` (12): decode
  disabled-defaults, enable+backface bits, front func, back func, front ops,
  back ops, front ref/masks, back ref/masks, missing-refmask full-mask
  fallback, render-state derived-copy survival (`with` keeps stencil),
  PipelineIdentity ignoring ref/masks but tracking ops/funcs, and the
  CB_COLORn_ATTRIB.NUM_SAMPLES log2 decode (incl. malformed + unrelated-bits).
- `tests/SharpEmu.Libs.Tests/VideoOut/VulkanStencilStateTests.cs` (7):
  ToVkStencilOp mapping + unknown->Keep, ToVkStencilOpState face mapping with
  ref/masks left dynamic (0), EffectiveStencilTestEnable gating,
  ShouldAttachGuestDepth stencil-only attach, ToVkSampleCount mapping,
  ClampRenderTargetSamples 1x backing clamp, max-across-targets resolution.
- `tests/SharpEmu.Libs.Tests/Gpu/MetalStencilMappingTests.cs` (3):
  ToMetalStencilOperation identity + unknown->Keep, ToMetalSampleCount
  mapping, ClampRenderTargetSamples backing clamp.

## Verification

- `dotnet build src/SharpEmu.Libs/SharpEmu.Libs.csproj` — 0 errors.
- `dotnet build src/SharpEmu.GUI/SharpEmu.GUI.csproj` — 0 errors.
- `dotnet build SharpEmu.slnx` (full solution) — 0 errors, 0 warnings.
- `dotnet test tests/SharpEmu.Libs.Tests/SharpEmu.Libs.Tests.csproj` —
  **910/910 passed** (888 baseline + 22 new), 0 failed, 0 skipped.

## Notes / deviations

- Stencil ops decode from DB_DEPTH_CONTROL (GFX10 hardware layout, stable
  since GFX6) rather than the legacy DB_STENCIL_CONTROL register that also
  appears in the AGC SDK defaults table — on GFX10 the ops live in
  DB_DEPTH_CONTROL and DB_STENCIL_CONTROL stays at its 0 default; the layout
  is documented at the decoder.
- The depth/stencil attachment backings are unchanged (Vulkan D32Sfloat,
  Metal Depth32Float): stencil state is fully plumbed and legal on both
  backends, but stencil writes have no storage until a stencil-plane backing
  lands — matching the integration work-log that still lists "stencil plane"
  and "MSAA backing" as remaining gaps after cff1386.
- The native-Vulkan backend (NativeGpuPacket ABI) has no stencil or
  sample-count fields, like depth state and base vertex; no changes needed
  there.

# Agent Worklog — imp/network (19 socket/DNS exports)

Reproduction of the "network (95c1bae)" improvement from the Task 3-integration
work-log: "19 socket/DNS exports with aerolib-catalog-verified NIDs (calibrated:
existing send NID matches catalog); non-blocking semantics, live poll/select,
fcntl O_NONBLOCK, SO_ERROR->errno, egress policy; 7 loopback tests; 880/880".

## Blueprint recovery

- Read `/home/z/my-project/worklog.md`: Task 2 finding "no recv/sendto in
  NetExports" + the Task 3-integration network bullet.
- Reference material in `/home/z/my-project/tool-results/`:
  - `read_1789504079190_a52dc24175c1.txt` / `read_1789504081945_99e2a4910dce.txt`
    (1660 lines each) proved to be two captures of the previous session's
    complete final `NetExports.cs` (verified identical after stripping the
    line-number prefixes).
  - `read_1789499174388_54187fb68697.txt` / `read_1789499172342_7942cbc07e68.txt`
    (814 lines) are the unchanged base file (byte-identical to this worktree's
    `NetExports.cs` at e58f263).
- The final file adds 16 new `[SysAbiExport]` methods to `NetExports.cs` and,
  per its own "shared with the kernel poll/select/fcntl exports" helper block,
  the remaining 3 of the 19 exports (fcntl/poll/select, pre-existing in
  `KernelFileExtendedExports.cs`) were made socket-aware through those helpers.

## NID verification (scripts/ps5_names.txt + Ps5Nid derivation)

Calibration first, as in the previous session: the pre-existing `send` export's
NID `fZOeZIOEmLw` equals `Ps5Nid.Compute("send")` (SHA1 suffix scheme, the same
algorithm `GenerateAerolibBinaryTask` uses to build `aerolib.bin`), and `send`
is present in `scripts/ps5_names.txt`. Every new export was then checked the
same way — name in the catalog AND declared NID equal to the derived NID:

| Export (ExportName)      | NID           | In ps5_names.txt | NID == derived | Status |
|--------------------------|---------------|------------------|----------------|--------|
| recv                     | Ez8xjo9UF4E   | yes              | yes            | new    |
| recvfrom                 | lUk6wrGXyMw   | yes              | yes            | new    |
| sendto                   | oBr313PppNE   | yes              | yes            | new    |
| connect                  | XVL8So3QJUk   | yes              | yes            | new (NID moved here from the kernel TCP compat backend) |
| bind                     | KuOmgKoqCdY   | yes              | yes            | new (NID moved here from the kernel TCP compat backend) |
| listen                   | pxnCmagrtao   | yes              | yes            | new    |
| accept                   | 3e+4Iv7IJ8U   | yes              | yes            | new    |
| shutdown                 | TUuiYS2kE8s   | yes              | yes            | new    |
| sceNetSend               | beRjXBn-z+o   | yes              | yes            | new    |
| sceNetRecv               | 9wO9XrMsNhc   | yes              | yes            | new    |
| sceNetRecvfrom           | 304ooNZxWDY   | yes              | yes            | new    |
| sceNetSendto             | gvD1greCu0A   | yes              | yes            | new    |
| sceNetConnect            | OXXX4mUk3uk   | yes              | yes            | new    |
| sceNetShutdown           | TSM6whtekok   | yes              | yes            | new    |
| sceNetResolverStartNtoa  | Nd91WaWmG2w   | yes              | yes            | new (DNS) |
| sceNetResolverStartAton  | Apb4YDxKsRI   | yes              | yes            | new (DNS reverse) |
| fcntl                    | 8nY19bKoiZk   | yes              | yes            | pre-existing, now socket-aware |
| poll                     | ku7D4q1Y9PI   | yes              | yes            | pre-existing, now socket-aware |
| select                   | T8fER+tIGgk   | yes              | yes            | pre-existing, now socket-aware |

No NIDs were invented: all 19 names are catalog symbols and every declared NID
matches the catalog derivation (SHEM004 would have failed the build otherwise,
and no new SHEM006 warnings appear — the 3 SHEM006 warnings in the build are the
pre-existing VoiceQoS/Agc ones). `getsockname` (RenI1lL1WFk, in catalog,
NID verified) already existed for the libKernel fd space in
`KernelSocketCompatExports` and was left there; `getpeername`, `gethostbyname`,
`getaddrinfo`/`freeaddrinfo` are catalog symbols but the PS5 libSceNet resolves
through the resolver API implemented here (`sceNetResolverStartNtoa`/`Aton`),
matching the previous session's final code, so they were not added.

## Implementation

### NetExports.cs (applied from the reference, verbatim except one fix)

- 16 new exports (table above): POSIX `recv`/`recvfrom`/`sendto`/`connect`/
  `bind`/`listen`/`accept`/`shutdown` under libKernel plus their libSceNet
  aliases (`sceNetSend`, `sceNetRecv`, `sceNetRecvfrom`, `sceNetSendto`,
  `sceNetConnect`, `sceNetShutdown`), and the DNS resolver pair
  `sceNetResolverStartNtoa`/`sceNetResolverStartAton` (bounded 30s host
  resolve via `Dns.GetHostAddressesAsync`/`GetHostEntryAsync`, `LastError`
  recorded per resolver for `sceNetResolverGetError`).
- Non-blocking semantics: `NetSocket` now creates every host socket
  non-blocking (a blocking host socket would pin the guest thread inside a
  host kernel call); `setsockopt(SO_NBIO)` and `fcntl(F_SETFL)` can opt back
  into blocking; recv paths only wait (Poll(-1)) on sockets the game switched
  back to blocking, MSG_DONTWAIT (0x80) forces the non-blocking path; accepted
  sockets inherit the listener's blocking mode; non-blocking
  recv/accept/connect return `ORBIS_NET_ERROR_EAGAIN` (0x80410123, errno 35) /
  connect returns `EINPROGRESS` (0x80410124, errno 36) so callers finish the
  handshake via writability + SO_ERROR.
- `connect`/`bind` egress policy: descriptors from the libKernel `socket`
  export live in the separate fd space served by the TCP compat backend, so
  `PosixConnect`/`PosixBind` hand unknown fds to
  `KernelSocketCompatExports.Connect/Bind` (which still apply
  SHARPEMU_NET_REDIRECT and the loopback-only outbound policy) instead of
  failing with EBADF. Sockets in the libSceNet table are direct host sockets
  (loopback-capable; no extra gate, matching the previous session's final
  code).
- `getsockopt(SO_ERROR)` (0x1007) now surfaces the live per-socket error from
  `SocketOptionName.Error` instead of a constant 0.
- New helpers: `TranslateSocketFlags` (MSG_PEEK/MSG_DONTROUTE), 
  `WriteSocketAddressResult` (sa_len-first sockaddr_in/in6 writer with
  truncation reporting), and the shared readiness/blocking queries
  (`IsSocketDescriptor`, `TryGetSocketBlocking`, `TrySetSocketBlocking`,
  `ComputeSocketPollEvents`, `TryGetSocketReadiness`, `Poll*` constants).
- `ResolverContext` changed from a record to a mutable class (LastError).
- **Deviation from the reference (bug fix)**: the reference `PosixRecv`/
  `PosixRecvfrom` never copied the received payload back into the guest buffer
  (recv returned the byte count but left the caller's memory untouched). Both
  now `TryWrite` the received span back (memory fault -> EINVAL), which the
  loopback round-trip tests require. This is the only textual deviation from
  the reference file (verified via diff: two inserted 5-line blocks).

### KernelSocketCompatExports.cs

- `Connect` and `Bind` are no longer `[SysAbiExport]`s — `NetExports` now owns
  the `connect`/`bind` NIDs (duplicate NIDs are a build error, SHEM001). The
  methods remain as the internal delegation targets described above; the
  pre-existing `KernelSocketCompatExportsTests` still calls `Connect` directly
  and passes unchanged. `socket`, `getsockname`, `bzero`, `inet_pton`, `htons`
  exports are untouched.

### KernelFileExtendedExports.cs (fcntl / poll / select made socket-aware)

- `fcntl(F_GETFL)`: a libSceNet socket reports `O_NONBLOCK` (0x0004, FreeBSD
  numbering) when non-blocking; regular files still report no flags.
- `fcntl(F_SETFL)`: mirrors the O_NONBLOCK bit onto the managed socket's
  blocking mode via `NetExports.TrySetSocketBlocking`; other bits accepted and
  ignored; non-socket descriptors unchanged.
- `poll`: pollfd entries whose fd is a libSceNet socket now get live revents
  from `NetExports.ComputeSocketPollEvents` (POLLIN/POLLPRI/POLLOUT when
  actually ready, POLLERR on error, POLLNVAL on closed); non-socket descriptors
  keep the previous always-ready behavior.
- `select`: parses the 128-byte fd_sets, keeps only descriptors that are
  actually ready — live `TryGetSocketReadiness` for sockets, always-ready for
  regular files — and returns the ready count (the timeout is never waited
  out, so idle sockets return 0 instead of blocking the guest thread).
  Documented limitation (unchanged from before for regular files): fd_set
  capacity is 1024 descriptors, and libSceNet socket ids start at 0x4001, so
  select's bitmap can only represent socket ids below 1024; poll has no such
  limit and is the readiness path games hit.

## Tests (tests/SharpEmu.Libs.Tests/Network/NetExportsLoopbackTests.cs)

7 new loopback tests (no external network access; every test closes the
descriptors it created):

1. `LoopbackTcpPair_SendRecvRoundTripsThroughAccept` — bind/listen/connect/
   accept on 127.0.0.1 (ephemeral port), send from the client, recv on the
   accepted socket, payload verified byte-for-byte.
2. `NonBlockingRecv_WithoutData_ReturnsWouldBlockAndSetsErrno` — EAGAIN
   (0x80410123) plus errno 35 read back through `sceNetErrnoLoc`.
3. `Poll_ReportsReadinessOnlyWhenDataArrives` — `poll` export returns 0
   revents on the idle accepted socket, POLLIN after the client sends, and 0
   again after the data is drained.
4. `FcntlNonblockFlag_RoundTripsWithSocketBlockingMode` — F_GETFL reports
   O_NONBLOCK on the born-non-blocking socket, F_SETFL(0) clears it (also
   visible via getsockopt(SO_NBIO)), F_SETFL(O_NONBLOCK) re-arms it.
5. `UdpLoopback_SendtoRecvfromReportsSourceAddress` — UDP sendto/recvfrom on
   127.0.0.1 with the sender sockaddr (family/port/address) and in/out length
   verified.
6. `ResolverStartNtoa_ResolvesLocalhostToLoopback` — pool + resolver +
   `sceNetResolverStartNtoa("localhost")` writes 127.0.0.1.
7. `ShutdownWriteSide_MakesPeerRecvReturnZero` — `shutdown(SHUT_WR)` on the
   client makes the peer's recv observe EOF (0).

## Verification

- `dotnet build src/SharpEmu.Libs/SharpEmu.Libs.csproj`: **0 errors** (the
  only new warning is CS0675 in `ComputeSocketPollEvents`, present verbatim in
  the reference final code; SHEM006 count unchanged at the 3 pre-existing
  VoiceQoS/Agc warnings — none of the 19 exports trigger it).
- `dotnet test tests/SharpEmu.Libs.Tests/SharpEmu.Libs.Tests.csproj`:
  **917/917 passed** (baseline 910 + 7 new).

# Agent Worklog — imp/stability-diag

Task: stability-diag — diagnostic ring races + signal-path env caching
(reproduction of the previously-completed 6fdb884 improvement per the Task 3-integration work-log)

## Investigation

- Read the blueprint in `/home/z/my-project/worklog.md`: Task 2 finding
  "shared diagnostic rings raced by concurrent executors; signal-path
  locks/allocations" and the Task 3 bullet "stability-diag (6fdb884):
  import-diagnostic ring races locked (verified 8-12 concurrent runners);
  signal-path env-lookup-per-fault fixed; 873/873".
- Located the shared import-diagnostic rings: they live on the single
  `DirectExecutionBackend` instance shared by all concurrent guest executors
  (`CpuDispatcher._nativeCpuBackend ??= new DirectExecutionBackend(...)`),
  not in SharpEmu.Libs — `LoadProgressDiagnostics` and the `_perfHle*`
  counters in Libs/Core were already Interlocked/Volatile-safe:
  - recent-import trace ring `_recentImportTrace[64]` + count + writeIndex
    (`RecordRecentImportTrace` / `DumpRecentImportTrace`), mutated per import
    dispatch from every executor thread with no synchronization;
  - distinct-NID history ring `_distinctImportNidHistory[128]` + `_lastDistinctImportNid`
    dedup + strlen-burst counters (`TrackDistinctImportNid` /
    `TrackStrlenPrelude` / `GetRecentDistinctImportPrelude`);
  - import-loop signature rings `_importLoopSignatures/_importLoopNidHashes/
    _importLoopReturnRips[2048]` + pattern-hit counters
    (`RecordImportLoopSignature` / `HasRepeatingImportLoopPattern*` /
    `ShouldForceGuestExitOnImportLoop`) — these feed the watchdog that
    force-exits a looping guest, so a torn window could falsely kill a
    healthy one;
  - VEH access-violation dedup fields `_lastAvTrace*` in
    `LogAccessViolationTrace` (fault path).
- Confirmed the "8-12 concurrent runners" context: `DirectExecutionBackend`
  ctor comment "Cover the Astro TBB spawn storm (often 8–12 concurrent
  tbb_thead)".
- Confirmed the concrete pre-fix failure modes: check-then-increment lets
  `_recentImportTraceCount` drift past 64, and the old dump computed
  `(writeIndex - count + len) % len` with a count > len, which in C# yields a
  negative index (`(-1) % 64 == -1`) → `IndexOutOfRangeException` inside the
  fault-dump path → nested fault; plus lost updates / torn entries on all
  rings above.
- Located the per-fault environment lookups (each `GetEnvironmentVariable`
  is a global lock + string allocation in the hottest error path):
  - `SHARPEMU_LOG_POSIX_SIGNALS` read on every POSIX fault in
    `TryHandlePosixFault` (PosixSignals.cs) — the signal path proper;
  - `SHARPEMU_DISABLE_GUEST_ALLOCATOR_HOLE_RECOVERY` read on every access
    violation in `TryRecoverGuestAllocatorHole` (Exceptions.cs, both
    Windows VEH and the POSIX bridge);
  - `SHARPEMU_LOG_LAZY_COMMIT` read on every demand-paged (guard-page)
    fault in `ShouldTraceLazyCommit` (Exceptions.cs).

## Changes (src/SharpEmu.Core/Cpu/Native/)

- `DirectExecutionBackend.cs`: added gates `_recentImportTraceGate`,
  `_importNidHistoryGate`, `_importLoopGuardGate`, `_avTraceGate` (matching
  the existing `_importResultLogSampleGate` lock style); `RecentImportTraceEntry`
  made internal (test visibility via the existing
  `InternalsVisibleTo("SharpEmu.Libs.Tests")`); `TryExecute` ring resets now
  taken under the matching gates.
- `DirectExecutionBackend.Diagnostics.cs`: `RecordRecentImportTrace` mutates
  the ring under `_recentImportTraceGate`; new internal
  `RecentImportTraceCount` + `SnapshotRecentImportTrace()` (oldest-to-newest
  copy, count clamped to capacity so a drifted count can never produce a
  negative index); `DumpRecentImportTrace` now logs the snapshot instead of
  walking the live ring (readers can no longer observe torn entries).
- `DirectExecutionBackend.Imports.cs`: `TrackDistinctImportNid`,
  `TrackStrlenPrelude`, and the distinct-NID walk of
  `GetRecentDistinctImportPrelude` run under `_importNidHistoryGate`
  (export-name resolution moved outside the gate);
  `ShouldForceGuestExitOnImportLoop` runs its whole check under
  `_importLoopGuardGate` (reentrant with `RecordImportLoopSignature`,
  `HasRepeatingImportLoopPattern`, `ResetImportLoopPattern`, which each take
  the gate so they stay safe when called directly). Bounded capacities
  unchanged (64 / 128 / 2048).
- `DirectExecutionBackend.Exceptions.cs`: `LogAccessViolationTrace` dedup
  state serialized under `_avTraceGate`; per-fault env lookups cached once
  per process (`_disableGuestAllocatorHoleRecovery`, `_logLazyCommit`
  static readonly bools, documented "set before launching the emulator").
- `DirectExecutionBackend.PosixSignals.cs`: `SHARPEMU_LOG_POSIX_SIGNALS`
  cached in `_logPosixSignals` (same pattern as the neighbouring
  `_perfSignalCounter`), used in the per-fault trace decision.
- Behavior notes: the crash-dump-only env knobs (SHARPEMU_DUMP_FAULT_STACK_WINDOW,
  SHARPEMU_LOG_DISASM*, SHARPEMU_LOG_REFSCAN_ADDRS, ...) were left per-fault
  because they only run inside the already I/O-heavy unrecovered-fault dump,
  not the hot recovery chain.

## Test

- New `tests/SharpEmu.Libs.Tests/Cpu/ImportDiagnosticsRingStressTests.cs`:
  encodes the "verified 8-12 concurrent runners" observation. Uses the
  Gen5NativeReturnSmokeTests isolated-worker pattern (backend construction
  installs process-wide fault handlers, so the hammer runs in a child
  `dotnet test` process). The hammer runs twice — 8 and 12 concurrent
  runner tasks, 100k import records each — against the exact production
  entry points (`RecordRecentImportTrace`, `TrackDistinctImportNid`,
  `RecordImportLoopSignature`) with a concurrent reader plus writer-side
  snapshot checks asserting: no exceptions, ring count never exceeds
  capacity, every entry field cross-checks against its (runner, seq)
  encoding (torn-entry detection), per-runner append order preserved,
  entries distinct, distinct-NID prelude sane, and the import-loop
  watchdog still detects a strict severe pattern after the concurrent
  hammer (and stays quiet while signatures are diverse).
- Honest caveat: with the gate removed again as an experiment, the race did
  not reproduce on this 2-CPU box within the soak budget (the windows are a
  few instructions wide), so the test is a regression soak/invariant encoder
  rather than a deterministic reproducer; the lock is the fix.

## Verification

- `dotnet build src/SharpEmu.Libs/SharpEmu.Libs.csproj` — 0 errors.
- `dotnet build src/SharpEmu.Core/SharpEmu.Core.csproj` — 0 errors.
- `dotnet test tests/SharpEmu.Libs.Tests/SharpEmu.Libs.Tests.csproj` —
  **874/874 passed** (873 pre-existing + 1 new), 0 failed, 0 skipped;
  stress test re-run 3 extra times for stability (passed each time).
- `dotnet test tests/SharpEmu.SourceGenerators.Tests` (references HLE→Core
  transitively) — 36/36 passed. ShaderCompiler test projects do not
  reference Core.

# Agent Worklog — imp/stability-mem

Task: stability-mem — close the mprotect host-memory sandbox hole (reproduction
of the dd833dc work-log bullet from Task 3-integration, "mprotect host-memory
sandbox hole closed via MprotectCore region validation; 4 new tests").

## Analysis

- Blueprint read from `/home/z/my-project/worklog.md` (Task 2 finding S1 +
  Task 3-integration stability-mem bullet) and the three reference excerpts in
  `/home/z/my-project/tool-results/`. Verified by diff that all three excerpts
  (KernelMemoryCompatExports.cs around lines 3740/60, PhysicalVirtualMemory.cs
  whole file) match the current worktree exactly — they are pre-fix context,
  so the final implementation was reconstructed in the same style.
- The hole: `KernelMprotect` / `KernelMtypeprotect` (and the POSIX `mprotect`
  alias used by libcohtml's embedded V8 for JIT page permissions) took the
  guest's (addr, len) pair, aligned it, and passed it straight to
  `TryProtectHostRange` → raw host `VirtualProtect`/`mprotect`. Guest
  addresses are identity-mapped onto host pages, so a guest could retag ANY
  host-committed page: on Windows every committed page; on POSIX every
  `HostMemory`-tracked mapping — which includes the execution backend's PLT
  stubs, TLS handler, intrinsic thunks, and abort stacks (emulator-critical
  executable memory). Same for `IGuestAddressSpace.TryProtect` in
  `PhysicalVirtualMemory`, which had NO region validation before its raw
  `_hostMemory.Protect`.
- Audited every raw-protect path for guest reachability: the only Libs call
  site of the private `VirtualProtect` wrapper is `TryProtectHostRange`; in
  Core, `SetProtection`/`ApplySegmentProtection` are loader-internal (ELF
  program-header driven, not guest-controlled), and `TryTemporarilyProtect*`
  operate on FindRegion-gated pages — `TryProtect` was the one ungated,
  guest-reachable seam.

## Changes

1. `src/SharpEmu.Libs/Kernel/KernelMemoryCompatExports.cs`
   - New `MprotectCore(ctx, address, length, protection, memoryType = null)`:
     shared body for `KernelMprotect`, `KernelMtypeprotect`, and (through the
     existing alias) `PosixMprotect`. After the existing zero/alignment
     checks it requires the aligned range to be fully guest-owned BEFORE any
     host protection change. Out-of-sandbox ranges return
     `ORBIS_GEN2_ERROR_PERMISSION_DENIED` (EACCES-flavoured) when the pages
     are host-committed but not guest-owned, and `ORBIS_GEN2_ERROR_NOT_FOUND`
     (the ENOMEM-flavoured code the raw path already returned for unmapped
     ranges) otherwise. Partial overlap is rejected outright (all-or-nothing,
     POSIX ENOMEM semantics), documented on the method.
   - `IsMprotectRangeGuestOwned`: no-gap coverage walk over the kernel
     mapping table (`_mappedRegions` — mmap/flexible/direct/HLE-data
     mappings) OR the backing memory's region table (new
     `IGuestAddressSpace.IsRangeGuestMapped` seam, loader segments and
     allocator ranges).
   - `IsHostRangeCommitted`: VirtualQuery walk (reusing `TryQueryHostPage`)
     to pick PERMISSION_DENIED vs NOT_FOUND for rejected ranges.
   - `TryProtectHostRange` now takes the `CpuContext` and routes host
     protection through the address-space seam
     (`KernelVirtualRangeAllocator.TryResolveAddressSpace` →
     `TryProtect` + new `ToGuestPageProtection`), keeping the raw
     `VirtualProtect` only as a fallback for memories without the seam
     (test fakes) — reachable only after MprotectCore validated the range.
   - POSIX `mprotect` doc comment extended to note the shared validation.
2. `src/SharpEmu.HLE/IGuestAddressSpace.cs` — new
   `IsRangeGuestMapped(address, size)` member (region-state query for
   protection exports) + doc on `TryProtect` requiring region validation.
   `PhysicalVirtualMemory` is the only implementer.
3. `src/SharpEmu.Core/Memory/PhysicalVirtualMemory.cs` — implemented
   `IsRangeGuestMapped` (read-locked, no-gap coverage walk over the sorted
   `_regions` list) and hardened `TryProtect` with the same gate so the seam
   is safe for any future caller, not just the kernel exports.
4. `tests/SharpEmu.Libs.Tests/Kernel/KernelMemoryCompatExportsTests.cs` —
   4 new tests (serialized `KernelMemoryCompatStateCollection`, distinct
   48 TB-range fixed mapping bases so static `_mappedRegions` leftovers from
   other tests cannot interfere):
   - `Mprotect_ValidGuestRangeSucceedsAndChangesAccessibility`: real
     `KernelMapNamedFlexibleMemory` fixed mapping over
     `PhysicalVirtualMemory` + recording `IHostMemory` fake; mprotect to
     PROT_READ returns OK, the host protect call is recorded as exactly
     `(base, 16 KiB, ReadOnly)`, and `KernelQueryMemoryProtection` reports
     the first 16 KiB page read-only while the second page keeps RW.
   - `Mprotect_NonGuestHostAddressIsRejectedWithoutTouchingHostProtection`:
     a REAL `HostMemory.Alloc`'d block (emulator-class memory, the same kind
     the backend's stubs live in) is rejected with PERMISSION_DENIED and the
     host protection is verified unchanged via before/after
     `HostMemory.Query` (the pre-fix code would have flipped it).
   - `PosixMprotect_MatchesKernelMprotectBehavior`: valid range → both OK
     with identical host protects; unmapped range → both NOT_FOUND, no extra
     host protects.
   - `Mprotect_ZeroLengthAndPartialOverlapAreRejected`: zero addr/len →
     INVALID_ARGUMENT; range starting inside the mapping but running past its
     end → NOT_FOUND with zero host protect calls (rejected, not clamped);
     interior range that 16 KiB alignment expands to the whole mapping → OK.

## Verification

- `dotnet build src/SharpEmu.Libs/SharpEmu.Libs.csproj`: **0 errors**
  (3 pre-existing SHEM006 catalog warnings only).
- `dotnet test tests/SharpEmu.Libs.Tests/SharpEmu.Libs.Tests.csproj`:
  **877/877 passed** (873 pre-existing + 4 new), 0 failed, 0 skipped.
- Extra safety (not mandated): full solution build `SharpEmu.slnx` 0 errors /
  0 warnings; ShaderCompiler 96/96, Metal 60/60, SourceGenerators 36/36.
- Reverted incidental `src/SharpEmu.CLI/packages.lock.json` churn caused by
  the full-solution restore pulling newer shared-cache package versions
  (unrelated to this change).

## Notes for integration

- `Mprotect_UnmappedRangeReturnsNotFound` (pre-existing) still passes: POSIX
  `HostMemory.Query` reports untracked host memory as free, so unmapped
  rejections keep returning NOT_FOUND exactly as before.
- libc-heap (`Marshal.AllocHGlobal`) blocks are not in either region table;
  on POSIX the old code already failed there (untracked → mprotect fails →
  NOT_FOUND), so behavior is unchanged; on Windows such an mprotect
  previously succeeded and is now denied — the deliberate security trade.

