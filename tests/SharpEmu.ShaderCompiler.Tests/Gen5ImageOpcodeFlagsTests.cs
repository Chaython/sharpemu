// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.ShaderCompiler;
using Xunit;

namespace SharpEmu.ShaderCompiler.Tests;

// ParseImageOpcodeFlags decomposes the MIMG sampling opcode names into their
// modifier flags. These suites walk every flag combination the gen5 ISA names,
// plus the rejections that used to be indistinguishable from ad-hoc substring
// matching (ImageSampleCl contains "SampleC" but is NOT a compare variant).
public sealed class Gen5ImageOpcodeFlagsTests
{
    [Theory]
    [InlineData("ImageSample", false, false, false, false, false, false, false, false)]
    [InlineData("ImageSampleCl", false, false, false, false, false, false, true, false)]
    [InlineData("ImageSampleD", false, false, false, false, false, true, false, false)]
    [InlineData("ImageSampleDCl", false, false, false, false, false, true, true, false)]
    [InlineData("ImageSampleL", false, false, false, false, true, false, false, false)]
    [InlineData("ImageSampleB", false, false, false, true, false, false, false, false)]
    [InlineData("ImageSampleBCl", false, false, false, true, false, false, true, false)]
    [InlineData("ImageSampleLz", false, false, false, false, false, false, false, true)]
    [InlineData("ImageSampleC", true, false, false, false, false, false, false, false)]
    [InlineData("ImageSampleCCl", true, false, false, false, false, false, true, false)]
    [InlineData("ImageSampleCD", true, false, false, false, false, true, false, false)]
    [InlineData("ImageSampleCDCl", true, false, false, false, false, true, true, false)]
    [InlineData("ImageSampleCL", true, false, false, false, true, false, false, false)]
    [InlineData("ImageSampleCB", true, false, false, true, false, false, false, false)]
    [InlineData("ImageSampleCBCl", true, false, false, true, false, false, true, false)]
    [InlineData("ImageSampleCLz", true, false, false, false, false, false, false, true)]
    [InlineData("ImageSampleO", false, false, true, false, false, false, false, false)]
    [InlineData("ImageSampleClO", false, false, true, false, false, false, true, false)]
    [InlineData("ImageSampleDO", false, false, true, false, false, true, false, false)]
    [InlineData("ImageSampleDClO", false, false, true, false, false, true, true, false)]
    [InlineData("ImageSampleLO", false, false, true, false, true, false, false, false)]
    [InlineData("ImageSampleBO", false, false, true, true, false, false, false, false)]
    [InlineData("ImageSampleBClO", false, false, true, true, false, false, true, false)]
    [InlineData("ImageSampleLzO", false, false, true, false, false, false, false, true)]
    [InlineData("ImageSampleCO", true, false, true, false, false, false, false, false)]
    [InlineData("ImageSampleCClO", true, false, true, false, false, false, true, false)]
    [InlineData("ImageSampleCDO", true, false, true, false, false, true, false, false)]
    [InlineData("ImageSampleCDClO", true, false, true, false, false, true, true, false)]
    [InlineData("ImageSampleCLO", true, false, true, false, true, false, false, false)]
    [InlineData("ImageSampleCBO", true, false, true, true, false, false, false, false)]
    [InlineData("ImageSampleCBClO", true, false, true, true, false, false, true, false)]
    [InlineData("ImageSampleCLzO", true, false, true, false, false, false, false, true)]
    public void SampleOpcodeFlagsParseAllCombinations(
        string opcode,
        bool compare,
        bool signedCompare,
        bool offset,
        bool bias,
        bool lod,
        bool derivatives,
        bool lodClamp,
        bool lodZero)
    {
        Assert.True(Gen5ShaderTranslator.ParseImageOpcodeFlags(opcode, out var flags));
        Assert.False(flags.Gather);
        Assert.Equal(compare, flags.Compare);
        Assert.Equal(signedCompare, flags.SignedCompare);
        Assert.Equal(offset, flags.Offset);
        Assert.Equal(bias, flags.Bias);
        Assert.Equal(lod, flags.Lod);
        Assert.Equal(derivatives, flags.Derivatives);
        Assert.Equal(lodClamp, flags.LodClamp);
        Assert.Equal(lodZero, flags.LodZero);
        Assert.Equal(compare, flags.UsesDepthReference);
    }

