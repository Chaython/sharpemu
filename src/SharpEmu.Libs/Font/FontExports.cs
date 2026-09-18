// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Buffers.Binary;
using System.Text;
using SharpEmu.HLE;
using SharpEmu.Libs.Kernel;

namespace SharpEmu.Libs.Font;

/// <summary>
/// libSceFont / libSceFontFt behavior. The library/renderer/font handles are
/// opaque guest allocations, glyph metrics come from a fixed fallback
/// geometry, and glyph rasterization remains a documented stub: the render
/// exports report success and clear their output buffers instead of drawing
/// pixels (see <see cref="RenderCharGlyphImageHorizontal"/>). What IS real:
/// font files referenced by guest path are resolved through the kernel
/// guest-path table (<see cref="OpenFontFile"/>) — including the optional
/// read-only /system_resources/fonts tree — so titles that load fonts through
/// their own rasterizer can read real font bytes.
/// </summary>
public static class FontExports
{
    private const ushort GlyphMagic = 0x0F03;
    private const ushort FontMagic = 0x0F02;
    private const int GlyphSize = 0x100;
    private const int GlyphMetricsSize = 8 * sizeof(float);
    private const int RenderOutputSize = 0x40;
    private const int MaxGuestPathBytes = 4096;
    // Guest pointers live well above the first 64 KiB.
    private const ulong MinGuestPointer = 0x1_0000;

    private static readonly object AllocationGate = new();
    private static readonly Stack<ulong> FreeGlyphs = new();
    private static ulong _librarySelectionAddress;
    private static ulong _rendererSelectionAddress;

    [SysAbiExport(
        Nid = "whrS4oksXc4",
        ExportName = "sceFontMemoryInit",
        Target = Generation.Gen5,
        LibraryName = "libSceFont")]
    public static int MemoryInit(CpuContext ctx)
    {
        var descriptorAddress = ctx[CpuRegister.Rdi];
        var regionAddress = ctx[CpuRegister.Rsi];
        var regionSize = (uint)ctx[CpuRegister.Rdx];
        var interfaceAddress = ctx[CpuRegister.Rcx];
        var mspaceAddress = ctx[CpuRegister.R8];
        var destroyCallback = ctx[CpuRegister.R9];
        if (descriptorAddress == 0 ||
            !TryWriteUInt32(ctx, descriptorAddress, 0x00000F00) ||
            !TryWriteUInt32(ctx, descriptorAddress + 0x04, regionSize) ||
            !ctx.TryWriteUInt64(descriptorAddress + 0x08, regionAddress) ||
            !ctx.TryWriteUInt64(descriptorAddress + 0x10, mspaceAddress) ||
            !ctx.TryWriteUInt64(descriptorAddress + 0x18, interfaceAddress) ||
            !ctx.TryWriteUInt64(descriptorAddress + 0x20, destroyCallback) ||
            !ctx.TryWriteUInt64(descriptorAddress + 0x28, 0) ||
            !ctx.TryWriteUInt64(descriptorAddress + 0x30, 0) ||
            !ctx.TryWriteUInt64(descriptorAddress + 0x38, mspaceAddress))
        {
            return SetReturn(ctx, OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT);
        }

        return SetSuccess(ctx);
    }

    [SysAbiExport(
        Nid = "oM+XCzVG3oM",
        ExportName = "sceFontSelectLibraryFt",
        Target = Generation.Gen5,
        LibraryName = "libSceFontFt")]
    public static int SelectLibraryFt(CpuContext ctx) =>
        ReturnSelection(ctx, ref _librarySelectionAddress, 0x38);

    [SysAbiExport(
        Nid = "Xx974EW-QFY",
        ExportName = "sceFontSelectRendererFt",
        Target = Generation.Gen5,
        LibraryName = "libSceFontFt")]
    public static int SelectRendererFt(CpuContext ctx) =>
        ReturnSelection(ctx, ref _rendererSelectionAddress, 0x100);

    [SysAbiExport(
        Nid = "n590hj5Oe-k",
        ExportName = "sceFontCreateLibraryWithEdition",
        Target = Generation.Gen5,
        LibraryName = "libSceFont")]
    public static int CreateLibraryWithEdition(CpuContext ctx) =>
        CreateOpaqueHandle(ctx, ctx[CpuRegister.Rcx], 0x100, magic: 0x0F01);

