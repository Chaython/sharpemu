// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using Silk.NET.Vulkan;
using SharpEmu.Libs.VideoOut;
using Xunit;

namespace SharpEmu.Libs.Tests.VideoOut;

public sealed class VulkanGuestDepthBackingTests
{
    [Fact]
    public void SelectGuestDepthBackingFormat_UsesCombinedFormatOnlyWithSupport()
    {
        // The PS5 GPU's native DB layout is 32-bit float depth + 8-bit
        // stencil; a device that can attach and sample D32SfloatS8Uint
        // gets the combined backing so stencil writes have storage.
        Assert.Equal(
            Format.D32SfloatS8Uint,
            VulkanVideoPresenter.SelectGuestDepthBackingFormat(
                supportsCombinedDepthStencil: true));
        // Without device support the depth-only fallback keeps the
        // pre-stencil-plane behavior exactly.
        Assert.Equal(
            Format.D32Sfloat,
            VulkanVideoPresenter.SelectGuestDepthBackingFormat(
                supportsCombinedDepthStencil: false));
    }

    [Theory]
    [InlineData(Format.D32SfloatS8Uint, true)]
    [InlineData(Format.D32Sfloat, false)]
    // Depth/stencil formats the presenter never selects as backings must
    // not be mistaken for the guest depth backing.
    [InlineData(Format.D24UnormS8Uint, false)]
    [InlineData(Format.S8Uint, false)]
    [InlineData(Format.D16Unorm, false)]
    [InlineData(Format.Undefined, false)]
    [InlineData(Format.B8G8R8A8Unorm, false)]
    public void HasStencilPlane_OnlyTheCombinedBackingHasAStencilPlane(
        Format format,
        bool expected)
    {
        Assert.Equal(expected, VulkanVideoPresenter.HasStencilPlane(format));
    }

    [Fact]
    public void HasStencilPlane_AgreesWithSelectedBackingFormats()
    {
        // Round-trip: whichever way the device decision goes, HasStencilPlane
        // must describe the selected backing consistently.
        Assert.True(VulkanVideoPresenter.HasStencilPlane(
            VulkanVideoPresenter.SelectGuestDepthBackingFormat(true)));
        Assert.False(VulkanVideoPresenter.HasStencilPlane(
            VulkanVideoPresenter.SelectGuestDepthBackingFormat(false)));
    }

    [Fact]
    public void GuestDepthAspects_CoversBothAspectsForCombinedBacking()
    {
        // Layout transitions and attachment views of a combined
        // depth/stencil image must cover BOTH aspects; leaving the stencil
        // plane in its old layout would desynchronize the image state.
        Assert.Equal(
            ImageAspectFlags.DepthBit | ImageAspectFlags.StencilBit,
            VulkanVideoPresenter.GuestDepthAspects(Format.D32SfloatS8Uint));
    }

    [Fact]
    public void GuestDepthAspects_DepthOnlyForTheFallbackBacking()
    {
        // A depth-only backing has just the depth aspect — identical to the
        // aspect set used before the combined backing existed, so the
        // fallback path is byte-for-byte the legacy behavior.
        Assert.Equal(
            ImageAspectFlags.DepthBit,
            VulkanVideoPresenter.GuestDepthAspects(Format.D32Sfloat));
    }
}
