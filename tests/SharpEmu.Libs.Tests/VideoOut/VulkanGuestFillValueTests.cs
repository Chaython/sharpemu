// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.Libs.VideoOut;
using Silk.NET.Vulkan;
using Xunit;

namespace SharpEmu.Libs.Tests.VideoOut;

public sealed class VulkanGuestFillValueTests
{
    [Fact]
    public void UnpackGuestFillValue_Unorm8KeepsByteOver255Expansion()
    {
        // The classic CP DMA fill: four UNORM bytes packed little-endian
        // (R in bits 0..7), matching UnpackMetaClearValue's channel order.
        var value = VulkanVideoPresenter.UnpackGuestFillValue(0xFF804020u, Format.R8G8B8A8Unorm);

        Assert.Equal(0x20 / 255f, value.Float32_0);
        Assert.Equal(0x40 / 255f, value.Float32_1);
        Assert.Equal(0x80 / 255f, value.Float32_2);
        Assert.Equal(0xFF / 255f, value.Float32_3);
    }

    [Fact]
    public void UnpackGuestFillValue_SrgbTargetsUseTheSameByteExpansion()
    {
        var value = VulkanVideoPresenter.UnpackGuestFillValue(0xFF804020u, Format.R8G8B8A8Srgb);

        Assert.Equal(0x20 / 255f, value.Float32_0);
        Assert.Equal(0xFF / 255f, value.Float32_3);
    }

    [Fact]
    public void UnpackGuestFillValue_UintTargetsTakeRawComponents()
    {
        // Integer clears read the uint member of the ClearColorValue union;
        // scaling the bytes to 0..1 floats would clear to garbage.
        var rgba8 = VulkanVideoPresenter.UnpackGuestFillValue(0xFF804020u, Format.R8G8B8A8Uint);

        Assert.Equal(0x20u, rgba8.Uint32_0);
        Assert.Equal(0x40u, rgba8.Uint32_1);
        Assert.Equal(0x80u, rgba8.Uint32_2);
        Assert.Equal(0xFFu, rgba8.Uint32_3);

        // A 32-bit integer channel IS the fill dword.
        var r32 = VulkanVideoPresenter.UnpackGuestFillValue(0xDEADBEEFu, Format.R32Uint);
        Assert.Equal(0xDEADBEEFu, r32.Uint32_0);
        Assert.Equal(0u, r32.Uint32_1);

        var rgba32 = VulkanVideoPresenter.UnpackGuestFillValue(0xDEADBEEFu, Format.R32G32B32A32Uint);
        Assert.Equal(0xDEADBEEFu, rgba32.Uint32_0);
        Assert.Equal(0xDEADBEEFu, rgba32.Uint32_1);
        Assert.Equal(0xDEADBEEFu, rgba32.Uint32_2);
        Assert.Equal(0xDEADBEEFu, rgba32.Uint32_3);
    }

    [Fact]
    public void UnpackGuestFillValue_SintTargetsSignExtendRawComponents()
    {
        var value = VulkanVideoPresenter.UnpackGuestFillValue(0xFF804020u, Format.R8G8B8A8Sint);

        // 8-bit signed channels sign-extend into the 32-bit union member.
        Assert.Equal(0x20u, value.Uint32_0);
        Assert.Equal(0x40u, value.Uint32_1);
        Assert.Equal(0xFFFFFF80u, value.Uint32_2);
        Assert.Equal(0xFFFFFFFFu, value.Uint32_3);

        // A negative 32-bit channel keeps its raw two's-complement bits.
        var r32 = VulkanVideoPresenter.UnpackGuestFillValue(0xFFFFFFFFu, Format.R32Sint);
        Assert.Equal(0xFFFFFFFFu, r32.Uint32_0);
    }

    [Fact]
    public void UnpackGuestFillValue_HalfFloatTargetsUnpackPackedF16Pairs()
    {
        // Each dword of the filled surface carries the pattern, so a 4-channel
        // 16F texel sees the low half in R/B and the high half in G/A.
        var value = VulkanVideoPresenter.UnpackGuestFillValue(0xBC004000u, Format.R16G16B16A16Sfloat);

        Assert.Equal(2.0f, value.Float32_0);          // 0x4000 = 2.0f16
        Assert.Equal(-1.0f, value.Float32_1);         // 0xBC00 = -1.0f16
        Assert.Equal(2.0f, value.Float32_2);
        Assert.Equal(-1.0f, value.Float32_3);
    }