    [SysAbiExport(
        Nid = "WaSFJoRWXaI",
        ExportName = "sceFontCreateRendererWithEdition",
        Target = Generation.Gen5,
        LibraryName = "libSceFont")]
    public static int CreateRendererWithEdition(CpuContext ctx) =>
        CreateOpaqueHandle(ctx, ctx[CpuRegister.Rcx], 0x100, magic: 0x0F07);

    [SysAbiExport(
        Nid = "3OdRkSjOcog",
        ExportName = "sceFontBindRenderer",
        Target = Generation.Gen5,
        LibraryName = "libSceFont")]
    public static int BindRenderer(CpuContext ctx) => SetSuccess(ctx);

    [SysAbiExport(
        Nid = "N1EBMeGhf7E",
        ExportName = "sceFontSetScalePixel",
        Target = Generation.Gen5,
        LibraryName = "libSceFont")]
    public static int SetScalePixel(CpuContext ctx) => SetSuccess(ctx);

    [SysAbiExport(
        Nid = "TMtqoFQjjbA",
        ExportName = "sceFontSetEffectSlant",
        Target = Generation.Gen5,
        LibraryName = "libSceFont")]
    public static int SetEffectSlant(CpuContext ctx) => SetSuccess(ctx);

    [SysAbiExport(
        Nid = "v0phZwa4R5o",
        ExportName = "sceFontSetEffectWeight",
        Target = Generation.Gen5,
        LibraryName = "libSceFont")]
    public static int SetEffectWeight(CpuContext ctx) => SetSuccess(ctx);

    [SysAbiExport(
        Nid = "6vGCkkQJOcI",
        ExportName = "sceFontSetupRenderScalePixel",
        Target = Generation.Gen5,
        LibraryName = "libSceFont")]
    public static int SetupRenderScalePixel(CpuContext ctx) => SetSuccess(ctx);

    [SysAbiExport(
        Nid = "lz9y9UFO2UU",
        ExportName = "sceFontSetupRenderEffectSlant",
        Target = Generation.Gen5,
        LibraryName = "libSceFont")]
    public static int SetupRenderEffectSlant(CpuContext ctx) => SetSuccess(ctx);

    [SysAbiExport(
        Nid = "XIGorvLusDQ",
        ExportName = "sceFontSetupRenderEffectWeight",
        Target = Generation.Gen5,
        LibraryName = "libSceFont")]
    public static int SetupRenderEffectWeight(CpuContext ctx) => SetSuccess(ctx);

    [SysAbiExport(
        Nid = "imxVx8lm+KM",
        ExportName = "sceFontGetHorizontalLayout",
        Target = Generation.Gen5,
        LibraryName = "libSceFont")]
    public static int GetHorizontalLayout(CpuContext ctx)
    {
        var layoutAddress = ctx[CpuRegister.Rsi];
        if (layoutAddress == 0)
        {
            return SetReturn(ctx, OrbisGen2Result.ORBIS_GEN2_ERROR_INVALID_ARGUMENT);
        }

        // Baseline, line advance, decoration extent: the same invented geometry
        // as GetRenderCharGlyphMetrics.
        var values = new[] { 12.0f, 16.0f, 0.0f };
        for (var index = 0; index < values.Length; index++)
        {
            if (!TryWriteUInt32(
                    ctx,
                    layoutAddress + (ulong)(index * sizeof(float)),
                    BitConverter.SingleToUInt32Bits(values[index])))
            {
                return SetReturn(ctx, OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT);
            }
        }

        return SetSuccess(ctx);
    }

    [SysAbiExport(
        Nid = "3BrWWFU+4ts",
        ExportName = "sceFontGetVerticalLayout",
        Target = Generation.Gen5,
        LibraryName = "libSceFont")]
    public static int GetVerticalLayout(CpuContext ctx)
    {
        var layoutAddress = ctx[CpuRegister.Rsi];
        if (layoutAddress == 0)
        {
            return SetReturn(ctx, OrbisGen2Result.ORBIS_GEN2_ERROR_INVALID_ARGUMENT);
        }

        // Baseline (horizontal offset), line advance, decoration extent.
        // Mirrors the same three-float layout as GetHorizontalLayout, but
        // interpreted for vertical writing (e.g. CJK text rendered top-to-bottom).
        var values = new[] { 8.0f, 16.0f, 0.0f };
        for (var index = 0; index < values.Length; index++)
        {
            if (!TryWriteUInt32(
                    ctx,
                    layoutAddress + (ulong)(index * sizeof(float)),
                    BitConverter.SingleToUInt32Bits(values[index])))
            {
                return SetReturn(ctx, OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT);
            }
        }

        return SetSuccess(ctx);
    }

