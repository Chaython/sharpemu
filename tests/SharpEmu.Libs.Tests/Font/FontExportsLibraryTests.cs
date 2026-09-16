// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Buffers.Binary;
using SharpEmu.HLE;
using SharpEmu.Libs.Font;
using SharpEmu.Libs.Kernel;
using Xunit;

namespace SharpEmu.Libs.Tests.Font;

[CollectionDefinition("FontRoutingState", DisableParallelization = true)]
public sealed class FontRoutingStateCollection;

// Font-library lifecycle, metrics/kerning and /system_resources/fonts routing.
// The fixture pins SHARPEMU_FONTS_DIR to a temp directory and the collection
// keeps other environment-mutating tests from running alongside.
[Collection("FontRoutingState")]
public sealed class FontExportsLibraryTests : IDisposable
{
    private const ulong Base = 0x1_0000_0000;
    private const ulong LibraryOut = Base + 0x100;
    private const ulong HandleOut = Base + 0x180;
    private const ulong PathString = Base + 0x200;
    private const ulong MetricsOut = Base + 0x300;
    private const ulong KerningOut = Base + 0x340;
    private const ulong RenderOut = Base + 0x380;
    private const ushort FontMagic = 0x0F02;

    private readonly FakeCpuMemory _memory = new(Base, 0x8000);
    private readonly CpuContext _ctx;
    private readonly string _fontsRoot;
    private readonly string? _previousFontsDir;

