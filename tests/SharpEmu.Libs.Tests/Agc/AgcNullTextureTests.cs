// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Reflection;
using SharpEmu.Libs.Agc;
using SharpEmu.Libs.Gpu;
using Xunit;

namespace SharpEmu.Libs.Tests.Agc;

public sealed class AgcNullTextureTests
{
    [Theory]
    [InlineData(3u, 0u)] // Invalid descriptor decoded as R8 SSCALED.
    [InlineData(4u, 4u)]
    [InlineData(5u, 5u)]
    public void InvalidTextureTypeUsesSampleablePlaceholder(uint numberType, uint expectedNumberType)
    {
        var create = typeof(AgcExports).GetMethod("CreateFallbackGuestDrawTexture",
            BindingFlags.Static | BindingFlags.NonPublic)!;
        var texture = Assert.IsType<GuestDrawTexture>(create.Invoke(null,
            [false, 1u, numberType, false, 0u, 1u]));

        Assert.True(texture.IsFallback);
        Assert.Equal(0UL, texture.Address);
        Assert.Equal(9u, texture.Type); // 2D instead of invalid descriptor type zero.
        Assert.Equal(10u, texture.Format); // Four bytes matching the placeholder texel.
        Assert.Equal(expectedNumberType, texture.NumberType);
        Assert.Equal(4, texture.RgbaPixels.Length);
    }
}
