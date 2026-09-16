// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.Libs.Gpu.Metal;
using Xunit;

namespace SharpEmu.Libs.Tests.Gpu;

public sealed class MetalStencilMappingTests
{
    [Fact]
    public void ToMetalStencilOperation_MapsGcnOpsAndDegradesUnknownToKeep()
    {
        // The GCN stencil-op encoding is identical to MTLStencilOperation:
        // 0=Keep, 1=Zero, 2=Replace, 3=IncrementClamp, 4=DecrementClamp,
        // 5=Invert, 6=IncrementWrap, 7=DecrementWrap.
        for (var op = 0u; op < 8u; op++)
        {
            Assert.Equal(op, MetalVideoPresenter.ToMetalStencilOperation(op));
        }

        // Unknown codes degrade to Keep, mirroring ToVkStencilOp.
        Assert.Equal(0u, MetalVideoPresenter.ToMetalStencilOperation(8));
        Assert.Equal(0u, MetalVideoPresenter.ToMetalStencilOperation(0xFFFFFFFFu));
    }

    [Fact]
    public void ToMetalSampleCount_MapsPowerOfTwoCounts()
    {
        // MTLSampleCount is the literal sample count; only 1/2/4/8 exist in
        // the guest encoding, anything else degrades to the 1x pipeline.
        Assert.Equal(1u, MetalVideoPresenter.ToMetalSampleCount(0));
        Assert.Equal(1u, MetalVideoPresenter.ToMetalSampleCount(1));
        Assert.Equal(2u, MetalVideoPresenter.ToMetalSampleCount(2));
        Assert.Equal(4u, MetalVideoPresenter.ToMetalSampleCount(4));
        Assert.Equal(8u, MetalVideoPresenter.ToMetalSampleCount(8));
        Assert.Equal(1u, MetalVideoPresenter.ToMetalSampleCount(3));
    }

    [Fact]
    public void ClampRenderTargetSamples_ClampsMultisampleRequestsToBacking()
    {
        // Every backing texture the Metal presenter allocates is 1x, so guest
        // multisample requests clamp to 1x (mirrors the Vulkan presenter);
        // the plumbing is complete for when multisample backings arrive.
        Assert.Equal(1u, MetalVideoPresenter.ClampRenderTargetSamples(1));
        Assert.Equal(1u, MetalVideoPresenter.ClampRenderTargetSamples(2));
        Assert.Equal(1u, MetalVideoPresenter.ClampRenderTargetSamples(4));
        Assert.Equal(1u, MetalVideoPresenter.ClampRenderTargetSamples(8));
    }
}
