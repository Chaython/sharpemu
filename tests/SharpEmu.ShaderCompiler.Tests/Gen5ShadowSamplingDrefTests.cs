// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Buffers.Binary;
using SharpEmu.ShaderCompiler;
using SharpEmu.ShaderCompiler.Vulkan;
using Xunit;

namespace SharpEmu.ShaderCompiler.Tests;

// Shadow-compare (dref) sampling: IMAGE_SAMPLE_C* / IMAGE_GATHER4_C* must
// lower to OpImageSampleDrefImplicitLod / OpImageSampleDrefExplicitLod /
// OpImageDrefGather against image types declared with Depth=2, instead of
// sampling and comparing in-shader. Programs and evaluations are synthetic:
// the translator is driven directly from decoded-style instructions, the
// pattern the image suites use.
public sealed class Gen5ShadowSamplingDrefTests
{
    private const ulong ShaderAddress = 0x1_0000_D000;
    private const uint SEndpgm = 0xBF810000;
    private const uint FloatFormatWord = 71u << 20; // FORMAT_16_16_16_16_FLOAT
    private const uint UintFormatWord = 20u << 20;  // R32ui

    [Fact]
    public void ImageSampleC_EmitsDrefImplicitLod()
    {
        var instructions = CompileImageOperation("ImageSampleC");

        var dref = Assert.Single(
            instructions,
            item => item.Opcode == SpirvOp.ImageSampleDrefImplicitLod);
        // No image operands on the plain compare form.
        Assert.Equal(5, dref.Operands.Length);
        Assert.DoesNotContain(
            instructions,
            item => item.Opcode is SpirvOp.ImageSampleImplicitLod or
                SpirvOp.ImageSampleExplicitLod);
        // The manual in-shader compare must be gone.
        Assert.DoesNotContain(
            instructions,
            item => item.Opcode == SpirvOp.FOrdLessThanEqual);
    }

    [Fact]
    public void ImageSampleCL_EmitsDrefExplicitLod()
    {
        var instructions = CompileImageOperation("ImageSampleCL");

        var dref = Assert.Single(
            instructions,
            item => item.Opcode == SpirvOp.ImageSampleDrefExplicitLod);
        // Explicit LOD: image-operands mask 0x2 (Lod) followed by the LOD id,
        // right after the dref.
        Assert.Equal(7, dref.Operands.Length);
        Assert.Equal(2u, dref.Operands[5]);
        Assert.DoesNotContain(
            instructions,
            item => item.Opcode == SpirvOp.ImageSampleDrefImplicitLod);
    }

    [Fact]
    public void ImageSampleCLz_EmitsDrefExplicitLod()
    {
        var instructions = CompileImageOperation("ImageSampleCLz");

        var dref = Assert.Single(
            instructions,
            item => item.Opcode == SpirvOp.ImageSampleDrefExplicitLod);
        Assert.Equal(7, dref.Operands.Length);
        Assert.Equal(2u, dref.Operands[5]);
    }

    [Fact]
    public void ImageSampleCB_EmitsDrefImplicitLodWithBiasOperand()
    {
        var instructions = CompileImageOperation("ImageSampleCB");

        var dref = Assert.Single(
            instructions,
            item => item.Opcode == SpirvOp.ImageSampleDrefImplicitLod);
        // Bias: image-operands mask 0x1 (Bias) right after the dref.
        Assert.Equal(7, dref.Operands.Length);
        Assert.Equal(1u, dref.Operands[5]);
    }

    [Fact]
    public void ImageSampleCD_EmitsDrefExplicitLodWithGradients()
    {
        var instructions = CompileImageOperation("ImageSampleCD");

        var dref = Assert.Single(
            instructions,
            item => item.Opcode == SpirvOp.ImageSampleDrefExplicitLod);
        // Gradients: image-operands mask 0x4 (Grad) followed by dx/dy.
        Assert.Equal(8, dref.Operands.Length);
        Assert.Equal(4u, dref.Operands[5]);
    }

