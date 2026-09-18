// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.ShaderCompiler;
using SharpEmu.ShaderCompiler.Metal;
using Xunit;

namespace SharpEmu.ShaderCompiler.Tests;

// Metal-side coverage for the improvements that touch the MSL emitter:
// the two FLAT-segment fixes (FLAT opcodes used to be passed to the shared
// memory emitter untranslated, and the FLAT address was never rebased against
// the binding's SGPR base) and the full FLAT/GLOBAL atomic set. Also covers
// the tokenizer-driven compare detection: "ImageSampleCl" is a LOD-clamp
// sample, not a shadow compare.
public sealed class MslFlatAndAtomicTests
{
    private const ulong ShaderAddress = 0x1_0000_B000;
    private const uint SEndpgm = 0xBF810000;

    [Fact]
    public void FlatLoadDword_TranslatesWithBaseSubtraction()
    {
        // Before the fix this failed with "unsupported memory opcode
        // FlatLoadDword" (the FLAT name never matched the GLOBAL cases), and
        // even mapped it would have used the raw guest pointer instead of the
        // binding-relative offset.
        var source = CompileMslMemory("FlatLoadDword", flat: true);

        Assert.Contains("(v[9] - s[12])", source, StringComparison.Ordinal);
        Assert.Contains("sharpemu_load_word(b0,", source, StringComparison.Ordinal);
        Assert.DoesNotContain("unsupported memory opcode", source, StringComparison.Ordinal);
    }

