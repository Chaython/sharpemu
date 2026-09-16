# SharpEmu — Integration Build (Windows x64)

This is a self-contained Windows x64 build of the SharpEmu PS5 emulator
(branch improvements/stability-graphics-playability = the restored
integration lineage + a second round of improvements (kernel timing/sync
fixes, audio/pad format fixes, 9 Vulkan correctness+perf fixes, stencil
plane backing + HLE polish) + a round-3 GUI build-integrity fix with a
headless GUI boot smoke-test suite + a round-4 broken-dump diagnostics
pass — see IMPROVEMENTS.md §7 and §8).

## Run
1. Unzip anywhere.
2. Run `SharpEmu.exe` — the Avalonia GUI opens when started without arguments.
   (SmartScreen may warn on unsigned binaries: "More info" -> "Run anyway".)
3. Launch a game by selecting its executable (eboot.bin), or pass it on the
   command line: `SharpEmu.exe C:\path\to\eboot.bin`.

## Requirements
- Windows 10/11 x64
- A Vulkan 1.2+ driver (the default Silk.NET Vulkan backend requires it)
- PS5 firmware modules and games are NOT included; supply your own

## Games won't boot? Read this first (round 4)
SharpEmu needs a **decrypted** executable image (bare ELF or fake-signed
fSELF). Two things this build now tells you explicitly instead of failing
cryptically:

- A **0-byte / truncated eboot.bin** (interrupted extraction, cloud-sync
  placeholder, antivirus quarantine) is rejected within milliseconds with
  the exact cause, remediation steps, and any non-empty executable images
  found nearby (e.g. in `decrypted/`).
- An **intact retail-encrypted dump** (e.g. straight from a backport/repack
  release) is also reported clearly: no PS5 emulator can decrypt retail
  eboots without console-derived keys. Use a decrypted dump.

Every load now logs `[RUNTIME] Executable image: <path> (<N> bytes)` — if
that size is not tens of MB, the dump itself is the problem.

## Notes
- The optional experimental "Native Vulkan" backend needs `sharpemu_gpu_vulkan.dll`
  from https://github.com/sharpemu/sharpemu.vulkan placed next to SharpEmu.exe
  (the GUI warns if it is missing). The default managed backend works without it.
- `plugins/` contains the FFmpeg runtime (avcodec/avformat/etc.) used by the
  video player — keep it next to the exe.
- Full details of everything merged/implemented: IMPROVEMENTS.md (in the source
  zip and on the delivery page).

## Build info
- .NET SDK 10.0.103, Release, win-x64, self-contained single-file
- Published from a fully wiped artifacts tree; the embedded GUI assembly is
  byte-identical (verbatim) to the freshly compiled one that passed the 13
  headless boot smoke tests, and the linux-x64 build of the same revision
  was verified booting under Xvfb.
- Full test suite at this revision: 1445/1445 passing
  (Libs 1096 · ShaderCompiler 240 · Metal 60 · SourceGenerators 36 · Gui 13,
  including 10 new broken-dump regression tests)
