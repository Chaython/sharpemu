// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.Libs.VideoOut;
using Silk.NET.Vulkan;
using Xunit;

namespace SharpEmu.Libs.Tests.VideoOut;

public sealed class VulkanDualSrcBlendTests
{
    [Theory]
    [InlineData(15u, BlendFactor.Src1Color)]
    [InlineData(16u, BlendFactor.OneMinusSrc1Color)]
    [InlineData(17u, BlendFactor.Src1Alpha)]
    [InlineData(18u, BlendFactor.OneMinusSrc1Alpha)]
    public void MapGuestBlendFactor_DualSourceFactorsMapToSrc1FamilyWhenSupported(
        uint factor,
        BlendFactor expected)
    {
        // With dualSrcBlend enabled the guest factors 15..18 map 1:1 to the
        // Vulkan Src1* enums.
        Assert.Equal(expected, VulkanVideoPresenter.MapGuestBlendFactor(factor, hasDualSrcBlend: true));
    }

    [Theory]
    [InlineData(15u, BlendFactor.SrcAlpha)]
    [InlineData(16u, BlendFactor.OneMinusSrcAlpha)]
    [InlineData(17u, BlendFactor.SrcAlpha)]
    [InlineData(18u, BlendFactor.OneMinusSrcAlpha)]
    public void MapGuestBlendFactor_DualSourceFactorsDegradeWithoutFeature(
        uint factor,
        BlendFactor expected)
    {
        // Without dualSrcBlend (MoltenVK entirely, strict drivers when the
        // feature was not enabled) the Src1* enums violate
        // VUID-VkPipelineColorBlendAttachmentState-dualSrcBlend-01506, so the
        // mapping degrades to the closest single-source equivalents instead
        // of failing pipeline creation.
        Assert.Equal(expected, VulkanVideoPresenter.MapGuestBlendFactor(factor, hasDualSrcBlend: false));
    }

    [Theory]
    [InlineData(0u, BlendFactor.Zero)]
    [InlineData(1u, BlendFactor.One)]
    [InlineData(2u, BlendFactor.SrcColor)]
    [InlineData(3u, BlendFactor.OneMinusSrcColor)]
    [InlineData(4u, BlendFactor.SrcAlpha)]
    [InlineData(5u, BlendFactor.OneMinusSrcAlpha)]
    [InlineData(6u, BlendFactor.DstAlpha)]
    [InlineData(7u, BlendFactor.OneMinusDstAlpha)]
    [InlineData(8u, BlendFactor.DstColor)]
    [InlineData(9u, BlendFactor.OneMinusDstColor)]
    [InlineData(10u, BlendFactor.SrcAlphaSaturate)]
    [InlineData(13u, BlendFactor.ConstantColor)]
    [InlineData(14u, BlendFactor.OneMinusConstantColor)]
    [InlineData(19u, BlendFactor.ConstantAlpha)]
    [InlineData(20u, BlendFactor.OneMinusConstantAlpha)]
    public void MapGuestBlendFactor_SingleSourceFactorsIgnoreFeatureFlag(
        uint factor,
        BlendFactor expected)
    {
        // Only the dual-source family depends on the feature; every other
        // code maps identically either way (unknown codes degrade to One).
        Assert.Equal(expected, VulkanVideoPresenter.MapGuestBlendFactor(factor, hasDualSrcBlend: true));
        Assert.Equal(expected, VulkanVideoPresenter.MapGuestBlendFactor(factor, hasDualSrcBlend: false));
    }

    [Fact]
    public void MapGuestBlendFactor_UnknownCodesDegradeToOne()
    {
        Assert.Equal(BlendFactor.One, VulkanVideoPresenter.MapGuestBlendFactor(11u, hasDualSrcBlend: true));
        Assert.Equal(BlendFactor.One, VulkanVideoPresenter.MapGuestBlendFactor(uint.MaxValue, hasDualSrcBlend: false));
    }
}
