// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.Libs.Agc;
using SharpEmu.Libs.Gpu;
using Xunit;

namespace SharpEmu.Libs.Tests.Agc;

public sealed class AgcStencilDecodeTests
{
    private const uint DbDepthControl = 0x200;
    private const uint DbStencilRefMask = 0x10C;
    private const uint DbStencilRefMaskBf = 0x10D;

    // DB_DEPTH_CONTROL stencil layout (GFX10): STENCIL_ENABLE bit0,
    // BACKFACE_ENABLE bit7, front func bits[10:8], back func bits[13:11],
    // front ops [16:14]/[19:17]/[22:20], back ops [25:23]/[28:26]/[31:29].
    private static uint StencilControl(
        bool enable,
        bool backface,
        uint frontFunc = 0,
        uint backFunc = 0,
        uint frontFail = 0,
        uint frontDepthFail = 0,
        uint frontPass = 0,
        uint backFail = 0,
        uint backDepthFail = 0,
        uint backPass = 0) =>
        (enable ? 1u : 0u) |
        (backface ? 1u << 7 : 0u) |
        ((frontFunc & 0x7u) << 8) |
        ((backFunc & 0x7u) << 11) |
        ((frontFail & 0x7u) << 14) |
        ((frontDepthFail & 0x7u) << 17) |
        ((frontPass & 0x7u) << 20) |
        ((backFail & 0x7u) << 23) |
        ((backDepthFail & 0x7u) << 26) |
        ((backPass & 0x7u) << 29);

    [Fact]
    public void StencilState_DecodesDisabledWithoutRegisters()
    {
        var state = AgcExports.DecodeStencilState(new Dictionary<uint, uint>());

        Assert.False(state.TestEnable);
        Assert.False(state.BackfaceEnable);
        // No DB_DEPTH_CONTROL: the func keeps the Always default and every op
        // decodes to Keep; the absent DB_STENCILREFMASK registers fall back to
        // ref 0 with full compare/write masks.
        Assert.Equal(GuestStencilFace.Default.CompareFunc, state.Front.CompareFunc);
        Assert.Equal(0u, state.Front.FailOp);
        Assert.Equal(0u, state.Front.DepthFailOp);
        Assert.Equal(0u, state.Front.PassOp);
        Assert.Equal(0u, state.Front.Reference);
        Assert.Equal(0xFFu, state.Front.CompareMask);
        Assert.Equal(0xFFu, state.Front.WriteMask);
        Assert.Equal(GuestStencilState.Default, state);
    }

    [Fact]
    public void StencilState_DecodesEnableAndBackfaceBits()
    {
        var enabled = AgcExports.DecodeStencilState(
            new Dictionary<uint, uint> { [DbDepthControl] = StencilControl(enable: true, backface: false) });
        Assert.True(enabled.TestEnable);
        Assert.False(enabled.BackfaceEnable);

        var withBackface = AgcExports.DecodeStencilState(
            new Dictionary<uint, uint> { [DbDepthControl] = StencilControl(enable: true, backface: true) });
        Assert.True(withBackface.TestEnable);
        Assert.True(withBackface.BackfaceEnable);
    }

    [Fact]
    public void StencilState_DecodesFrontFunc()
    {
        var state = AgcExports.DecodeStencilState(
            new Dictionary<uint, uint>
            {
                [DbDepthControl] = StencilControl(enable: true, backface: false, frontFunc: 5),
            });

        Assert.True(state.TestEnable);
        Assert.Equal(5u, state.Front.CompareFunc);
    }

    [Fact]
    public void StencilState_DecodesBackFuncIndependently()
    {
        var state = AgcExports.DecodeStencilState(
            new Dictionary<uint, uint>
            {
                [DbDepthControl] = StencilControl(
                    enable: true,
                    backface: true,
                    frontFunc: 5,
                    backFunc: 3),
            });

        Assert.True(state.BackfaceEnable);
        Assert.Equal(5u, state.Front.CompareFunc);
        Assert.Equal(3u, state.Back.CompareFunc);
    }