    [Theory]
    [InlineData("ImageGather4", false, false, false, false, false, false, false)]
    [InlineData("ImageGather4Cl", false, false, false, false, false, true, false)]
    [InlineData("ImageGather4L", false, false, false, true, false, false, false)]
    [InlineData("ImageGather4B", false, true, false, false, false, false, false)]
    [InlineData("ImageGather4BCl", false, true, false, false, false, true, false)]
    [InlineData("ImageGather4Lz", false, false, false, false, false, false, true)]
    [InlineData("ImageGather4C", true, false, false, false, false, false, false)]
    [InlineData("ImageGather4CCl", true, false, false, false, false, true, false)]
    [InlineData("ImageGather4CL", true, false, false, true, false, false, false)]
    [InlineData("ImageGather4CB", true, true, false, false, false, false, false)]
    [InlineData("ImageGather4CBCl", true, true, false, false, false, true, false)]
    [InlineData("ImageGather4CLz", true, false, false, false, false, false, true)]
    [InlineData("ImageGather4O", false, false, true, false, false, false, false)]
    [InlineData("ImageGather4ClO", false, false, true, false, false, true, false)]
    [InlineData("ImageGather4LO", false, false, true, true, false, false, false)]
    [InlineData("ImageGather4BO", false, true, true, false, false, false, false)]
    [InlineData("ImageGather4BClO", false, true, true, false, false, true, false)]
    [InlineData("ImageGather4LzO", false, false, true, false, false, false, true)]
    [InlineData("ImageGather4CO", true, false, true, false, false, false, false)]
    [InlineData("ImageGather4CClO", true, false, true, false, false, true, false)]
    [InlineData("ImageGather4CLO", true, false, true, true, false, false, false)]
    [InlineData("ImageGather4CBO", true, true, true, false, false, false, false)]
    [InlineData("ImageGather4CBClO", true, true, true, false, false, true, false)]
    [InlineData("ImageGather4CLzO", true, false, true, false, false, false, true)]
    public void GatherOpcodeFlagsParseAllCombinations(
        string opcode,
        bool compare,
        bool bias,
        bool offset,
        bool lod,
        bool derivatives,
        bool lodClamp,
        bool lodZero)
    {
        Assert.True(Gen5ShaderTranslator.ParseImageOpcodeFlags(opcode, out var flags));
        Assert.True(flags.Gather);
        Assert.Equal(compare, flags.Compare);
        Assert.Equal(offset, flags.Offset);
        Assert.Equal(bias, flags.Bias);
        Assert.Equal(lod, flags.Lod);
        Assert.Equal(derivatives, flags.Derivatives);
        Assert.Equal(lodClamp, flags.LodClamp);
        Assert.Equal(lodZero, flags.LodZero);
    }

    [Fact]
    public void SignedCompareTokenIsDecodedAfterCompare()
    {
        Assert.True(Gen5ShaderTranslator.ParseImageOpcodeFlags("ImageSampleCS", out var sample));
        Assert.True(sample.Compare);
        Assert.True(sample.SignedCompare);
        Assert.True(sample.UsesDepthReference);

        Assert.True(Gen5ShaderTranslator.ParseImageOpcodeFlags("ImageGather4CSO", out var gather));
        Assert.True(gather.Gather);
        Assert.True(gather.Compare);
        Assert.True(gather.SignedCompare);
        Assert.True(gather.Offset);

        // The S token is only valid directly after C.
        Assert.False(Gen5ShaderTranslator.ParseImageOpcodeFlags("ImageSampleS", out _));
        Assert.False(Gen5ShaderTranslator.ParseImageOpcodeFlags("ImageSampleOS", out _));
    }

    [Theory]
    [InlineData("ImageLoad")]
    [InlineData("ImageStoreMip")]
    [InlineData("ImageGetResinfo")]
    [InlineData("ImageAtomicAdd")]
    public void NonSamplingOpcodesAreRejected(string opcode)
    {
        Assert.False(Gen5ShaderTranslator.ParseImageOpcodeFlags(opcode, out _));
    }

    [Theory]
    [InlineData("ImageSampleX")]
    [InlineData("ImageSampleOC")]
    [InlineData("ImageGather4CD")]
    public void MalformedOrImpossibleNamesAreRejected(string opcode)
    {
        // Unknown token, offset out of order, and derivatives on a gather.
        Assert.False(Gen5ShaderTranslator.ParseImageOpcodeFlags(opcode, out _));
    }

    [Fact]
    public void EveryMimgSamplingOpcodeNameParses()
    {
        // Every sampled-image opcode name the decoder can produce, in opcode
        // order; each must tokenize without error.
        string[] names =
        [
            "ImageSample", "ImageSampleCl", "ImageSampleD", "ImageSampleDCl",
            "ImageSampleL", "ImageSampleB", "ImageSampleBCl", "ImageSampleLz",
            "ImageSampleC", "ImageSampleCCl", "ImageSampleCD", "ImageSampleCDCl",
            "ImageSampleCL", "ImageSampleCB", "ImageSampleCBCl", "ImageSampleCLz",
            "ImageSampleO", "ImageSampleClO", "ImageSampleDO", "ImageSampleDClO",
            "ImageSampleLO", "ImageSampleBO", "ImageSampleBClO", "ImageSampleLzO",
            "ImageSampleCO", "ImageSampleCClO", "ImageSampleCDO", "ImageSampleCDClO",
            "ImageSampleCLO", "ImageSampleCBO", "ImageSampleCBClO", "ImageSampleCLzO",
            "ImageGather4", "ImageGather4Cl", "ImageGather4L", "ImageGather4B",
            "ImageGather4BCl", "ImageGather4Lz", "ImageGather4C", "ImageGather4CCl",
            "ImageGather4CL", "ImageGather4CB", "ImageGather4CBCl", "ImageGather4CLz",
            "ImageGather4O", "ImageGather4ClO", "ImageGather4LO", "ImageGather4BO",
            "ImageGather4BClO", "ImageGather4LzO", "ImageGather4CO", "ImageGather4CClO",
            "ImageGather4CLO", "ImageGather4CBO", "ImageGather4CBClO", "ImageGather4CLzO",
        ];
        foreach (var name in names)
        {
            Assert.True(
                Gen5ShaderTranslator.ParseImageOpcodeFlags(name, out _),
                $"{name} must tokenize");
        }
    }
}