    [SysAbiExport(
        Nid = "cKYtVmeSTcw",
        ExportName = "sceFontOpenFontSet",
        Target = Generation.Gen5,
        LibraryName = "libSceFont")]
    public static int OpenFontSet(CpuContext ctx) =>
        CreateOpaqueHandle(ctx, ctx[CpuRegister.R8], 0x100, magic: 0x0F02);

    [SysAbiExport(
        Nid = "KXUpebrFk1U",
        ExportName = "sceFontOpenFontMemory",
        Target = Generation.Gen5,
        LibraryName = "libSceFont")]
    public static int OpenFontMemory(CpuContext ctx) =>
        CreateOpaqueHandle(ctx, ctx[CpuRegister.R8], 0x100, magic: 0x0F02);

    [SysAbiExport(
        Nid = "JzCH3SCFnAU",
        ExportName = "sceFontOpenFontInstance",
        Target = Generation.Gen5,
        LibraryName = "libSceFont")]
    public static int OpenFontInstance(CpuContext ctx)
    {
        var sourceHandle = ctx[CpuRegister.Rdi];
        var setupHandle = ctx[CpuRegister.Rsi];
        var outputAddress = ctx[CpuRegister.Rdx];
        if (outputAddress == 0)
        {
            return SetReturn(ctx, OrbisGen2Result.ORBIS_GEN2_ERROR_INVALID_ARGUMENT);
        }

        if (setupHandle != 0)
        {
            return ctx.TryWriteUInt64(outputAddress, setupHandle)
                ? SetSuccess(ctx)
                : SetReturn(ctx, OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT);
        }

        if (!TryAllocateOpaque(ctx, 0x100, out var handle))
        {
            return SetReturn(ctx, OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT);
        }

        if (sourceHandle != 0)
        {
            Span<byte> source = stackalloc byte[0x100];
            if (ctx.Memory.TryRead(sourceHandle, source))
            {
                _ = ctx.Memory.TryWrite(handle, source);
            }
        }

        _ = TryWriteUInt16(ctx, handle, 0x0F02);
        return ctx.TryWriteUInt64(outputAddress, handle)
            ? SetSuccess(ctx)
            : SetReturn(ctx, OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT);
    }

    /// <summary>
    /// Opens a font file referenced by guest path (a title-bundled font under
    /// /app0, or a real system font under /system_resources/fonts). The path
    /// is resolved through the kernel guest-path table; a file that does not
    /// exist resolves to nothing and the call reports NOT_FOUND — a defined
    /// failure the caller must already handle — rather than handing back a
    /// handle the rasterizer stubs would silently render nothing from.
    /// </summary>
    /// <remarks>
    /// PS4 ABI: (library, const char* path, openMode, fileSize, SceFontHandle*).
    /// Some titles bind the older four-argument variant
    /// (library, path, openMode, SceFontHandle*); the output slot is selected
    /// by which register carries a plausible pointer, mirroring the register
    /// disambiguation in sceSaveDataCreateTransactionResource.
    /// </remarks>
    [SysAbiExport(
        Nid = "RvXyHMUiLhE",
        ExportName = "sceFontOpenFontFile",
        Target = Generation.Gen5,
        LibraryName = "libSceFont")]
    public static int OpenFontFile(CpuContext ctx)
    {
        var pathAddress = ctx[CpuRegister.Rsi];
        var outputAddress = SelectHandleOutAddress(ctx[CpuRegister.R8], ctx[CpuRegister.Rcx]);
        if (pathAddress == 0 || outputAddress == 0)
        {
            return SetReturn(ctx, OrbisGen2Result.ORBIS_GEN2_ERROR_INVALID_ARGUMENT);
        }

        if (!TryReadGuestPath(ctx, pathAddress, out var guestPath))
        {
            return SetReturn(ctx, OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT);
        }

        var hostPath = KernelMemoryCompatExports.ResolveGuestPath(guestPath);
        if (string.IsNullOrEmpty(hostPath) || !File.Exists(hostPath))
        {
            TraceFont($"open_font_file path='{guestPath}' not_found");
            return SetReturn(ctx, OrbisGen2Result.ORBIS_GEN2_ERROR_NOT_FOUND);
        }

        TraceFont($"open_font_file path='{guestPath}' host='{hostPath}'");
        return CreateOpaqueHandle(ctx, outputAddress, 0x100, FontMagic);
    }

