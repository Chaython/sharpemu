// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Buffers.Binary;
using SharpEmu.Libs.VideoOut;
using Xunit;

namespace SharpEmu.Libs.Tests.VideoOut;

public sealed class VulkanSpirvDescriptorCountTests
{
    private const ushort SpirvOpVariable = 59;

    // SPIR-V storage classes (same values the translator emits).
    private const uint StorageClassUniformConstant = 0;
    private const uint StorageClassUniform = 2;
    private const uint StorageClassFunction = 7;
    private const uint StorageClassPushConstant = 9;
    private const uint StorageClassStorageBuffer = 12;

    [Fact]
    public void CountsUniformConstantAndStorageBufferVariablesOnly()
    {
        // Images/samplers are OpVariable UniformConstant; guest global
        // buffers are OpVariable StorageBuffer. Function-private variables
        // and push constants are not descriptor bindings.
        var spirv = BuildSpirv(
            Variable(StorageClassUniformConstant),
            Variable(StorageClassFunction),
            Variable(StorageClassStorageBuffer),
            Variable(StorageClassStorageBuffer),
            Variable(StorageClassPushConstant),
            Variable(StorageClassUniform));

        Assert.True(VulkanVideoPresenter.TryCountSpirvDescriptorVariables(
            spirv,
            out var count,
            out var error));
        Assert.Equal(4u, count);
        Assert.Equal(string.Empty, error);
    }

    [Fact]
    public void ZeroDescriptorModuleCountsAsPureCompute()
    {
        // A module with only function/private variables declares zero
        // descriptors: a legitimate pure-ALU compute shader that must be
        // allowed through with nothing bound.
        var spirv = BuildSpirv(
            Variable(StorageClassFunction),
            Variable(StorageClassFunction));

        Assert.True(VulkanVideoPresenter.TryCountSpirvDescriptorVariables(
            spirv,
            out var count,
            out _));
        Assert.Equal(0u, count);
    }

    [Fact]
    public void EmptyModuleBodyCountsZeroDescriptors()
    {
        Assert.True(VulkanVideoPresenter.TryCountSpirvDescriptorVariables(
            BuildSpirv(),
            out var count,
            out _));
        Assert.Equal(0u, count);
    }

    [Fact]
    public void RejectsInvalidHeader()
    {
        var notSpirv = new byte[] { 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13, 14, 15, 16, 17, 18, 19, 20 };

        Assert.False(VulkanVideoPresenter.TryCountSpirvDescriptorVariables(
            notSpirv,
            out _,
            out var error));
        Assert.Equal("invalid-spirv-header", error);
    }

    [Fact]
    public void RejectsTruncatedInstructionBody()
    {
        // An instruction whose word count runs past the buffer must fail
        // closed (the caller then treats the module as declaring
        // descriptors and keeps the historical reject path).
        var spirv = BuildSpirv();
        // Header (5 words) + a bogus 4-word instruction with only its first
        // word present.
        var truncated = new byte[spirv.Length + 4];
        spirv.AsSpan().CopyTo(truncated);
        BinaryPrimitives.WriteUInt32LittleEndian(
            truncated.AsSpan(spirv.Length),
            (4u << 16) | SpirvOpVariable);

        Assert.False(VulkanVideoPresenter.TryCountSpirvDescriptorVariables(
            truncated,
            out _,
            out var error));
        Assert.Equal("invalid-spirv-instruction-size", error);
    }

    private static byte[] Variable(uint storageClass)
    {
        // OpVariable: [wordCount|op, resultType, resultId, storageClass]
        var words = new uint[4];
        words[0] = (4u << 16) | SpirvOpVariable;
        words[1] = 1; // result type
        words[2] = 2; // result id
        words[3] = storageClass;
        return words.SelectMany(BitConverter.GetBytes).ToArray();
    }

    private static byte[] BuildSpirv(params byte[][] instructions)
    {
        var words = new List<uint>
        {
            0x07230203u, // magic
            0x00010000u, // version 1.0
            0,           // generator
            32,          // bound
            0,           // schema
        };
        foreach (var instruction in instructions)
        {
            for (var offset = 0; offset < instruction.Length; offset += 4)
            {
                words.Add(BinaryPrimitives.ReadUInt32LittleEndian(
                    instruction.AsSpan(offset)));
            }
        }

        return words.SelectMany(BitConverter.GetBytes).ToArray();
    }
}
