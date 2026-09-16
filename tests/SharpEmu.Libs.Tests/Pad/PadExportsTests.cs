// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Buffers.Binary;
using SharpEmu.HLE;
using SharpEmu.Libs.Pad;
using Xunit;

namespace SharpEmu.Libs.Tests.Pad;

public sealed class PadExportsTests
{
    private const ulong Base = 0x1_0000_0000;
    private const int InvalidHandle = unchecked((int)0x80920003);
    private const int InvalidArgument = unchecked((int)0x80020003); // ORBIS_GEN2_ERROR_INVALID_ARGUMENT

    private readonly FakeCpuMemory _memory = new(Base, 0x1000);
    private readonly CpuContext _ctx;

    public PadExportsTests()
    {
        _ctx = new CpuContext(_memory, Generation.Gen5);
    }

    [Theory]
    [InlineData(0, 0)]
    [InlineData(1, 0)]
    [InlineData(2, InvalidHandle)]
    [InlineData(-1, InvalidHandle)]
    public void SetTiltCorrectionState_ValidatesHandle(int handle, int expected)
    {
        _ctx[CpuRegister.Rdi] = unchecked((ulong)handle);
        Assert.Equal(expected, PadExports.PadSetTiltCorrectionState(_ctx));
    }

    /// <summary>
    /// Mirrors the calling frame observed in PPSA10112: the out-param points at
    /// rbp-0x30 and the caller's stack cookie sits at rbp-0x28, so the state is
    /// eight bytes. Writing more would smash the cookie and fail the guest's
    /// stack check, which is the failure mode this size guards against.
    /// </summary>
    [Fact]
    public void GetTriggerEffectState_WritesEightBytesAndLeavesTheCookieIntact()
    {
        const ulong stateAddress = Base + 0x100;
        const ulong cookieAddress = stateAddress + 8;
        const ulong cookie = 0xC0DEC0DECAFEBA00UL;

        Assert.True(_memory.TryWrite(stateAddress, new byte[] { 0xEE, 0xEE, 0xEE, 0xEE, 0xEE, 0xEE, 0xEE, 0xEE }));
        Assert.True(_memory.TryWrite(cookieAddress, BitConverter.GetBytes(cookie)));

        _ctx[CpuRegister.Rdi] = 0;
        _ctx[CpuRegister.Rsi] = stateAddress;

        Assert.Equal(0, PadExports.PadGetTriggerEffectState(_ctx));

        Span<byte> state = stackalloc byte[8];
        Assert.True(_memory.TryRead(stateAddress, state));
        foreach (var value in state)
        {
            Assert.Equal(0, value);
        }

        Span<byte> guard = stackalloc byte[8];
        Assert.True(_memory.TryRead(cookieAddress, guard));
        Assert.Equal(cookie, BitConverter.ToUInt64(guard));
    }

    [Theory]
    [InlineData(2)]
    [InlineData(-1)]
    public void GetTriggerEffectState_RejectsForeignHandles(int handle)
    {
        _ctx[CpuRegister.Rdi] = unchecked((ulong)handle);
        _ctx[CpuRegister.Rsi] = Base + 0x100;
        Assert.Equal(InvalidHandle, PadExports.PadGetTriggerEffectState(_ctx));
    }

    /// <summary>
    /// scePadReadStateExt must produce byte-for-byte the same neutral-state
    /// layout as scePadReadState (buttons, sticks, motion, touch, timestamp)
    /// plus a zeroed trailing extension — every field except the timestamp is
    /// compared, and the ext timestamp must not run backwards.
    /// </summary>
    [Fact]
    public void ReadStateExt_MirrorsReadStatePlusZeroedExtension()
    {
        const ulong stateAddress = Base + 0x100;
        const ulong extStateAddress = Base + 0x200;

        Fill(stateAddress, 0x78, 0xEE);
        Fill(extStateAddress, 0x80, 0xEE);

        _ctx[CpuRegister.Rdi] = 0;
        _ctx[CpuRegister.Rsi] = stateAddress;
        Assert.Equal(0, PadExports.PadReadState(_ctx));

        _ctx[CpuRegister.Rsi] = extStateAddress;
        Assert.Equal(0, PadExports.PadReadStateExt(_ctx));

        Span<byte> state = stackalloc byte[0x78];
        Span<byte> extState = stackalloc byte[0x80];
        Assert.True(_memory.TryRead(stateAddress, state));
        Assert.True(_memory.TryRead(extStateAddress, extState));

        // Everything except the microsecond timestamp at 0x50..0x58 matches.
        for (var offset = 0; offset < 0x78; offset++)
        {
            if (offset is >= 0x50 and < 0x58)
            {
                continue;
            }

            Assert.Equal(state[offset], extState[offset]);
        }

        Assert.True(
            BinaryPrimitives.ReadUInt64LittleEndian(extState[0x50..]) >=
            BinaryPrimitives.ReadUInt64LittleEndian(state[0x50..]));

        // The extension field the Ext variant adds past the base layout is
        // zeroed, and the full 0x80 bytes were overwritten (no 0xEE left).
        for (var offset = 0x78; offset < 0x80; offset++)
        {
            Assert.Equal(0, extState[offset]);
        }

        Assert.DoesNotContain((byte)0xEE, extState.ToArray());
    }

