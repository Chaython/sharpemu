// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Buffers.Binary;
using SharpEmu.HLE;
using SharpEmu.ShaderCompiler;
using SharpEmu.ShaderCompiler.Vulkan;
using Xunit;

namespace SharpEmu.ShaderCompiler.Tests;

// The full FLAT/GLOBAL atomic set: decode (both segments, return and
// no-return forms) plus SPIR-V emission of the matching OpAtomic* opcodes.
// Word layouts follow the RDNA2 ISA manual: op = word0[24:18], segment =
// word0[15:14] (0=FLAT, 2=GLOBAL), glc = word0[16], offset = word0[12:0];
// word1 packs vaddr[7:0], saddr[15:8], vdata[31:24].
public sealed class Gen5FlatGlobalAtomicTests
{
    private const ulong ShaderAddress = 0x1_0000_A000;
    private const uint EndPgm = 0xBF810000;

    [Theory]
    [InlineData(0x30u, "Swap")]
    [InlineData(0x31u, "Cmpswap")]
    [InlineData(0x32u, "Add")]
    [InlineData(0x33u, "Sub")]
    [InlineData(0x35u, "Smin")]
    [InlineData(0x36u, "Umin")]
    [InlineData(0x37u, "Smax")]
    [InlineData(0x38u, "Umax")]
    [InlineData(0x39u, "And")]
    [InlineData(0x3Au, "Or")]
    [InlineData(0x3Bu, "Xor")]
    [InlineData(0x3Cu, "Inc")]
    [InlineData(0x3Du, "Dec")]
    public void AtomicOpcodes_DecodeBothSegments(uint opcodeNumber, string suffix)
    {
        var program = DecodeProgram(
            FlatWord(opcodeNumber, glc: false),
            0x037D0001,
            GlobalWord(opcodeNumber, glc: false),
            0x03100009);

        Assert.Equal("FlatAtomic" + suffix, program.Instructions[0].Opcode);
        var flatControl = Assert.IsType<Gen5GlobalMemoryControl>(
            program.Instructions[0].Control);
        Assert.True(flatControl.UsesFlatAddress);
        Assert.Equal(1u, flatControl.VectorAddress);
        Assert.Equal(3u, flatControl.VectorData);
        Assert.Equal(
            suffix == "Cmpswap" ? 2u : 1u,
            flatControl.DwordCount);
        Assert.False(flatControl.Glc);

        Assert.Equal("GlobalAtomic" + suffix, program.Instructions[1].Opcode);
        var globalControl = Assert.IsType<Gen5GlobalMemoryControl>(
            program.Instructions[1].Control);
        Assert.False(globalControl.UsesFlatAddress);
        Assert.Equal(9u, globalControl.VectorAddress);
        Assert.Equal(16u, globalControl.ScalarAddress);
        Assert.Equal(3u, globalControl.VectorData);
        Assert.Equal(
            suffix == "Cmpswap" ? 2u : 1u,
            globalControl.DwordCount);
        Assert.Equal(
            new[] { Gen5Operand.Vector(9), Gen5Operand.Scalar(16) },
            program.Instructions[1].Sources);
    }

    [Fact]
    public void FlatAtomicCmpswap_UsesTwoDataRegisters()
    {
        var program = DecodeProgram(FlatWord(0x31, glc: true), 0x037D0001);

        Assert.Equal("FlatAtomicCmpswap", program.Instructions[0].Opcode);
        var control = Assert.IsType<Gen5GlobalMemoryControl>(
            program.Instructions[0].Control);
        Assert.Equal(2u, control.DwordCount);
        Assert.Equal(3u, control.VectorData);
        Assert.True(control.Glc);
        // The pre-operation value returns through VDATA (keyed on GLC at
        // emission), so the instruction itself carries no destinations.
        Assert.Empty(program.Instructions[0].Destinations);
    }

    [Fact]
    public void GlobalAtomicAdd_GlcBitSelectsReturnForm()
    {
        var withReturn = DecodeProgram(GlobalWord(0x32, glc: true), 0x03100009);
        var control = Assert.IsType<Gen5GlobalMemoryControl>(
            withReturn.Instructions[0].Control);
        Assert.True(control.Glc);

        var withoutReturn = DecodeProgram(GlobalWord(0x32, glc: false), 0x03100009);
        var plainControl = Assert.IsType<Gen5GlobalMemoryControl>(
            withoutReturn.Instructions[0].Control);
        Assert.False(plainControl.Glc);
    }