    [SysAbiExport(
        Nid = "SsRbbCiWoGw",
        ExportName = "sceFontSupportSystemFonts",
        Target = Generation.Gen5,
        LibraryName = "libSceFont")]
    public static int SupportSystemFonts(CpuContext ctx) => SetSuccess(ctx);

    [SysAbiExport(
        Nid = "mz2iTY0MK4A",
        ExportName = "sceFontSupportExternalFonts",
        Target = Generation.Gen5,
        LibraryName = "libSceFont")]
    public static int SupportExternalFonts(CpuContext ctx) => SetSuccess(ctx);

    [SysAbiExport(
        Nid = "CUKn5pX-NVY",
        ExportName = "sceFontAttachDeviceCacheBuffer",
        Target = Generation.Gen5,
        LibraryName = "libSceFont")]
    public static int AttachDeviceCacheBuffer(CpuContext ctx) => SetSuccess(ctx);

    [SysAbiExport(
        Nid = "IQtleGLL5pQ",
        ExportName = "sceFontGetRenderCharGlyphMetrics",
        Target = Generation.Gen5,
        LibraryName = "libSceFont")]
    public static int GetRenderCharGlyphMetrics(CpuContext ctx) =>
        WriteFallbackGlyphMetrics(ctx, ctx[CpuRegister.Rdx]);

    [SysAbiExport(
        Nid = "gdUCnU0gHdI",
        ExportName = "sceFontRenderSurfaceInit",
        Target = Generation.Gen5,
        LibraryName = "libSceFont")]
    public static int RenderSurfaceInit(CpuContext ctx)
    {
        var surfaceAddress = ctx[CpuRegister.Rdi];
        var bufferAddress = ctx[CpuRegister.Rsi];
        var widthBytes = (uint)ctx[CpuRegister.Rdx];
        var pixelBytes = (uint)ctx[CpuRegister.Rcx] & 0xFF;
        var width = (uint)ctx[CpuRegister.R8];
        var height = (uint)ctx[CpuRegister.R9];
        if (surfaceAddress == 0 ||
            !ctx.TryWriteUInt64(surfaceAddress, bufferAddress) ||
            !TryWriteUInt32(ctx, surfaceAddress + 0x08, widthBytes) ||
            !TryWriteUInt32(ctx, surfaceAddress + 0x0C, pixelBytes) ||
            !TryWriteUInt32(ctx, surfaceAddress + 0x10, width) ||
            !TryWriteUInt32(ctx, surfaceAddress + 0x14, height) ||
            !TryWriteUInt32(ctx, surfaceAddress + 0x18, 0) ||
            !TryWriteUInt32(ctx, surfaceAddress + 0x1C, 0) ||
            !TryWriteUInt32(ctx, surfaceAddress + 0x20, width) ||
            !TryWriteUInt32(ctx, surfaceAddress + 0x24, height))
        {
            return SetReturn(ctx, OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT);
        }

        return SetSuccess(ctx);
    }

    [SysAbiExport(
        Nid = "C-4Qw5Srlyw",
        ExportName = "sceFontGenerateCharGlyph",
        Target = Generation.Gen5,
        LibraryName = "libSceFont")]
    public static int GenerateCharGlyph(CpuContext ctx)
    {
        var outputAddress = ctx[CpuRegister.Rcx];
        if (outputAddress == 0)
        {
            return SetReturn(ctx, OrbisGen2Result.ORBIS_GEN2_ERROR_INVALID_ARGUMENT);
        }

        if (!TryRentGlyph(ctx, out var glyph) ||
            !ctx.TryWriteUInt64(outputAddress, glyph))
        {
            return SetReturn(ctx, OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT);
        }

        return SetSuccess(ctx);
    }

    [SysAbiExport(
        Nid = "8-zmgsxkBek",
        ExportName = "sceFontGlyphDefineAttribute",
        Target = Generation.Gen5,
        LibraryName = "libSceFont")]
    public static int GlyphDefineAttribute(CpuContext ctx) => SetSuccess(ctx);