    [Theory]
    [InlineData(2, InvalidHandle)]
    [InlineData(-1, InvalidHandle)]
    public void ReadStateExt_RejectsForeignHandles(int handle, int expected)
    {
        _ctx[CpuRegister.Rdi] = unchecked((ulong)handle);
        _ctx[CpuRegister.Rsi] = Base + 0x100;
        Assert.Equal(expected, PadExports.PadReadStateExt(_ctx));
    }

    [Fact]
    public void ReadStateExt_RejectsNullDataPointer()
    {
        _ctx[CpuRegister.Rdi] = 0;
        _ctx[CpuRegister.Rsi] = 0;
        Assert.Equal(InvalidArgument, PadExports.PadReadStateExt(_ctx));
    }

    [Theory]
    [InlineData(0, 1)]
    [InlineData(1, 1)]
    [InlineData(2, 0)]
    [InlineData(-1, 0)]
    public void IsValidHandle_ReportsTheOpenPadTable(int handle, int expected)
    {
        _ctx[CpuRegister.Rdi] = unchecked((ulong)handle);
        Assert.Equal(expected, PadExports.PadIsValidHandle(_ctx));
    }

    [Fact]
    public void GetIdleCount_WritesZeroIdleCount()
    {
        const ulong countAddress = Base + 0x100;
        Fill(countAddress, sizeof(uint), 0xEE);

        _ctx[CpuRegister.Rdi] = 0;
        _ctx[CpuRegister.Rsi] = countAddress;
        Assert.Equal(0, PadExports.PadGetIdleCount(_ctx));

        Span<byte> count = stackalloc byte[sizeof(uint)];
        Assert.True(_memory.TryRead(countAddress, count));
        Assert.Equal(0u, BinaryPrimitives.ReadUInt32LittleEndian(count));
    }

    [Fact]
    public void GetIdleCount_ValidatesHandleAndOutPointer()
    {
        _ctx[CpuRegister.Rdi] = 2;
        _ctx[CpuRegister.Rsi] = Base + 0x100;
        Assert.Equal(InvalidHandle, PadExports.PadGetIdleCount(_ctx));

        _ctx[CpuRegister.Rdi] = 0;
        _ctx[CpuRegister.Rsi] = 0;
        Assert.Equal(InvalidArgument, PadExports.PadGetIdleCount(_ctx));
    }

    [Fact]
    public void GetCapability_ReportsZeroedUnsupportedValue()
    {
        const ulong valueAddress = Base + 0x100;
        Fill(valueAddress, sizeof(uint), 0xEE);

        _ctx[CpuRegister.Rdi] = 1;
        _ctx[CpuRegister.Rsi] = 42; // any capability id
        _ctx[CpuRegister.Rdx] = valueAddress;
        Assert.Equal(0, PadExports.PadGetCapability(_ctx));

        Span<byte> value = stackalloc byte[sizeof(uint)];
        Assert.True(_memory.TryRead(valueAddress, value));
        Assert.Equal(0u, BinaryPrimitives.ReadUInt32LittleEndian(value));
    }

    [Fact]
    public void GetCapability_ValidatesHandleAndOutPointer()
    {
        _ctx[CpuRegister.Rdi] = 2;
        _ctx[CpuRegister.Rsi] = 42;
        _ctx[CpuRegister.Rdx] = Base + 0x100;
        Assert.Equal(InvalidHandle, PadExports.PadGetCapability(_ctx));

        _ctx[CpuRegister.Rdi] = 1;
        _ctx[CpuRegister.Rsi] = 42;
        _ctx[CpuRegister.Rdx] = 0;
        Assert.Equal(InvalidArgument, PadExports.PadGetCapability(_ctx));
    }

    [Theory]
    [InlineData(0, 0)]
    [InlineData(1, 0)]
    [InlineData(2, InvalidHandle)]
    public void ResetOrientation_SucceedsForPrimaryHandles(int handle, int expected)
    {
        _ctx[CpuRegister.Rdi] = unchecked((ulong)handle);
        Assert.Equal(expected, PadExports.PadResetOrientation(_ctx));
    }

    [Fact]
    public void GetVersionInfo_WritesZeroedBlobFromTheHandleConvention()
    {
        const ulong versionInfoAddress = Base + 0x100;
        Fill(versionInfoAddress, 0x10, 0xEE);

        _ctx[CpuRegister.Rdi] = 0; // handle
        _ctx[CpuRegister.Rsi] = versionInfoAddress;
        Assert.Equal(0, PadExports.PadGetVersionInfo(_ctx));

        AssertZeroed(versionInfoAddress, 0x10);
    }

    [Fact]
    public void GetVersionInfo_WritesZeroedBlobFromTheSingleArgumentConvention()
    {
        const ulong versionInfoAddress = Base + 0x100;
        Fill(versionInfoAddress, 0x10, 0xEE);

        _ctx[CpuRegister.Rdi] = versionInfoAddress;
        _ctx[CpuRegister.Rsi] = 0;
        Assert.Equal(0, PadExports.PadGetVersionInfo(_ctx));

        AssertZeroed(versionInfoAddress, 0x10);
    }

    private void Fill(ulong address, int length, byte value)
    {
        Span<byte> filler = stackalloc byte[length];
        filler.Fill(value);
        Assert.True(_memory.TryWrite(address, filler));
    }

    private void AssertZeroed(ulong address, int length)
    {
        Span<byte> buffer = stackalloc byte[length];
        Assert.True(_memory.TryRead(address, buffer));
        foreach (var value in buffer)
        {
            Assert.Equal(0, value);
        }
    }
}
