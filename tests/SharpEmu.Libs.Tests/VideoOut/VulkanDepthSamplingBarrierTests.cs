// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.Libs.VideoOut;
using Silk.NET.Vulkan;
using Xunit;

namespace SharpEmu.Libs.Tests.VideoOut;

public sealed class VulkanDepthSamplingBarrierTests
{
    [Fact]
    public void AttachmentLayoutWaitsOnEarlyAndLateDepthReadsAndWrites()
    {
        // Blending/early-Z READS the depth attachment as well as writing it:
        // the attachment -> shader-read transition must make BOTH
        // DepthStencilAttachmentRead and DepthStencilAttachmentWrite available
        // and wait on EarlyFragmentTests as well as LateFragmentTests (the old
        // barrier only carried the write bit / late stage, so an early-Z read
        // could sample the depth buffer before the writes it depends on were
        // visible).
        var (srcAccess, srcStage) = VulkanVideoPresenter.GetDepthToSampledBarrierSource(
            ImageLayout.DepthStencilAttachmentOptimal);

        Assert.Equal(
            AccessFlags.DepthStencilAttachmentReadBit |
            AccessFlags.DepthStencilAttachmentWriteBit,
            srcAccess);
        Assert.Equal(
            PipelineStageFlags.EarlyFragmentTestsBit |
            PipelineStageFlags.LateFragmentTestsBit,
            srcStage);
    }

    [Fact]
    public void UndefinedLayoutUsesTheAttachmentScopeToo()
    {
        // A depth image that was never cleared/rendered to still transitions
        // through the attachment scope (the non-transfer branch); only an
        // explicit TransferDstOptimal layout — set by the in-function clear —
        // takes the transfer scope.
        var (srcAccess, srcStage) = VulkanVideoPresenter.GetDepthToSampledBarrierSource(
            ImageLayout.Undefined);

        Assert.Equal(
            AccessFlags.DepthStencilAttachmentReadBit |
            AccessFlags.DepthStencilAttachmentWriteBit,
            srcAccess);
        Assert.Equal(
            PipelineStageFlags.EarlyFragmentTestsBit |
            PipelineStageFlags.LateFragmentTestsBit,
            srcStage);
    }

    [Fact]
    public void TransferDstLayoutKeepsTransferWriteAndStage()
    {
        // The first-use path clears the depth image to TransferDstOptimal
        // inside RecordGuestDepthForSampling; that branch is already correct:
        // only the transfer write needs to be made visible, sourced from the
        // transfer stage.
        var (srcAccess, srcStage) = VulkanVideoPresenter.GetDepthToSampledBarrierSource(
            ImageLayout.TransferDstOptimal);

        Assert.Equal(AccessFlags.TransferWriteBit, srcAccess);
        Assert.Equal(PipelineStageFlags.TransferBit, srcStage);
    }
}
