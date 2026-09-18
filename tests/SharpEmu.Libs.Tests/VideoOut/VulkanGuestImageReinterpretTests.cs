// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.Libs.VideoOut;
using Xunit;

namespace SharpEmu.Libs.Tests.VideoOut;

public sealed class VulkanGuestImageReinterpretTests
{
    [Fact]
    public void MatchesGuestImageLogicalExtent_ScaleTwoBackingMatchesLogicalTarget()
    {
        // SHARPEMU_RENDER_SCALE=2.0: the existing image's physical backing is
        // 2Wx2H while its logical extent stays WxH; the reinterpret fast path
        // must compare logical dims (the old physical-vs-logical comparison
        // never matched and every reinterpret fell to a full recreate).
        const uint width = 1280;
        const uint height = 720;

        Assert.True(VulkanVideoPresenter.MatchesGuestImageLogicalExtent(
            existingLogicalWidth: width,
            existingLogicalHeight: height,
            existingMipLevels: 1,
            targetWidth: width,
            targetHeight: height,
            targetMipLevels: 1));
    }

    [Fact]
    public void MatchesGuestImageLogicalExtent_ScaleOneBackingStillMatches()
    {
        // At scale 1.0 the logical and physical extents agree, so the fixed
        // comparison keeps matching.
        Assert.True(VulkanVideoPresenter.MatchesGuestImageLogicalExtent(
            existingLogicalWidth: 640,
            existingLogicalHeight: 360,
            existingMipLevels: 3,
            targetWidth: 640,
            targetHeight: 360,
            targetMipLevels: 3));
    }

    [Fact]
    public void MatchesGuestImageLogicalExtent_RejectsLogicalMismatch()
    {
        Assert.False(VulkanVideoPresenter.MatchesGuestImageLogicalExtent(
            existingLogicalWidth: 1280,
            existingLogicalHeight: 720,
            existingMipLevels: 1,
            targetWidth: 1280,
            targetHeight: 719,
            targetMipLevels: 1));
        Assert.False(VulkanVideoPresenter.MatchesGuestImageLogicalExtent(
            existingLogicalWidth: 1280,
            existingLogicalHeight: 720,
            existingMipLevels: 1,
            targetWidth: 1279,
            targetHeight: 720,
            targetMipLevels: 1));
    }

    [Fact]
    public void MatchesGuestImageLogicalExtent_RejectsMipChainMismatch()
    {
        // A different mip chain needs different views/barriers; keep the
        // recreate path for it.
        Assert.False(VulkanVideoPresenter.MatchesGuestImageLogicalExtent(
            existingLogicalWidth: 1280,
            existingLogicalHeight: 720,
            existingMipLevels: 2,
            targetWidth: 1280,
            targetHeight: 720,
            targetMipLevels: 3));
    }
}