    [Fact]
    public void ImageSampleCO_FoldsDynamicOffsetIntoCoordinates()
    {
        var instructions = CompileImageOperation("ImageSampleCO");

        var dref = Assert.Single(
            instructions,
            item => item.Opcode == SpirvOp.ImageSampleDrefImplicitLod);
        // The per-lane offset is folded into the coordinates (Vulkan forbids
        // the dynamic Offset operand on non-gather sampling), so the Dref
        // instruction carries no image operands at all.
        Assert.Equal(5, dref.Operands.Length);
    }

    [Fact]
    public void ImageGather4C_EmitsDrefGather()
    {
        var instructions = CompileImageOperation("ImageGather4C");

        Assert.Single(instructions, item => item.Opcode == SpirvOp.ImageDrefGather);
        Assert.DoesNotContain(instructions, item => item.Opcode == SpirvOp.ImageGather);
        Assert.DoesNotContain(
            instructions,
            item => item.Opcode == SpirvOp.FOrdLessThanEqual);
    }

    [Fact]
    public void ImageGather4CO_EmitsDrefGatherWithOffsetOperand()
    {
        var instructions = CompileImageOperation("ImageGather4CO");

        var dref = Assert.Single(
            instructions,
            item => item.Opcode == SpirvOp.ImageDrefGather);
        // Gather ops may carry the dynamic Offset image operand: mask 0x10
        // followed by the offset id, after the dref.
        Assert.Equal(7, dref.Operands.Length);
        Assert.Equal(0x10u, dref.Operands[5]);
    }

    [Theory]
    [InlineData("ImageSampleC")]
    [InlineData("ImageSampleCLz")]
    [InlineData("ImageSampleCB")]
    [InlineData("ImageGather4C")]
    [InlineData("ImageGather4CLz")]
    [InlineData("ImageGather4CO")]
    public void CompareBindingsDeclareDepth2ImageTypes(string opcode)
    {
        var instructions = CompileImageOperation(opcode);

        var imageType = Assert.Single(
            instructions,
            item => item.Opcode == SpirvOp.TypeImage);
        // Operands: [result, sampledType, dim, depth, arrayed, ms, sampled,
        // format]; Depth=2 declares "unknown depth", legal for both the Dref
        // instructions and plain sampling of the same binding.
        Assert.Equal(2u, imageType.Operands[3]);
    }

    [Fact]
    public void PlainSamplingStaysDepth0WithoutDref()
    {
        var instructions = CompileImageOperation("ImageSampleLz");

        var imageType = Assert.Single(
            instructions,
            item => item.Opcode == SpirvOp.TypeImage);
        Assert.Equal(0u, imageType.Operands[3]);
        Assert.Single(instructions, item => item.Opcode == SpirvOp.ImageSampleExplicitLod);
        Assert.DoesNotContain(
            instructions,
            item => item.Opcode is SpirvOp.ImageSampleDrefImplicitLod or
                SpirvOp.ImageSampleDrefExplicitLod or
                SpirvOp.ImageDrefGather);
    }

    [Fact]
    public void IntegerCompareBindingFallsBackToManualCompare()
    {
        // A uint texture cannot be declared as a depth image, so the compare
        // stays in-shader rather than emitting an invalid Dref instruction.
        var instructions = CompileImageOperation(
            "ImageSampleC",
            descriptorWord1: UintFormatWord);

        var imageType = Assert.Single(
            instructions,
            item => item.Opcode == SpirvOp.TypeImage);
        Assert.Equal(0u, imageType.Operands[3]);
        Assert.DoesNotContain(
            instructions,
            item => item.Opcode is SpirvOp.ImageSampleDrefImplicitLod or
                SpirvOp.ImageSampleDrefExplicitLod or
                SpirvOp.ImageDrefGather);
        Assert.Contains(instructions, item => item.Opcode == SpirvOp.ImageSampleImplicitLod);
        Assert.Contains(instructions, item => item.Opcode == SpirvOp.FOrdLessThanEqual);
    }