    [SysAbiExport(
        Nid = "LHDoRWVFGqk",
        ExportName = "sceFontDeleteGlyph",
        Target = Generation.Gen5,
        LibraryName = "libSceFont")]
    public static int DeleteGlyph(CpuContext ctx)
    {
        var glyphPointerAddress = ctx[CpuRegister.Rsi];
        if (glyphPointerAddress == 0)
        {
            return SetReturn(ctx, OrbisGen2Result.ORBIS_GEN2_ERROR_INVALID_ARGUMENT);
        }

        if (!ctx.TryReadUInt64(glyphPointerAddress, out var glyph))
        {
            return SetReturn(ctx, OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT);
        }

        if (glyph != 0)
        {
            lock (AllocationGate)
            {
                FreeGlyphs.Push(glyph);
            }
        }

        return ctx.TryWriteUInt64(glyphPointerAddress, 0)
            ? SetSuccess(ctx)
            : SetReturn(ctx, OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT);
    }

    [SysAbiExport(
        Nid = "kAenWy1Zw5o",
        ExportName = "sceFontRenderCharGlyphImageHorizontal",
        Target = Generation.Gen5,
        LibraryName = "libSceFont")]
    public static int RenderCharGlyphImageHorizontal(CpuContext ctx)
    {
        var metricsAddress = ctx[CpuRegister.Rcx];
        var resultAddress = ctx[CpuRegister.R8];

        // Documented rasterization stub: the fallback geometry is reported
        // and the output buffer is cleared, but no glyph pixels are drawn.
        if (metricsAddress != 0)
        {
            var metricsResult = WriteFallbackGlyphMetrics(ctx, metricsAddress);
            if (metricsResult != 0)
            {
                return metricsResult;
            }
        }

        if (resultAddress != 0)
        {
            Span<byte> cleared = stackalloc byte[RenderOutputSize];
            cleared.Clear();
            if (!ctx.Memory.TryWrite(resultAddress, cleared))
            {
                return SetReturn(ctx, OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT);
            }
        }

        return SetSuccess(ctx);
    }

    [SysAbiExport(
        Nid = "vzHs3C8lWJk",
        ExportName = "sceFontCloseFont",
        Target = Generation.Gen5,
        LibraryName = "libSceFont")]
    public static int CloseFont(CpuContext ctx) => SetSuccess(ctx);

    [SysAbiExport(
        Nid = "1QjhKxrsOB8",
        ExportName = "sceFontUnbindRenderer",
        Target = Generation.Gen5,
        LibraryName = "libSceFont")]
    public static int UnbindRenderer(CpuContext ctx) => SetSuccess(ctx);

    [SysAbiExport(
        Nid = "exAxkyVLt0s",
        ExportName = "sceFontDestroyRenderer",
        Target = Generation.Gen5,
        LibraryName = "libSceFont")]
    public static int DestroyRenderer(CpuContext ctx)
    {
        var rendererPointerAddress = ctx[CpuRegister.Rdi];
        if (rendererPointerAddress == 0)
        {
            return SetReturn(ctx, OrbisGen2Result.ORBIS_GEN2_ERROR_INVALID_ARGUMENT);
        }

        return ctx.TryWriteUInt64(rendererPointerAddress, 0)
            ? SetSuccess(ctx)
            : SetReturn(ctx, OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT);
    }

    // Mirrors DestroyRenderer: the library slot is cleared so a stale handle
    // is not reused after teardown.
    [SysAbiExport(
        Nid = "FXP359ygujs",
        ExportName = "sceFontDestroyLibrary",
        Target = Generation.Gen5,
        LibraryName = "libSceFont")]
    public static int DestroyLibrary(CpuContext ctx)
    {
        var libraryPointerAddress = ctx[CpuRegister.Rdi];
        if (libraryPointerAddress == 0)
        {
            return SetReturn(ctx, OrbisGen2Result.ORBIS_GEN2_ERROR_INVALID_ARGUMENT);
        }

        return ctx.TryWriteUInt64(libraryPointerAddress, 0)
            ? SetSuccess(ctx)
            : SetReturn(ctx, OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT);
    }

    [SysAbiExport(
        Nid = "h6hIgxXEiEc",
        ExportName = "sceFontMemoryTerm",
        Target = Generation.Gen5,
        LibraryName = "libSceFont")]
    public static int MemoryTerm(CpuContext ctx) => SetSuccess(ctx);