    [Fact]
    public void StencilState_DecodesFrontOps()
    {
        var state = AgcExports.DecodeStencilState(
            new Dictionary<uint, uint>
            {
                [DbDepthControl] = StencilControl(
                    enable: true,
                    backface: false,
                    frontFail: 2,
                    frontDepthFail: 4,
                    frontPass: 6),
            });

        // 2=Replace, 4=DecrementClamp, 6=IncrementWrap (guest op encoding
        // matches the Vulkan StencilOp / MTLStencilOperation ordering).
        Assert.Equal(2u, state.Front.FailOp);
        Assert.Equal(4u, state.Front.DepthFailOp);
        Assert.Equal(6u, state.Front.PassOp);
    }

    [Fact]
    public void StencilState_DecodesBackOps()
    {
        var state = AgcExports.DecodeStencilState(
            new Dictionary<uint, uint>
            {
                [DbDepthControl] = StencilControl(
                    enable: true,
                    backface: true,
                    backFail: 1,
                    backDepthFail: 5,
                    backPass: 7),
            });

        // 1=Zero, 5=Invert, 7=DecrementWrap.
        Assert.Equal(1u, state.Back.FailOp);
        Assert.Equal(5u, state.Back.DepthFailOp);
        Assert.Equal(7u, state.Back.PassOp);
    }

    [Fact]
    public void StencilState_DecodesReferenceAndMasks()
    {
        // DB_STENCILREFMASK: STENCILREF [7:0], STENCILMASK [15:8],
        // STENCILWRITEMASK [23:16].
        var state = AgcExports.DecodeStencilState(
            new Dictionary<uint, uint>
            {
                [DbDepthControl] = StencilControl(enable: true, backface: false),
                [DbStencilRefMask] = 0x59372Au,
            });

        Assert.Equal(0x2Au, state.Front.Reference);
        Assert.Equal(0x37u, state.Front.CompareMask);
        Assert.Equal(0x59u, state.Front.WriteMask);
    }

    [Fact]
    public void StencilState_DecodesBackReferenceAndMasks()
    {
        var state = AgcExports.DecodeStencilState(
            new Dictionary<uint, uint>
            {
                [DbDepthControl] = StencilControl(enable: true, backface: true),
                [DbStencilRefMask] = 0x59372Au,
                [DbStencilRefMaskBf] = 0x442211u,
            });

        Assert.Equal(0x2Au, state.Front.Reference);
        Assert.Equal(0x37u, state.Front.CompareMask);
        Assert.Equal(0x59u, state.Front.WriteMask);
        Assert.Equal(0x11u, state.Back.Reference);
        Assert.Equal(0x22u, state.Back.CompareMask);
        Assert.Equal(0x44u, state.Back.WriteMask);
    }

    [Fact]
    public void StencilState_MissingRefMaskRegistersUseFullMasks()
    {
        var state = AgcExports.DecodeStencilState(
            new Dictionary<uint, uint>
            {
                [DbDepthControl] = StencilControl(enable: true, backface: true),
            });

        // The hardware-reset DB_STENCILREFMASK value is 0; a guest enabling
        // stencil without programming the registers still gets a testable
        // pass-through pair (ref 0, full masks) instead of a mask of 0.
        Assert.Equal(0u, state.Front.Reference);
        Assert.Equal(0xFFu, state.Front.CompareMask);
        Assert.Equal(0xFFu, state.Front.WriteMask);
        Assert.Equal(0u, state.Back.Reference);
        Assert.Equal(0xFFu, state.Back.CompareMask);
        Assert.Equal(0xFFu, state.Back.WriteMask);
    }