    public FontExportsLibraryTests()
    {
        _ctx = new CpuContext(_memory, Generation.Gen5);
        _fontsRoot = Path.Combine(Path.GetTempPath(), $"sharpemu-fonts-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_fontsRoot);
        _previousFontsDir = Environment.GetEnvironmentVariable("SHARPEMU_FONTS_DIR");
        Environment.SetEnvironmentVariable("SHARPEMU_FONTS_DIR", _fontsRoot);
    }

    public void Dispose()
    {
        Environment.SetEnvironmentVariable("SHARPEMU_FONTS_DIR", _previousFontsDir);
        if (Directory.Exists(_fontsRoot))
        {
            Directory.Delete(_fontsRoot, recursive: true);
        }
    }

    private CpuContext Reg(ulong rdi = 0, ulong rsi = 0, ulong rdx = 0, ulong rcx = 0, ulong r8 = 0)
    {
        _ctx[CpuRegister.Rdi] = rdi;
        _ctx[CpuRegister.Rsi] = rsi;
        _ctx[CpuRegister.Rdx] = rdx;
        _ctx[CpuRegister.Rcx] = rcx;
        _ctx[CpuRegister.R8] = r8;
        return _ctx;
    }

    [Fact]
    public void CreateLibraryWithEdition_ThenDestroyLibrary_ClearsSlot()
    {
        Assert.Equal(0, FontExports.CreateLibraryWithEdition(Reg(rcx: LibraryOut)));
        Assert.True(_ctx.TryReadUInt64(LibraryOut, out var library));
        Assert.True(library >= Base);

        // The handle carries the font-library magic.
        Span<byte> magic = stackalloc byte[sizeof(ushort)];
        Assert.True(_memory.TryRead(library, magic));
        Assert.Equal(0x0F01, BinaryPrimitives.ReadUInt16LittleEndian(magic));

        // Teardown clears the caller's slot so a stale handle is not reused.
        Assert.Equal(0, FontExports.DestroyLibrary(Reg(rdi: LibraryOut)));
        Assert.True(_ctx.TryReadUInt64(LibraryOut, out var cleared));
        Assert.Equal(0uL, cleared);
    }

    [Fact]
    public void NullOutPointers_ReturnInvalidArgument()
    {
        Assert.Equal(
            (int)OrbisGen2Result.ORBIS_GEN2_ERROR_INVALID_ARGUMENT,
            FontExports.DestroyLibrary(Reg(rdi: 0)));
        Assert.Equal(
            (int)OrbisGen2Result.ORBIS_GEN2_ERROR_INVALID_ARGUMENT,
            FontExports.GetKerning(Reg(rcx: 0)));
        Assert.Equal(
            (int)OrbisGen2Result.ORBIS_GEN2_ERROR_INVALID_ARGUMENT,
            FontExports.OpenFontFile(Reg(rsi: 0, r8: HandleOut)));
    }

    [Fact]
    public void SystemResourcesFonts_RoutesToConfiguredHostDirectory()
    {
        File.WriteAllText(Path.Combine(_fontsRoot, "ksts-gothic.ttf"), "font-bytes");

        Assert.Equal(
            Path.GetFullPath(Path.Combine(_fontsRoot, "ksts-gothic.ttf")),
            KernelMemoryCompatExports.ResolveGuestPath("/system_resources/fonts/ksts-gothic.ttf"));

        // The mount root itself resolves, and ".." traversal is clamped at
        // the root like every other mount: the escape lands back inside the
        // fonts tree instead of reaching the host filesystem.
        Assert.Equal(
            Path.GetFullPath(_fontsRoot),
            KernelMemoryCompatExports.ResolveGuestPath("/system_resources/fonts"));
        Assert.Equal(
            Path.GetFullPath(Path.Combine(_fontsRoot, "etc", "passwd")),
            KernelMemoryCompatExports.ResolveGuestPath("/system_resources/fonts/../../etc/passwd"));
    }

    [Fact]
    public void SystemResourcesFonts_IsReadOnlyForGuestMutations()
    {
        Assert.True(KernelMemoryCompatExports.IsReadOnlyGuestMutationPath("/system_resources/fonts/ksts-gothic.ttf"));
        Assert.True(KernelMemoryCompatExports.IsReadOnlyGuestMutationPath("/system_resources/fonts"));
        Assert.False(KernelMemoryCompatExports.IsReadOnlyGuestMutationPath("/savedata0/save.bin"));
    }

    [Fact]
    public void OpenFontFile_SystemFont_ReturnsHandleWithFontMagic()
    {
        File.WriteAllText(Path.Combine(_fontsRoot, "ksts-gothic.ttf"), "font-bytes");
        _memory.WriteCString(PathString, "/system_resources/fonts/ksts-gothic.ttf");

        Assert.Equal(0, FontExports.OpenFontFile(Reg(rsi: PathString, r8: HandleOut)));
        Assert.True(_ctx.TryReadUInt64(HandleOut, out var handle));
        Assert.True(handle >= Base);

        Span<byte> magic = stackalloc byte[sizeof(ushort)];
        Assert.True(_memory.TryRead(handle, magic));
        Assert.Equal(FontMagic, BinaryPrimitives.ReadUInt16LittleEndian(magic));
    }

    [Fact]
    public void OpenFontFile_MissingSystemFont_ReturnsNotFound()
    {
        _memory.WriteCString(PathString, "/system_resources/fonts/no-such-font.ttf");
        Assert.Equal(
            (int)OrbisGen2Result.ORBIS_GEN2_ERROR_NOT_FOUND,
            FontExports.OpenFontFile(Reg(rsi: PathString, r8: HandleOut)));
    }

    [Fact]
    public void OpenFontFile_FourArgumentVariant_SelectsRcxOutputSlot()
    {
        File.WriteAllText(Path.Combine(_fontsRoot, "ksts-gothic.ttf"), "font-bytes");
        _memory.WriteCString(PathString, "/system_resources/fonts/ksts-gothic.ttf");

        // Older titles pass (library, path, openMode, SceFontHandle*): the
        // output slot is RCX while R8 carries the small openMode value. A
        // register-disambiguation bug would treat openMode as the out pointer
        // and fault writing to address 1.
        Assert.Equal(0, FontExports.OpenFontFile(Reg(rsi: PathString, rcx: HandleOut, r8: 1)));
        Assert.True(_ctx.TryReadUInt64(HandleOut, out var handle));
        Assert.True(handle >= Base);
    }

    [Fact]
    public void GetCharGlyphMetrics_WritesFallbackGeometry()
    {
        const uint Sentinel = 0xDEADBEEF;
        Assert.True(_ctx.TryWriteUInt32(MetricsOut + 8 * sizeof(float), Sentinel));

        Assert.Equal(0, FontExports.GetCharGlyphMetrics(Reg(rdx: MetricsOut)));

        Span<byte> metrics = stackalloc byte[9 * sizeof(float)];
        Assert.True(_memory.TryRead(MetricsOut, metrics));
        Assert.Equal(8.0f, BinaryPrimitives.ReadSingleLittleEndian(metrics));
        Assert.Equal(16.0f, BinaryPrimitives.ReadSingleLittleEndian(metrics[(sizeof(float) * 1)..]));
        Assert.Equal(0.0f, BinaryPrimitives.ReadSingleLittleEndian(metrics[(sizeof(float) * 2)..]));
        Assert.Equal(12.0f, BinaryPrimitives.ReadSingleLittleEndian(metrics[(sizeof(float) * 3)..]));
        Assert.Equal(8.0f, BinaryPrimitives.ReadSingleLittleEndian(metrics[(sizeof(float) * 4)..]));
        Assert.Equal(0.0f, BinaryPrimitives.ReadSingleLittleEndian(metrics[(sizeof(float) * 5)..]));
        Assert.Equal(0.0f, BinaryPrimitives.ReadSingleLittleEndian(metrics[(sizeof(float) * 6)..]));
        Assert.Equal(16.0f, BinaryPrimitives.ReadSingleLittleEndian(metrics[(sizeof(float) * 7)..]));
        Assert.Equal(Sentinel, BinaryPrimitives.ReadUInt32LittleEndian(metrics[(sizeof(float) * 8)..]));
    }

    [Fact]
    public void GetKerning_WritesZeroForFallbackGeometry()
    {
        Assert.True(_ctx.TryWriteUInt32(KerningOut, 0xA5A5A5A5));
        Assert.Equal(0, FontExports.GetKerning(Reg(rcx: KerningOut)));
        Assert.True(_ctx.TryReadUInt32(KerningOut, out var kerning));
        Assert.Equal(0u, kerning);
    }

    [Fact]
    public void RenderCharGlyphImageHorizontal_WritesMetricsAndClearsOutput()
    {
        _memory.TryWrite(RenderOut, Enumerable.Repeat((byte)0xA5, 0x40).ToArray());
        Assert.Equal(0, FontExports.RenderCharGlyphImageHorizontal(Reg(rcx: MetricsOut, r8: RenderOut)));

        // The documented rasterization stub: fallback metrics are reported and
        // the render output is cleared rather than drawn into.
        Span<byte> output = stackalloc byte[0x40];
        Assert.True(_memory.TryRead(RenderOut, output));
        for (var index = 0; index < output.Length; index++)
        {
            Assert.Equal(0, output[index]);
        }

        Span<byte> metrics = stackalloc byte[8 * sizeof(float)];
        Assert.True(_memory.TryRead(MetricsOut, metrics));
        Assert.Equal(12.0f, BinaryPrimitives.ReadSingleLittleEndian(metrics[(sizeof(float) * 3)..]));
    }
}
