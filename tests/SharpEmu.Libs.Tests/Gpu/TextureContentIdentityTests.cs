// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.Libs.Gpu;
using Xunit;

namespace SharpEmu.Libs.Tests.Gpu;

public sealed class TextureContentIdentityTests
{
    [Fact]
    public void SamplerIsExcludedFromContentIdentity()
    {
        // The same texture sampled with point and linear filters is ONE
        // cached image: every backend resolves samplers separately from the
        // per-draw descriptor, so the sampler must not split the texture
        // cache key (it used to duplicate the VkImage/MTLTexture + upload).
        var point = CreateIdentity(new GuestSampler(0x01020304, 0, 0, 0));
        var linear = CreateIdentity(new GuestSampler(0x05060708, 0, 0, 0));

        Assert.Equal(point, linear);
        Assert.True(point == linear);
        Assert.Equal(point.GetHashCode(), linear.GetHashCode());
    }

    [Fact]
    public void SamplerStillRidesOnTheIdentityForCallersThatNeedIt()
    {
        var point = CreateIdentity(new GuestSampler(0x01020304, 0, 0, 0));
        var linear = CreateIdentity(new GuestSampler(0x05060708, 0, 0, 0));

        // The field is still carried (AgcExports builds identities from the
        // live descriptor), it just does not participate in equality.
        Assert.NotEqual(point.Sampler, linear.Sampler);
        Assert.Equal(0x05060708u, linear.Sampler.Word0);
    }

    [Fact]
    public void ContentFieldsStillDriveEquality()
    {
        var baseIdentity = CreateIdentity(new GuestSampler(1, 2, 3, 4));

        // Format, dimensions, pitch and tile mode all affect the decoded
        // texels and must keep splitting the cache.
        Assert.NotEqual(baseIdentity, baseIdentity with { Format = 11 });
        Assert.NotEqual(baseIdentity, baseIdentity with { Width = 9 });
        Assert.NotEqual(baseIdentity, baseIdentity with { Height = 9 });
        Assert.NotEqual(baseIdentity, baseIdentity with { Pitch = 16 });
        Assert.NotEqual(baseIdentity, baseIdentity with { TileMode = 2 });
        Assert.NotEqual(baseIdentity, baseIdentity with { NumberType = 7 });
        Assert.NotEqual(baseIdentity, baseIdentity with { DstSelect = 0x123 });
        Assert.NotEqual(baseIdentity, baseIdentity with { Type = 10 });
        Assert.NotEqual(baseIdentity, baseIdentity with { Depth = 4 });
        Assert.NotEqual(baseIdentity, baseIdentity with { Arrayed = true });
        Assert.NotEqual(baseIdentity, baseIdentity with { ArrayLayers = 6 });

        // Same content, different sampler: still the same identity.
        Assert.Equal(baseIdentity, baseIdentity with
        {
            Sampler = new GuestSampler(0xFFFFFFFF, 0, 0, 0),
        });
    }

    [Fact]
    public void IdentityWorksAsADictionaryKeyAcrossSamplers()
    {
        var cache = new Dictionary<TextureContentIdentity, int>();
        var point = CreateIdentity(new GuestSampler(0x01020304, 0, 0, 0));
        var linear = CreateIdentity(new GuestSampler(0x05060708, 0, 0, 0));

        cache[point] = 42;
        Assert.True(cache.TryGetValue(linear, out var value));
        Assert.Equal(42, value);
        Assert.Single(cache);
    }

    private static TextureContentIdentity CreateIdentity(GuestSampler sampler) =>
        new(
            Address: 0x1000,
            Width: 64,
            Height: 64,
            Format: 10,
            NumberType: 0,
            DstSelect: 0xFAC,
            TileMode: 0,
            Pitch: 64,
            Sampler: sampler,
            Arrayed: false,
            ArrayLayers: 1,
            Type: 9,
            Depth: 1);
}
