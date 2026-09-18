// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Buffers.Binary;
using System.Diagnostics;
using SharpEmu.HLE;
using SharpEmu.Libs.Kernel;
using Xunit;

namespace SharpEmu.Libs.Tests.Kernel;

// Timed sceKernelWaitEqueue waits must park the GUEST thread with a deadline
// (the cooperative block-with-deadline pattern of KernelWaitSema /
// KernelWaitEventFlag) instead of Monitor.Wait-ing on the calling host
// thread. These tests stage the block the way a guest thread under the
// scheduler would (pattern of KernelEventQueueWaiterLifetimeTests).
public sealed class KernelEventQueueTimedWaitTests
{
    private const ulong BaseAddress = 0x1_0000_0000;
    private const ulong HandleAddress = BaseAddress + 0x100;
    private const ulong EventsAddress = BaseAddress + 0x200;
    private const ulong OutCountAddress = BaseAddress + 0x300;
    private const ulong TimeoutAddress = BaseAddress + 0x400;

    [Fact]
    public void TimedWait_BlocksCooperativelyAndTimesOutWithoutHostBlocking()
    {
        var (memory, ctx, handle) = CreateEqueue();
        const uint timeoutUsec = 100_000; // 100 ms
        WriteUInt32(memory, TimeoutAddress, timeoutUsec);

        var stopwatch = Stopwatch.StartNew();
        var (waiter, deadline) = StageTimedGuestWait(ctx, handle, threadHandle: 0x901);
        stopwatch.Stop();

        // The call returned well before the deadline could have elapsed, so
        // it parked the guest thread cooperatively instead of blocking the
        // host thread for the whole timeout duration (the old Monitor.Wait
        // loop could not return before the 100 ms deadline).
        Assert.True(
            stopwatch.ElapsedMilliseconds < 60,
            $"timed wait blocked the host thread: {stopwatch.ElapsedMilliseconds} ms");
        Assert.True(deadline > Stopwatch.GetTimestamp(), "deadline must be in the future");

        // Deadline expiry readies the thread WITHOUT running the wake
        // predicate, so the resume must discover the timeout itself and
        // report zero delivered events.
        Thread.Sleep(150); // let the deadline pass
        Assert.Equal(
            (int)OrbisGen2Result.ORBIS_GEN2_ERROR_TIMED_OUT,
            waiter.Resume());
        Assert.Equal(0u, ReadUInt32(memory, OutCountAddress));
    }

    [Fact]
    public void TimedWait_DeliversEventReservedByWake()
    {
        var (memory, ctx, handle) = CreateEqueue();
        WriteUInt32(memory, TimeoutAddress, 5_000_000); // 5 s

        var (waiter, _) = StageTimedGuestWait(ctx, handle, threadHandle: 0x902);

        // Nothing pending yet: the wake predicate must not release the waiter.
        Assert.False(waiter.TryWake());

        var expected = new KernelEventQueueCompatExports.KernelQueuedEvent(
            Ident: 0x99,
            Filter: KernelEventQueueCompatExports.KernelEventFilterUser,
            Flags: KernelEventQueueCompatExports.KernelEventFlagClear,
            Fflags: 1,
            Data: 0xBEEF,
            UserData: 0xCAFE);
        Assert.True(KernelEventQueueCompatExports.EnqueueEvent(handle, expected));

        // A queued event lets the wake predicate commit the delivery.
        Assert.True(waiter.TryWake());
        Assert.Equal(
            (int)OrbisGen2Result.ORBIS_GEN2_OK,
            waiter.Resume());
        Assert.Equal(1u, ReadUInt32(memory, OutCountAddress));
        // The delivered event carries the queued ident/filter/data.
        Assert.True(ctx.TryReadUInt64(EventsAddress, out var ident));
        Assert.Equal(expected.Ident, ident);
        Assert.True(ctx.TryReadUInt64(EventsAddress + 0x10, out var data));
        Assert.Equal(expected.Data, data);
    }

    private static (FakeCpuMemory Memory, CpuContext Context, ulong Handle)
        CreateEqueue()
    {
        var memory = new FakeCpuMemory(BaseAddress, 0x1000);
        var ctx = new CpuContext(memory, Generation.Gen5);
        ctx[CpuRegister.Rdi] = HandleAddress;
        Assert.Equal(
            (int)OrbisGen2Result.ORBIS_GEN2_OK,
            KernelEventQueueCompatExports.KernelCreateEqueue(ctx));
        var handle = ReadUInt64(memory, HandleAddress);
        Assert.NotEqual(0UL, handle);
        return (memory, ctx, handle);
    }

    // Stages the cooperative timed block the way a guest thread under the
    // scheduler would and returns the waiter plus the registered deadline.
    private static (IGuestThreadBlockWaiter Waiter, long Deadline)
        StageTimedGuestWait(
            CpuContext ctx,
            ulong handle,
            ulong threadHandle)
    {
        var previousThread = GuestThreadExecution.EnterGuestThread(threadHandle);
        var previousFrame = GuestThreadExecution.EnterImportCallFrame(
            returnRip: 0x1_0000 + threadHandle,
            resumeRsp: 0x2_0000 + threadHandle,
            returnSlotAddress: 0x3_0000 + threadHandle);
        try
        {
            ctx[CpuRegister.Rdi] = handle;
            ctx[CpuRegister.Rsi] = EventsAddress;
            ctx[CpuRegister.Rdx] = 1;
            ctx[CpuRegister.Rcx] = OutCountAddress;
            ctx[CpuRegister.R8] = TimeoutAddress;
            Assert.Equal(
                (int)OrbisGen2Result.ORBIS_GEN2_OK,
                KernelEventQueueCompatExports.KernelWaitEqueue(ctx));

            Assert.True(GuestThreadExecution.TryConsumeCurrentThreadBlock(
                out var reason,
                out _,
                out var hasContinuation,
                out _,
                out var waiter,
                out var deadline));
            Assert.Equal("sceKernelWaitEqueue", reason);
            Assert.True(hasContinuation);
            Assert.True(deadline != 0, "timed wait must register a block deadline");
            return (Assert.IsAssignableFrom<IGuestThreadBlockWaiter>(waiter), deadline);
        }
        finally
        {
            GuestThreadExecution.RestoreImportCallFrame(previousFrame);
            GuestThreadExecution.RestoreGuestThread(previousThread);
        }
    }

    private static void WriteUInt32(FakeCpuMemory memory, ulong address, uint value)
    {
        Span<byte> bytes = stackalloc byte[sizeof(uint)];
        BinaryPrimitives.WriteUInt32LittleEndian(bytes, value);
        Assert.True(memory.TryWrite(address, bytes));
    }

    private static uint ReadUInt32(FakeCpuMemory memory, ulong address)
    {
        Span<byte> bytes = stackalloc byte[sizeof(uint)];
        Assert.True(memory.TryRead(address, bytes));
        return BinaryPrimitives.ReadUInt32LittleEndian(bytes);
    }

    private static ulong ReadUInt64(FakeCpuMemory memory, ulong address)
    {
        Span<byte> bytes = stackalloc byte[sizeof(ulong)];
        Assert.True(memory.TryRead(address, bytes));
        return BinaryPrimitives.ReadUInt64LittleEndian(bytes);
    }
}
