// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Diagnostics;
using SharpEmu.HLE;
using SharpEmu.Libs.Kernel;
using Xunit;

namespace SharpEmu.Libs.Tests.Kernel;

[Collection("Host wait scheduler")]
public sealed class KernelUmtxAbiTests
{
    [Theory]
    [InlineData(0L, 0L, 60)]
    [InlineData(-1L, 0L, 22)]
    [InlineData(0L, 1_000_000_000L, 22)]
    public void ZeroAndInvalidTimeoutsFailWithoutParking(long seconds, long nanoseconds, int expectedErrno)
    {
        const ulong memoryBase = 0x3_2400_0000;
        var ctx = new CpuContext(new FakeCpuMemory(memoryBase, 0x1000), Generation.Gen5)
        {
            FsBase = memoryBase + 0x800,
        };
        var timeout = memoryBase + 0x200;
        Assert.True(ctx.TryWriteUInt64(timeout, unchecked((ulong)seconds)));
        Assert.True(ctx.TryWriteUInt64(timeout + 8, unchecked((ulong)nanoseconds)));
        ctx[CpuRegister.Rdi] = memoryBase + 0x100;
        ctx[CpuRegister.Rsi] = 2;
        ctx[CpuRegister.R8] = timeout;
        Assert.Equal(-1, KernelUmtxCompatExports.UmtxOp(ctx));
        Assert.Equal(ulong.MaxValue, ctx[CpuRegister.Rax]);
        KernelRuntimeCompatExports.ErrorAddress(ctx);
        Assert.True(ctx.TryReadUInt32(ctx[CpuRegister.Rax], out var errno));
        Assert.Equal((uint)expectedErrno, errno);
    }

    [Theory]
    [InlineData(2)]
    [InlineData(11)]
    [InlineData(15)]
    public void TimedWaitUsesFifthArgumentAndResumesWithErrno(int operation)
    {
        const ulong memoryBase = 0x3_2300_0000;
        var ctx = new CpuContext(new FakeCpuMemory(memoryBase, 0x1000), Generation.Gen5)
        {
            FsBase = memoryBase + 0x800,
            GsBase = memoryBase + 0x800,
        };
        var timeout = memoryBase + 0x200;
        Assert.True(ctx.TryWriteUInt64(timeout + 8, 5_000_000));
        // Bytes following a plain timespec are unrelated caller stack data.
        Assert.True(ctx.TryWriteUInt32(timeout + 16, 1));
        ctx[CpuRegister.Rdi] = memoryBase + 0x100;
        ctx[CpuRegister.Rsi] = (ulong)operation;
        ctx[CpuRegister.Rcx] = 0;
        ctx[CpuRegister.R8] = timeout;
        var previousThread = GuestThreadExecution.EnterGuestThread(0xA520 + (ulong)operation);
        var previousFrame = GuestThreadExecution.EnterImportCallFrame(0x10000, 0x20000, 0x30000);
        try
        {
            var started = Stopwatch.GetTimestamp();
            Assert.Equal(0, KernelUmtxCompatExports.UmtxOp(ctx));
            Assert.True(GuestThreadExecution.TryConsumeCurrentThreadBlock(
                out _, out _, out _, out _, out var waiter, out var deadline));
            var delay = (double)(deadline - started) / Stopwatch.Frequency;
            Assert.InRange(delay, 0.004, 0.05);
            Thread.Sleep(10);
            Assert.Equal(-1, waiter!.Resume());
            Assert.Equal(ulong.MaxValue, ctx[CpuRegister.Rax]);
            Assert.Equal(0, KernelRuntimeCompatExports.ErrorAddress(ctx));
            Assert.True(ctx.TryReadUInt32(ctx[CpuRegister.Rax], out var errno));
            Assert.Equal(60u, errno);
        }
        finally
        {
            GuestThreadExecution.RestoreImportCallFrame(previousFrame);
            GuestThreadExecution.RestoreGuestThread(previousThread);
        }
    }
}