    [Theory]
    [InlineData("Swap", SpirvOp.AtomicExchange)]
    [InlineData("Cmpswap", SpirvOp.AtomicCompareExchange)]
    [InlineData("Add", SpirvOp.AtomicIAdd)]
    [InlineData("Sub", SpirvOp.AtomicISub)]
    [InlineData("Smin", SpirvOp.AtomicSMin)]
    [InlineData("Umin", SpirvOp.AtomicUMin)]
    [InlineData("Smax", SpirvOp.AtomicSMax)]
    [InlineData("Umax", SpirvOp.AtomicUMax)]
    [InlineData("And", SpirvOp.AtomicAnd)]
    [InlineData("Or", SpirvOp.AtomicOr)]
    [InlineData("Xor", SpirvOp.AtomicXor)]
    [InlineData("Inc", SpirvOp.AtomicIIncrement)]
    [InlineData("Dec", SpirvOp.AtomicIDecrement)]
    public void AtomicOpcodes_EmitSpirvAtomicsForBothSegments(
        string suffix,
        SpirvOp expected)
    {
        var globalOpcodes = CompileAtomic("GlobalAtomic" + suffix, flat: false);
        Assert.Contains((ushort)expected, globalOpcodes);

        var flatOpcodes = CompileAtomic("FlatAtomic" + suffix, flat: true);
        Assert.Contains((ushort)expected, flatOpcodes);
        // FLAT addresses carry the whole 64-bit guest pointer; the translator
        // rebases them against the inferred SGPR pair with a subtraction.
        Assert.Contains((ushort)SpirvOp.ISub, flatOpcodes);
    }

    private static uint FlatWord(uint opcodeNumber, bool glc) =>
        0xDC000000u | (opcodeNumber << 18) | (glc ? 1u << 16 : 0u);

    private static uint GlobalWord(uint opcodeNumber, bool glc) =>
        0xDC000000u | (opcodeNumber << 18) | (glc ? 1u << 16 : 0u) | (2u << 14);

    private static Gen5ShaderProgram DecodeProgram(params uint[] words)
    {
        var memory = new TestCpuMemory(ShaderAddress, 0x2000);
        var programWords = new uint[words.Length + 1];
        words.CopyTo(programWords, 0);
        programWords[^1] = EndPgm;
        var shader = new byte[programWords.Length * sizeof(uint)];
        for (var index = 0; index < programWords.Length; index++)
        {
            BinaryPrimitives.WriteUInt32LittleEndian(
                shader.AsSpan(index * sizeof(uint)),
                programWords[index]);
        }

        Assert.True(memory.TryWrite(ShaderAddress, shader));
        var ctx = new CpuContext(memory, Generation.Gen5);
        Assert.True(
            Gen5ShaderTranslator.TryDecodeProgram(
                ctx,
                ShaderAddress,
                out var program,
                out var error),
            error);
        return program;
    }

    private static HashSet<ushort> CompileAtomic(string opcode, bool flat, bool glc = false)
    {
        var control = new Gen5GlobalMemoryControl(
            DwordCount: opcode.EndsWith("Cmpswap", StringComparison.Ordinal) ? 2u : 1u,
            VectorAddress: 9,
            VectorData: 3,
            ScalarAddress: flat ? 12u : 16u,
            OffsetBytes: 0,
            Glc: glc,
            Slc: false,
            UsesFlatAddress: flat);
        Gen5Operand[] sources = flat
            ?
            [
                Gen5Operand.Vector(9),
                Gen5Operand.Vector(10),
                Gen5Operand.Scalar(12),
            ]
            :
            [
                Gen5Operand.Vector(9),
                Gen5Operand.Scalar(16),
            ];
        var instruction = new Gen5ShaderInstruction(
            0,
            Gen5ShaderEncoding.Flat,
            opcode,
            [flat ? FlatWord(0x32, glc) : GlobalWord(0x32, glc), 0x03100009],
            sources,
            [],
            control);
        var end = new Gen5ShaderInstruction(
            4,
            Gen5ShaderEncoding.Sopp,
            "SEndpgm",
            [EndPgm],
            [],
            [],
            null);
        var state = new Gen5ShaderState(
            new Gen5ShaderProgram(ShaderAddress, [instruction, end]),
            [],
            null);
        var binding = new Gen5GlobalMemoryBinding(
            flat ? 12u : 16u,
            0x1_0000_1000UL,
            [0u],
            new byte[64],
            64,
            DataPooled: false)
        {
            Writable = true,
        };
        var evaluation = new Gen5ShaderEvaluation(
            new uint[256],
            new uint[256],
            [],
            [binding]);

        Assert.True(
            Gen5SpirvTranslator.TryCompileComputeShader(
                state,
                evaluation,
                1,
                1,
                1,
                out var shader,
                out var error),
            error);
        return CollectOpcodes(shader.Spirv);
    }

    private static HashSet<ushort> CollectOpcodes(byte[] spirv)
    {
        Assert.Equal(0x07230203u, BinaryPrimitives.ReadUInt32LittleEndian(spirv));
        var opcodes = new HashSet<ushort>();
        // 5-word SPIR-V header, then (wordCount << 16 | opcode) words.
        for (var offset = 5 * sizeof(uint); offset + sizeof(uint) <= spirv.Length;)
        {
            var word = BinaryPrimitives.ReadUInt32LittleEndian(
                spirv.AsSpan(offset, sizeof(uint)));
            opcodes.Add((ushort)word);
            offset += Math.Max((int)(word >> 16), 1) * sizeof(uint);
        }

        return opcodes;
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
