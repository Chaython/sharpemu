// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Buffers.Binary;
using SharpEmu.HLE;
using SharpEmu.ShaderCompiler;
using Xunit;

namespace SharpEmu.ShaderCompiler.Tests;

// Decodes the shadow-compare ("C variant") MIMG opcodes that were previously
// missing from the gen5 decoder, plus representative existing entries, and
// checks the operand wiring the translators rely on. Word layouts follow the
// RDNA2 ISA manual: MIMG op = word0[24:18], dmask = word0[11:8],
// glc = word0[13]; word1 packs vaddr[7:0], vdata[15:8], sres[20:16],
// samp[25:21].
public sealed class Gen5MimgCVariantDecodeTests
{
    private const ulong ShaderAddress = 0x1_0000_C000;
    private const uint EndPgm = 0xBF810000;

    [Theory]
    [InlineData(0x28u, "ImageSampleC")]
    [InlineData(0x29u, "ImageSampleCCl")]
    [InlineData(0x2Au, "ImageSampleCD")]
    [InlineData(0x2Cu, "ImageSampleCL")]
    [InlineData(0x2Du, "ImageSampleCB")]
    [InlineData(0x38u, "ImageSampleCO")]
    [InlineData(0x3Bu, "ImageSampleCDClO")]
    [InlineData(0x3Fu, "ImageSampleCLzO")]
    [InlineData(0x49u, "ImageGather4CCl")]
    [InlineData(0x4Cu, "ImageGather4CBCl")]
    [InlineData(0x58u, "ImageGather4CO")]
    [InlineData(0x5Cu, "ImageGather4CBClO")]
    public void ShadowCompareVariantsDecode(uint opcodeNumber, string expectedName)
    {
        var instruction = DecodeSingleImageInstruction(opcodeNumber, glc: false);

        Assert.Equal(expectedName, instruction.Opcode);
        var control = Assert.IsType<Gen5ImageControl>(instruction.Control);
        Assert.Equal(8u, control.ScalarResource);
        Assert.Equal(16u, control.ScalarSampler);
        Assert.Equal(4u, control.VectorData);
        Assert.Equal(0xFu, control.Dmask);
        Assert.False(control.Glc);
        // Sampled ops keep the data register as the single destination.
        Assert.Equal(new[] { Gen5Operand.Vector(4) }, instruction.Destinations);
    }

    [Theory]
    [InlineData(0x2Fu, "ImageSampleCLz")]
    [InlineData(0x48u, "ImageGather4C")]
    [InlineData(0x5Fu, "ImageGather4CLzO")]
    public void PreExistingCompareVariantsStillDecode(uint opcodeNumber, string expectedName)
    {
        var instruction = DecodeSingleImageInstruction(opcodeNumber, glc: false);
        Assert.Equal(expectedName, instruction.Opcode);
    }

    [Fact]
    public void ImageSampleC_DecodesControlAndSources()
    {
        // IMAGE_SAMPLE_C v4, v0, s[8:15], s[16:19] dmask:0xF dim:1D glc.
        // The one-VADDR-dword form names exactly one address register;
        // GetAddressRegister derives later components from VectorAddress.
        var instruction = DecodeSingleImageInstruction(0x28, glc: true);

        Assert.Equal("ImageSampleC", instruction.Opcode);
        var control = Assert.IsType<Gen5ImageControl>(instruction.Control);
        Assert.True(control.Glc);
        Assert.Equal(0u, control.VectorAddress);
        Assert.Equal(4u, control.VectorData);
        Assert.Equal(
            [
                Gen5Operand.Vector(0),
                Gen5Operand.Scalar(8),
                Gen5Operand.Scalar(16),
            ],
            instruction.Sources);
        Assert.Equal(new[] { Gen5Operand.Vector(4) }, instruction.Destinations);
    }

    [Fact]
    public void ImageGather4CLzO_DecodesOffsetCompareAndBodyLayout()
    {
        var instruction = DecodeSingleImageInstruction(0x5F, glc: false);

        Assert.Equal("ImageGather4CLzO", instruction.Opcode);
        var control = Assert.IsType<Gen5ImageControl>(instruction.Control);
        Assert.Equal(0xFu, control.Dmask);
        // The tokenizer must see compare + lod-zero + offset for this name.
        Assert.True(Gen5ShaderTranslator.ParseImageOpcodeFlags(instruction.Opcode, out var flags));
        Assert.True(flags.Gather);
        Assert.True(flags.Compare);
        Assert.True(flags.LodZero);
        Assert.True(flags.Offset);
    }

    private static Gen5ShaderInstruction DecodeSingleImageInstruction(
        uint opcodeNumber,
        bool glc)
    {
        var word0 = 0xF0000000u |
            (opcodeNumber << 18) |
            (0xFu << 8) |
            (glc ? 1u << 13 : 0u);
        // vaddr=0, vdata=4, sres=2 (s[8:11]), samp=4 (s[16:19]).
        var word1 = (4u << 8) | (2u << 16) | (4u << 21);
        var memory = new TestCpuMemory(ShaderAddress, 0x1000);
        var program = new uint[] { word0, word1, EndPgm };
        var shader = new byte[program.Length * sizeof(uint)];
        for (var index = 0; index < program.Length; index++)
        {
            BinaryPrimitives.WriteUInt32LittleEndian(
                shader.AsSpan(index * sizeof(uint)),
                program[index]);
        }

        Assert.True(memory.TryWrite(ShaderAddress, shader));
        var ctx = new CpuContext(memory, Generation.Gen5);
        Assert.True(
            Gen5ShaderTranslator.TryDecodeProgram(
                ctx,
                ShaderAddress,
                out var decoded,
                out var error),
            error);
        return decoded.Instructions[0];
    }

    private sealed class TestCpuMemory(ulong baseAddress, int size) : ICpuMemory
    {
        private readonly byte[] _storage = new byte[size];

        public bool TryRead(ulong virtualAddress, Span<byte> destination)
        {
            if (!TryResolve(virtualAddress, destination.Length, out var offset))
            {
                return false;
            }

            _storage.AsSpan(offset, destination.Length).CopyTo(destination);
            return true;
        }

        public bool TryWrite(ulong virtualAddress, ReadOnlySpan<byte> source)
        {
            if (!TryResolve(virtualAddress, source.Length, out var offset))
            {
                return false;
            }

            source.CopyTo(_storage.AsSpan(offset, source.Length));
            return true;
        }

        private bool TryResolve(ulong virtualAddress, int length, out int offset)
        {
            offset = 0;
            if (virtualAddress < baseAddress)
            {
                return false;
            }

            var relative = virtualAddress - baseAddress;
            if (relative + (ulong)length > (ulong)_storage.Length)
            {
                return false;
            }

            offset = (int)relative;
            return true;
        }
    }
}