    [Fact]
    public void CompareAndPlainBindingsDeclareDistinctImageTypes()
    {
        var instructions = CompileImageOperation(
            "ImageSample",
            secondOpcode: "ImageSampleC");

        var imageTypes = instructions
            .Where(item => item.Opcode == SpirvOp.TypeImage)
            .ToArray();
        Assert.Equal(2, imageTypes.Length);
        Assert.Equal(
            new[] { 0u, 2u },
            imageTypes.Select(item => item.Operands[3]).OrderBy(depth => depth).ToArray());
    }

    private static IReadOnlyList<ParsedSpirvInstruction> CompileImageOperation(
        string opcode,
        uint descriptorWord1 = FloatFormatWord,
        string? secondOpcode = null)
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
        var imageInstruction = new Gen5ShaderInstruction(
            0,
            Gen5ShaderEncoding.Mimg,
            opcode,
            [],
            [],
            [Gen5Operand.Vector(4)],
            control);
        Gen5ShaderInstruction? secondInstruction = null;
        if (secondOpcode is not null)
        {
            secondInstruction = new Gen5ShaderInstruction(
                4,
                Gen5ShaderEncoding.Mimg,
                secondOpcode,
                [],
                [],
                [Gen5Operand.Vector(4)],
                control);
        }

        var end = new Gen5ShaderInstruction(
            8,
            Gen5ShaderEncoding.Sopp,
            "SEndpgm",
            [SEndpgm],
            [],
            [],
            null);
        Gen5ShaderInstruction[] programInstructions = secondInstruction is null
            ? [imageInstruction, end]
            : [imageInstruction, secondInstruction, end];
        var state = new Gen5ShaderState(
            new Gen5ShaderProgram(ShaderAddress, programInstructions),
            [],
            null);
        var scalarRegisters = new uint[256];
        var descriptor = new uint[8];
        descriptor[1] = descriptorWord1;
        var bindings = new List<Gen5ImageBinding>
        {
            new(
                imageInstruction.Pc,
                imageInstruction.Opcode,
                control,
                descriptor,
                new uint[4],
                null),
        };
        if (secondInstruction is not null)
        {
            bindings.Add(
                new Gen5ImageBinding(
                    secondInstruction.Pc,
                    secondInstruction.Opcode,
                    control,
                    descriptor,
                    new uint[4],
                    null));
        }

        var evaluation = new Gen5ShaderEvaluation(
            scalarRegisters,
            scalarRegisters,
            bindings,
            []);

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
        return ReadSpirvInstructions(shader.Spirv);
    }

    private static IReadOnlyList<ParsedSpirvInstruction> ReadSpirvInstructions(
        byte[] spirv)
    {
        Assert.Equal(0x07230203u, BinaryPrimitives.ReadUInt32LittleEndian(spirv));
        var instructions = new List<ParsedSpirvInstruction>();
        for (var offset = 5 * sizeof(uint); offset < spirv.Length;)
        {
            var instruction = BinaryPrimitives.ReadUInt32LittleEndian(
                spirv.AsSpan(offset));
            var wordCount = checked((int)(instruction >> 16));
            Assert.InRange(wordCount, 1, (spirv.Length - offset) / sizeof(uint));
            var operands = new uint[wordCount - 1];
            for (var operand = 0; operand < operands.Length; operand++)
            {
                operands[operand] = BinaryPrimitives.ReadUInt32LittleEndian(
                    spirv.AsSpan(offset + (operand + 1) * sizeof(uint)));
            }

            instructions.Add(
                new ParsedSpirvInstruction((SpirvOp)(ushort)instruction, operands));
            offset += wordCount * sizeof(uint);
        }

        return instructions;
    }

    private readonly record struct ParsedSpirvInstruction(
        SpirvOp Opcode,
        uint[] Operands);
}