    // The non-rendering twin of GetRenderCharGlyphMetrics: same fallback
    // geometry, asked of a font instead of a renderer.
    [SysAbiExport(
        Nid = "L97d+3OgMlE",
        ExportName = "sceFontGetCharGlyphMetrics",
        Target = Generation.Gen5,
        LibraryName = "libSceFont")]
    public static int GetCharGlyphMetrics(CpuContext ctx) =>
        WriteFallbackGlyphMetrics(ctx, ctx[CpuRegister.Rdx]);

    // No kerning is modeled for the fallback geometry.
    [SysAbiExport(
        Nid = "sDuhHGNhHvE",
        ExportName = "sceFontGetKerning",
        Target = Generation.Gen5,
        LibraryName = "libSceFont")]
    public static int GetKerning(CpuContext ctx)
    {
        var kerningAddress = ctx[CpuRegister.Rcx];
        if (kerningAddress == 0)
        {
            return SetReturn(ctx, OrbisGen2Result.ORBIS_GEN2_ERROR_INVALID_ARGUMENT);
        }

        return TryWriteUInt32(ctx, kerningAddress, 0)
            ? SetSuccess(ctx)
            : SetReturn(ctx, OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT);
    }

    [SysAbiExport(
        Nid = "+ehNXJPUyhk",
        ExportName = "sceFontFtSupportSystemFonts",
        Target = Generation.Gen5,
        LibraryName = "libSceFontFt")]
    public static int FtSupportSystemFonts(CpuContext ctx) => SetSuccess(ctx);

    // The point-based scale twins of the pixel variants above; the fallback
    // geometry ignores scale, so these accept and discard the value.
    [SysAbiExport(
        Nid = "sw65+7wXCKE",
        ExportName = "sceFontSetScalePoint",
        Target = Generation.Gen5,
        LibraryName = "libSceFont")]
    public static int SetScalePoint(CpuContext ctx) => SetSuccess(ctx);

    [SysAbiExport(
        Nid = "nMZid4oDfi4",
        ExportName = "sceFontSetupRenderScalePoint",
        Target = Generation.Gen5,
        LibraryName = "libSceFont")]
    public static int SetupRenderScalePoint(CpuContext ctx) => SetSuccess(ctx);

    [SysAbiExport(
        Nid = "I1acwR7Qp8E",
        ExportName = "sceFontSetResolutionDpi",
        Target = Generation.Gen5,
        LibraryName = "libSceFont")]
    public static int SetResolutionDpi(CpuContext ctx) => SetSuccess(ctx);

    [SysAbiExport(
        Nid = "kihFGYJee7o",
        ExportName = "sceFontSetFontsOpenMode",
        Target = Generation.Gen5,
        LibraryName = "libSceFont")]
    public static int SetFontsOpenMode(CpuContext ctx) => SetSuccess(ctx);

    // The fixed fallback glyph geometry shared by every metrics query:
    // horizontalWidth, verticalWidth, horizontalAdvance, ... in the order the
    // render exports already report.
    private static int WriteFallbackGlyphMetrics(CpuContext ctx, ulong metricsAddress)
    {
        if (metricsAddress == 0)
        {
            return SetReturn(ctx, OrbisGen2Result.ORBIS_GEN2_ERROR_INVALID_ARGUMENT);
        }

        var values = new[] { 8.0f, 16.0f, 0.0f, 12.0f, 8.0f, 0.0f, 0.0f, 16.0f };
        for (var index = 0; index < values.Length; index++)
        {
            if (!TryWriteUInt32(
                    ctx,
                    metricsAddress + (ulong)(index * sizeof(float)),
                    BitConverter.SingleToUInt32Bits(values[index])))
            {
                return SetReturn(ctx, OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT);
            }
        }

        return SetSuccess(ctx);
    }

    // Picks the SceFontHandle* slot between the five-argument PS4 ABI (out in
    // R8 after library/path/openMode/fileSize) and the four-argument variant
    // (out in RCX after library/path/openMode), based on which register holds
    // a plausible guest pointer.
    private static ulong SelectHandleOutAddress(ulong r8, ulong rcx) =>
        r8 >= MinGuestPointer ? r8 : rcx >= MinGuestPointer ? rcx : 0;