    [Fact]
    public void StencilState_SurvivesRenderStateDerivedCopies()
    {
        var registers = new Dictionary<uint, uint>
        {
            [DbDepthControl] = StencilControl(
                enable: true,
                backface: true,
                frontFunc: 5,
                backFunc: 3,
                frontFail: 2),
            [DbStencilRefMask] = 0x59372Au,
            [DbStencilRefMaskBf] = 0x442211u,
        };
        var renderState = GuestRenderState.Default with
        {
            Stencil = AgcExports.DecodeStencilState(registers),
        };

        // Presenters rebuild render states with `with` when they adjust one
        // slice (e.g. read-only depth drops WriteEnable); the decoded stencil
        // must survive those derived copies untouched.
        var derived = renderState with
        {
            Depth = renderState.Depth with { WriteEnable = false },
        };

        Assert.Equal(renderState.Stencil, derived.Stencil);
        Assert.True(derived.Stencil.TestEnable);
        Assert.True(derived.Stencil.BackfaceEnable);
        Assert.Equal(5u, derived.Stencil.Front.CompareFunc);
        Assert.Equal(3u, derived.Stencil.Back.CompareFunc);
        Assert.Equal(0x2Au, derived.Stencil.Front.Reference);
        Assert.Equal(0x11u, derived.Stencil.Back.Reference);
        Assert.Equal(GuestStencilState.Default, GuestRenderState.Default.Stencil);
    }

    [Fact]
    public void StencilState_PipelineIdentityIgnoresRefMasksButTracksOps()
    {
        var baseRegisters = new Dictionary<uint, uint>
        {
            [DbDepthControl] = StencilControl(
                enable: true,
                backface: true,
                frontFunc: 5,
                backFunc: 3,
                frontFail: 2,
                frontDepthFail: 4,
                frontPass: 6),
            [DbStencilRefMask] = 0x59372Au,
            [DbStencilRefMaskBf] = 0x442211u,
        };
        var baseIdentity = AgcExports.DecodeStencilState(baseRegisters).PipelineIdentity;

        // Reference and compare/write masks are per-draw dynamic state; they
        // must not change the pipeline identity (no pipeline rebuilds).
        var changedMasks = AgcExports.DecodeStencilState(
            baseRegisters.WithRegister(DbStencilRefMask, 0x010203u)).PipelineIdentity;
        Assert.Equal(baseIdentity, changedMasks);

        // Ops and funcs are baked into the pipeline, so they are part of it.
        var changedOps = AgcExports.DecodeStencilState(
            baseRegisters.WithRegister(
                DbDepthControl,
                StencilControl(true, true, frontFunc: 5, backFunc: 3, frontFail: 7))).PipelineIdentity;
        Assert.NotEqual(baseIdentity, changedOps);
        var changedFuncs = AgcExports.DecodeStencilState(
            baseRegisters.WithRegister(
                DbDepthControl,
                StencilControl(true, true, frontFunc: 6, backFunc: 3, frontFail: 2))).PipelineIdentity;
        Assert.NotEqual(baseIdentity, changedFuncs);
    }

    [Fact]
    public void RenderTargetSampleCount_DecodesCbColorAttribNumSamples()
    {
        // CB_COLORn_ATTRIB.NUM_SAMPLES [16:14] holds log2 of the sample count
        // (0=1x, 1=2x, 2=4x, 3=8x); absent/malformed codes decode to 1.
        Assert.Equal(1u, AgcExports.DecodeRenderTargetSampleCount(0u));
        Assert.Equal(2u, AgcExports.DecodeRenderTargetSampleCount(1u << 14));
        Assert.Equal(4u, AgcExports.DecodeRenderTargetSampleCount(2u << 14));
        Assert.Equal(8u, AgcExports.DecodeRenderTargetSampleCount(3u << 14));
        // Malformed codes (field reading 4..7) degrade to 1.
        Assert.Equal(1u, AgcExports.DecodeRenderTargetSampleCount(4u << 14));
        Assert.Equal(1u, AgcExports.DecodeRenderTargetSampleCount(7u << 14));
        // Unrelated low bits (tile mode etc.) do not affect the sample count.
        Assert.Equal(4u, AgcExports.DecodeRenderTargetSampleCount(0xFFFu | (2u << 14)));
    }
}

file static class StencilDecodeTestDictionaryExtensions
{
    public static Dictionary<uint, uint> WithRegister(
        this Dictionary<uint, uint> source,
        uint register,
        uint value)
    {
        var copy = new Dictionary<uint, uint>(source) { [register] = value };
        return copy;
    }
}
