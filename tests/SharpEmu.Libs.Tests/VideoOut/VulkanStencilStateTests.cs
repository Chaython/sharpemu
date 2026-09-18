// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.Libs.Gpu;
using SharpEmu.Libs.VideoOut;
using Silk.NET.Vulkan;
using Xunit;

namespace SharpEmu.Libs.Tests.VideoOut;

public sealed class VulkanStencilStateTests
{
    [Fact]
    public void ToVkStencilOp_MapsGcnOpsAndDegradesUnknownToKeep()
    {
        // The GCN DB_DEPTH_CONTROL stencil-op encoding is identical to the
        // Vulkan StencilOp values.
        Assert.Equal(StencilOp.Keep, VulkanVideoPresenter.ToVkStencilOp(0));
        Assert.Equal(StencilOp.Zero, VulkanVideoPresenter.ToVkStencilOp(1));
        Assert.Equal(StencilOp.Replace, VulkanVideoPresenter.ToVkStencilOp(2));
        Assert.Equal(StencilOp.IncrementAndClamp, VulkanVideoPresenter.ToVkStencilOp(3));
        Assert.Equal(StencilOp.DecrementAndClamp, VulkanVideoPresenter.ToVkStencilOp(4));
        Assert.Equal(StencilOp.Invert, VulkanVideoPresenter.ToVkStencilOp(5));
        Assert.Equal(StencilOp.IncrementAndWrap, VulkanVideoPresenter.ToVkStencilOp(6));
        Assert.Equal(StencilOp.DecrementAndWrap, VulkanVideoPresenter.ToVkStencilOp(7));
        // Unknown codes degrade to Keep, a no-op that cannot corrupt stencil.
        Assert.Equal(StencilOp.Keep, VulkanVideoPresenter.ToVkStencilOp(8));
        Assert.Equal(StencilOp.Keep, VulkanVideoPresenter.ToVkStencilOp(0xFFFFFFFFu));
    }

    [Fact]
    public void ToVkStencilOpState_MapsGuestFaceAndKeepsRefMasksDynamic()
    {
        var face = new GuestStencilFace(
            CompareFunc: 1,
            FailOp: 2,
            DepthFailOp: 3,
            PassOp: 4,
            Reference: 0x2A,
            CompareMask: 0x37,
            WriteMask: 0x59);
        var opState = VulkanVideoPresenter.ToVkStencilOpState(face);

        // Funcs/ops map through the shared compare-op / stencil-op orderings.
        Assert.Equal(CompareOp.Less, opState.CompareOp);
        Assert.Equal(StencilOp.Replace, opState.FailOp);
        Assert.Equal(StencilOp.IncrementAndClamp, opState.DepthFailOp);
        Assert.Equal(StencilOp.DecrementAndClamp, opState.PassOp);
        // Reference and compare/write masks are bound as DYNAMIC state
        // (vkCmdSetStencilCompareMask/WriteMask/Reference), so the pipeline
        // copies deliberately stay 0 — changing them must not rebuild a
        // pipeline.
        Assert.Equal(0u, opState.CompareMask);
        Assert.Equal(0u, opState.WriteMask);
        Assert.Equal(0u, opState.Reference);
    }

    [Fact]
    public void EffectiveStencilTestEnable_RequiresAttachmentAndGuestEnable()
    {
        var enabled = GuestStencilState.Default with { TestEnable = true };
        var disabled = GuestStencilState.Default;

        // Both the guest's STENCIL_ENABLE and a bound DB attachment are
        // required (stencil test without an attachment is invalid Vulkan).
        Assert.True(VulkanVideoPresenter.EffectiveStencilTestEnable(true, enabled));
        Assert.False(VulkanVideoPresenter.EffectiveStencilTestEnable(true, disabled));
        Assert.False(VulkanVideoPresenter.EffectiveStencilTestEnable(false, enabled));
    }

    [Fact]
    public void ShouldAttachGuestDepth_AttachesForStencilOnlyDraw()
    {
        // Depth test and write both off: only the guest stencil test needs
        // the DB attachment (stencil ops run against the same surface).
        var depthState = new GuestDepthState(false, false, CompareOp: 7);
        var stencilState = GuestStencilState.Default with { TestEnable = true };

        Assert.True(VulkanVideoPresenter.ShouldAttachGuestDepth(
            StencilTarget,
            depthState,
            stencilState));
        Assert.False(VulkanVideoPresenter.ShouldAttachGuestDepth(
            null,
            depthState,
            stencilState));
        Assert.False(VulkanVideoPresenter.ShouldAttachGuestDepth(
            StencilTarget,
            depthState));
    }

    [Fact]
    public void ToVkSampleCount_MapsPowerOfTwoCounts()
    {
        Assert.Equal(SampleCountFlags.Count1Bit, VulkanVideoPresenter.ToVkSampleCount(1));
        Assert.Equal(SampleCountFlags.Count2Bit, VulkanVideoPresenter.ToVkSampleCount(2));
        Assert.Equal(SampleCountFlags.Count4Bit, VulkanVideoPresenter.ToVkSampleCount(4));
        Assert.Equal(SampleCountFlags.Count8Bit, VulkanVideoPresenter.ToVkSampleCount(8));
        // Only 1/2/4/8 exist in the guest encoding; anything else is 1x.
        Assert.Equal(SampleCountFlags.Count1Bit, VulkanVideoPresenter.ToVkSampleCount(0));
        Assert.Equal(SampleCountFlags.Count1Bit, VulkanVideoPresenter.ToVkSampleCount(3));
    }

    [Fact]
    public void ClampRenderTargetSamples_ClampsMultisampleRequestsToBacking()
    {
        // Every attachment backing the presenter allocates is 1x, so guest
        // multisample requests clamp to 1x; the plumbing is complete for when
        // multisample backings arrive.
        Assert.Equal(1u, VulkanVideoPresenter.ClampRenderTargetSamples(1));
        Assert.Equal(1u, VulkanVideoPresenter.ClampRenderTargetSamples(2));
        Assert.Equal(1u, VulkanVideoPresenter.ClampRenderTargetSamples(4));
        Assert.Equal(1u, VulkanVideoPresenter.ClampRenderTargetSamples(8));
    }

    [Fact]
    public void GetGuestRenderTargetSampleCount_TakesMaximumAcrossTargets()
    {
        GuestRenderTarget[] targets =
        [
            new(0x1000, 64, 64, 10, 0, SampleCount: 1),
            new(0x2000, 64, 64, 10, 0, SampleCount: 4),
            new(0x3000, 64, 64, 10, 0, SampleCount: 2),
        ];

        Assert.Equal(4u, VulkanVideoPresenter.GetGuestRenderTargetSampleCount(targets));
        Assert.Equal(1u, VulkanVideoPresenter.GetGuestRenderTargetSampleCount([]));
        Assert.Equal(8u, VulkanVideoPresenter.GetGuestRenderTargetSampleCount(
            [new(0x4000, 64, 64, 10, 0, SampleCount: 8)]));
    }

    private static readonly GuestDepthTarget StencilTarget = new(
        ReadAddress: 0x1000,
        WriteAddress: 0x1000,
        Width: 1920,
        Height: 1080,
        GuestFormat: 1,
        SwizzleMode: 0,
        ClearDepth: 1f,
        ReadOnly: false);
}