    // Reads a NUL-terminated guest path, bounded so a missing terminator
    // cannot loop unbounded over guest memory.
    private static bool TryReadGuestPath(CpuContext ctx, ulong address, out string value)
    {
        value = string.Empty;
        var builder = new StringBuilder();
        Span<byte> chunk = stackalloc byte[128];
        var consumed = 0;
        while (consumed < MaxGuestPathBytes)
        {
            var length = Math.Min(chunk.Length, MaxGuestPathBytes - consumed);
            if (!ctx.Memory.TryRead(address + (ulong)consumed, chunk[..length]))
            {
                return false;
            }

            var terminator = chunk[..length].IndexOf((byte)0);
            if (terminator >= 0)
            {
                builder.Append(Encoding.ASCII.GetString(chunk[..terminator]));
                value = builder.ToString();
                return true;
            }

            builder.Append(Encoding.ASCII.GetString(chunk[..length]));
            consumed += length;
        }

        // Unterminated path: treat as invalid rather than guess the end.
        return false;
    }

    private static void TraceFont(string message)
    {
        if (!string.Equals(Environment.GetEnvironmentVariable("SHARPEMU_LOG_FONT"), "1", StringComparison.Ordinal))
        {
            return;
        }

        Console.Error.WriteLine($"[LOADER][TRACE] font.{message}");
    }

    private static bool TryRentGlyph(CpuContext ctx, out ulong glyph)
    {
        lock (AllocationGate)
        {
            if (FreeGlyphs.Count > 0)
            {
                glyph = FreeGlyphs.Pop();
                return TryWriteUInt16(ctx, glyph, GlyphMagic);
            }
        }

        return TryAllocateOpaque(ctx, GlyphSize, out glyph) &&
               TryWriteUInt16(ctx, glyph, GlyphMagic);
    }

    private static int ReturnSelection(CpuContext ctx, ref ulong selectionAddress, uint objectSize)
    {
        if (ctx[CpuRegister.Rdi] != 0)
        {
            ctx[CpuRegister.Rax] = 0;
            return 0;
        }

        lock (AllocationGate)
        {
            if (selectionAddress == 0)
            {
                if (!TryAllocateOpaque(ctx, 0x20, out selectionAddress) ||
                    !TryWriteUInt32(ctx, selectionAddress, 0) ||
                    !TryWriteUInt32(ctx, selectionAddress + 4, objectSize))
                {
                    selectionAddress = 0;
                }
            }
        }

        ctx[CpuRegister.Rax] = selectionAddress;
        return 0;
    }

    private static int CreateOpaqueHandle(CpuContext ctx, ulong outputAddress, int size, ushort magic)
    {
        if (outputAddress == 0 || !TryAllocateOpaque(ctx, size, out var handle))
        {
            return SetReturn(ctx, OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT);
        }

        if (!TryWriteUInt16(ctx, handle, magic) || !ctx.TryWriteUInt64(outputAddress, handle))
        {
            return SetReturn(ctx, OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT);
        }

        return SetSuccess(ctx);
    }

    private static bool TryAllocateOpaque(CpuContext ctx, int size, out ulong address)
    {
        address = 0;
        if (ctx.Memory is not IGuestMemoryAllocator allocator ||
            !allocator.TryAllocateGuestMemory((ulong)size, 0x10, out address))
        {
            return false;
        }

        Span<byte> bytes = stackalloc byte[size];
        bytes.Clear();
        return ctx.Memory.TryWrite(address, bytes);
    }

    private static bool TryWriteUInt16(CpuContext ctx, ulong address, ushort value)
    {
        Span<byte> bytes = stackalloc byte[sizeof(ushort)];
        BinaryPrimitives.WriteUInt16LittleEndian(bytes, value);
        return ctx.Memory.TryWrite(address, bytes);
    }

    private static bool TryWriteUInt32(CpuContext ctx, ulong address, uint value)
    {
        Span<byte> bytes = stackalloc byte[sizeof(uint)];
        BinaryPrimitives.WriteUInt32LittleEndian(bytes, value);
        return ctx.Memory.TryWrite(address, bytes);
    }

    private static int SetSuccess(CpuContext ctx)
    {
        ctx[CpuRegister.Rax] = 0;
        return 0;
    }

    private static int SetReturn(CpuContext ctx, OrbisGen2Result result)
    {
        ctx[CpuRegister.Rax] = unchecked((ulong)(int)result);
        return (int)result;
    }
}