    [Fact]
    public void UnpackGuestFillValue_Float32TargetsUseRawBits()
    {
        var value = VulkanVideoPresenter.UnpackGuestFillValue(
            unchecked((uint)BitConverter.SingleToInt32Bits(0.5f)),
            Format.R32G32B32A32Sfloat);

        Assert.Equal(0.5f, value.Float32_0);
        Assert.Equal(0.5f, value.Float32_1);
        Assert.Equal(0.5f, value.Float32_2);
        Assert.Equal(0.5f, value.Float32_3);

        var r32 = VulkanVideoPresenter.UnpackGuestFillValue(
            unchecked((uint)BitConverter.SingleToInt32Bits(-2.5f)),
            Format.R32Sfloat);
        Assert.Equal(-2.5f, r32.Float32_0);
    }

    [Fact]
    public void UnpackGuestFillValue_Unorm16ScalesByComponentDepth()
    {
        // 16-bit UNORM channels divide by 65535, not 255.
        var value = VulkanVideoPresenter.UnpackGuestFillValue(0xFFFF0000u, Format.R16G16B16A16Unorm);

        Assert.Equal(0f, value.Float32_0);
        Assert.Equal(1f, value.Float32_1);
        Assert.Equal(0f, value.Float32_2);
        Assert.Equal(1f, value.Float32_3);
    }

    [Fact]
    public void UnpackGuestFillValue_Packed1010102UnpacksExactComponentPositions()
    {
        // A2B10G10R10 packs R in bits 0..9, G in 10..19, B in 20..29 and the
        // 2-bit alpha in 30..31 (the old byte/255 expansion was wrong here).
        var value = VulkanVideoPresenter.UnpackGuestFillValue(0xFFFFFFFFu, Format.A2B10G10R10UnormPack32);

        Assert.Equal(1f, value.Float32_0);
        Assert.Equal(1f, value.Float32_1);
        Assert.Equal(1f, value.Float32_2);
        Assert.Equal(1f, value.Float32_3);

        var half = VulkanVideoPresenter.UnpackGuestFillValue(0x000001FFu, Format.A2B10G10R10UnormPack32);
        Assert.Equal(0x1FF / 1023f, half.Float32_0);
        Assert.Equal(0f, half.Float32_1);
        Assert.Equal(0f, half.Float32_2);
        Assert.Equal(0f, half.Float32_3);
    }

    [Fact]
    public void UnpackGuestFillValue_PackedFloat111110UnpacksExponents()
    {
        // B10G11R11: 1.0 in every channel is E=15/M=0 per component
        // (R 11-bit in 0..10, G 11-bit in 11..21, B 10-bit in 22..31:
        // R=0x3C0, G=0x3C0<<11, B=0x1E0<<22).
        var value = VulkanVideoPresenter.UnpackGuestFillValue(0x781E03C0u, Format.B10G11R11UfloatPack32);

        Assert.Equal(1f, value.Float32_0);
        Assert.Equal(1f, value.Float32_1);
        Assert.Equal(1f, value.Float32_2);
        // Alpha does not exist in the format; a neutral 1 keeps packed-alpha
        // conventions.
        Assert.Equal(1f, value.Float32_3);

        var zero = VulkanVideoPresenter.UnpackGuestFillValue(0u, Format.B10G11R11UfloatPack32);
        Assert.Equal(0f, zero.Float32_0);
        Assert.Equal(0f, zero.Float32_1);
        Assert.Equal(0f, zero.Float32_2);
    }

    [Fact]
    public void UnpackGuestFillValue_UnknownFormatsKeepHistoricalUnorm8Fallback()
    {
        // Formats without an explicit case (e.g. SNORM, which no title fills
        // through) degrade to the historical byte/255 expansion rather than
        // guessing a layout.
        var value = VulkanVideoPresenter.UnpackGuestFillValue(0xFF804020u, Format.R8G8B8A8SNorm);

        Assert.Equal(0x20 / 255f, value.Float32_0);
        Assert.Equal(0x40 / 255f, value.Float32_1);
        Assert.Equal(0x80 / 255f, value.Float32_2);
        Assert.Equal(0xFF / 255f, value.Float32_3);
    }
}