    [Fact]
    public void FlatAtomicAdd_EmitsMslAtomicWithBaseSubtraction()
    {
        var source = CompileMslMemory("FlatAtomicAdd", flat: true);

        Assert.Contains("(v[9] - s[12])", source, StringComparison.Ordinal);
        Assert.Contains(
            "atomic_fetch_add_explicit((device atomic_uint*)(b0 +",
            source,
            StringComparison.Ordinal);
        Assert.Contains("memory_order_acq_rel", source, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("Swap", "atomic_exchange_explicit((device atomic_uint*)(b0 +")]
    [InlineData("Cmpswap", "atomic_compare_exchange_weak_explicit((device atomic_uint*)(b0 +")]
    [InlineData("Add", "atomic_fetch_add_explicit((device atomic_uint*)(b0 +")]
    [InlineData("Sub", "atomic_fetch_sub_explicit((device atomic_uint*)(b0 +")]
    [InlineData("Smin", "atomic_fetch_min_explicit((device atomic_int*)(b0 +")]
    [InlineData("Umin", "atomic_fetch_min_explicit((device atomic_uint*)(b0 +")]
    [InlineData("Smax", "atomic_fetch_max_explicit((device atomic_int*)(b0 +")]
    [InlineData("Umax", "atomic_fetch_max_explicit((device atomic_uint*)(b0 +")]
    [InlineData("And", "atomic_fetch_and_explicit((device atomic_uint*)(b0 +")]
    [InlineData("Or", "atomic_fetch_or_explicit((device atomic_uint*)(b0 +")]
    [InlineData("Xor", "atomic_fetch_xor_explicit((device atomic_uint*)(b0 +")]
    [InlineData("Inc", "atomic_fetch_add_explicit((device atomic_uint*)(b0 +")]
    [InlineData("Dec", "atomic_fetch_sub_explicit((device atomic_uint*)(b0 +")]
    public void AtomicOpcodes_EmitMslAtomicCalls(string suffix, string expectedCall)
    {
        var globalSource = CompileMslMemory("GlobalAtomic" + suffix, flat: false);
        Assert.Contains(expectedCall, globalSource, StringComparison.Ordinal);
        Assert.Contains("memory_order_acq_rel", globalSource, StringComparison.Ordinal);
        if (suffix is "Smin" or "Smax")
        {
            Assert.Contains("as_type<int>", globalSource, StringComparison.Ordinal);
        }

        // The FLAT segment shares the emission path after rebase.
        var flatSource = CompileMslMemory("FlatAtomic" + suffix, flat: true);
        Assert.Contains(expectedCall, flatSource, StringComparison.Ordinal);
        Assert.Contains("(v[9] - s[12])", flatSource, StringComparison.Ordinal);
    }

    [Fact]
    public void SampleClIsNotTreatedAsCompare()
    {
        // "ImageSampleCl" contains "SampleC", but it is the LOD-clamp form:
        // the tokenizer must keep it on the plain sampling path with no
        // in-shader PCF select.
        var source = CompileMslImageSample("ImageSampleCl");

        Assert.Contains(".sample(", source, StringComparison.Ordinal);
        Assert.DoesNotContain("? 1.0f : 0.0f", source, StringComparison.Ordinal);
    }

    [Fact]
    public void SampleCStillAppliesManualCompare()
    {
        // Metal keeps the manual PCF select for real compare variants (the
        // documented Metal Dref gap), selected through the tokenizer.
        var source = CompileMslImageSample("ImageSampleC");

        Assert.Contains(".sample(", source, StringComparison.Ordinal);
        Assert.Contains("? 1.0f : 0.0f", source, StringComparison.Ordinal);
    }

    private static string CompileMslMemory(string opcode, bool flat)
    {
        var control = new Gen5GlobalMemoryControl(
            DwordCount: opcode.EndsWith("Cmpswap", StringComparison.Ordinal) ? 2u : 1u,
            VectorAddress: 9,
            VectorData: 3,
            ScalarAddress: flat ? 12u : 16u,
            OffsetBytes: 0,
            Glc: false,
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
            [0xDCC88000, 0x03100009],
            sources,
            [],
            control);
        return CompileMsl(
            [instruction],
            [],
            new[]
            {
                new Gen5GlobalMemoryBinding(
                    flat ? 12u : 16u,
                    0x1_0000_1000UL,
                    [0u],
                    new byte[64],
                    64,
                    DataPooled: false)
                {
                    Writable = true,
                },
            });
    }

    private static string CompileMslImageSample(string opcode)
    {
        var control = new Gen5ImageControl(
            Dmask: 0xF,
            VectorAddress: 0,
            AddressRegisters: [0],
            VectorData: 4,
            ScalarResource: 8,
            ScalarSampler: 16,
            Dimension: 0,
            IsArray: false,
            Glc: false,
            Slc: false,
            A16: false,
            D16: false);
        var instruction = new Gen5ShaderInstruction(
            0,
            Gen5ShaderEncoding.Mimg,
            opcode,
            [],
            [],
            [Gen5Operand.Vector(4)],
            control);
        var descriptor = new uint[8];
        descriptor[1] = 71u << 20; // FORMAT_16_16_16_16_FLOAT
        var binding = new Gen5ImageBinding(
            instruction.Pc,
            instruction.Opcode,
            control,
            descriptor,
            new uint[4],
            null);
        return CompileMsl([instruction], [binding], []);
    }

    private static string CompileMsl(
        IReadOnlyList<Gen5ShaderInstruction> instructions,
        IReadOnlyList<Gen5ImageBinding> imageBindings,
        IReadOnlyList<Gen5GlobalMemoryBinding> globalBindings)
    {
        var end = new Gen5ShaderInstruction(
            instructions.Count == 1 ? 4u : 8u,
            Gen5ShaderEncoding.Sopp,
            "SEndpgm",
            [SEndpgm],
            [],
            [],
            null);
        var state = new Gen5ShaderState(
            new Gen5ShaderProgram(ShaderAddress, [.. instructions, end]),
            [],
            null);
        var evaluation = new Gen5ShaderEvaluation(
            new uint[256],
            new uint[256],
            imageBindings,
            globalBindings);

        Assert.True(
            Gen5MslTranslator.TryCompileComputeShader(
                state,
                evaluation,
                32,
                1,
                1,
                out var shader,
                out var error),
            error);
        return shader.Source;
    }
}
