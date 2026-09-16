// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Buffers.Binary;
using System.Diagnostics;
using SharpEmu.HLE;
using SharpEmu.Libs.Kernel;
using Xunit;

namespace SharpEmu.Libs.Tests.Kernel;

// sceKernelDeleteSema must release blocked waiters with DELETED (scene unload,
// streamer shutdown) and sceKernelCancelSema must release ALL waiters with
// CANCELED, even when the post-cancel count stays below their need count.
// Both are exercised on the host fallback wait loop (real Task threads parked
// in the production code) and on the cooperative block waiter.
public sealed class KernelSemaphoreDeleteCancelTests
{
    private const ulong MemoryBase = 0x1_0000_0000;
    private const ulong HandleAddress = MemoryBase + 0x100;
    private const ulong NameAddress = MemoryBase + 0x180;
    private const ulong TimeoutAddress = MemoryBase + 0x200;
    private const ulong WaitingThreadsAddress = MemoryBase + 0x300;

    [Fact]
    public void DeleteSemaReleasesBlockedHostWaiterAsDeleted()
    {
        var (_, ctx, handle) = CreateSemaphore(initialCount: 0, maxCount: 10);

        ctx[CpuRegister.Rdi] = handle;
        ctx[CpuRegister.Rsi] = 1; // needCount
        ctx[CpuRegister.Rdx] = 0; // no timeout pointer: infinite wait
        var waiter = Task.Run(() => KernelSemaphoreCompatExports.KernelWaitSema(ctx));

        WaitForParkedWaiter(handle);

        ctx[CpuRegister.Rdi] = handle;
        Assert.Equal(
            (int)OrbisGen2Result.ORBIS_GEN2_OK,
            KernelSemaphoreCompatExports.KernelDeleteSema(ctx));

        Assert.True(waiter.Wait(TimeSpan.FromSeconds(10)), "blocked waiter did not return after delete");
        Assert.Equal((int)OrbisGen2Result.ORBIS_GEN2_ERROR_DELETED, waiter.Result);

        // The handle is gone for new callers too.
        ctx[CpuRegister.Rdi] = handle;
        Assert.Equal(
            (int)OrbisGen2Result.ORBIS_GEN2_ERROR_NOT_FOUND,
            KernelSemaphoreCompatExports.KernelDeleteSema(ctx));
    }

    [Fact]
    public void DeleteSemaReleasesCooperativeWaiterAsDeleted()
    {
        var (_, ctx, handle) = CreateSemaphore(initialCount: 0, maxCount: 10);
        var waiter = StageSemaphoreWait(ctx, handle, needCount: 1, timeoutAddress: 0, threadHandle: 0x801);

        ctx[CpuRegister.Rdi] = handle;
        Assert.Equal(
            (int)OrbisGen2Result.ORBIS_GEN2_OK,
            KernelSemaphoreCompatExports.KernelDeleteSema(ctx));

        Assert.True(waiter.TryWake());
        Assert.Equal((int)OrbisGen2Result.ORBIS_GEN2_ERROR_DELETED, waiter.Resume());
    }

    [Fact]
    public void CancelSemaReleasesBlockedHostWaiterAsCanceled()
    {
        var (_, ctx, handle) = CreateSemaphore(initialCount: 0, maxCount: 10);

        ctx[CpuRegister.Rdi] = handle;
        ctx[CpuRegister.Rsi] = 1;
        ctx[CpuRegister.Rdx] = 0;
        var waiter = Task.Run(() => KernelSemaphoreCompatExports.KernelWaitSema(ctx));

        WaitForParkedWaiter(handle);

        Assert.Equal(
            (int)OrbisGen2Result.ORBIS_GEN2_OK,
            KernelSemaphoreCompatExports.KernelCancelSema(
                ctx,
                handle,
                setCount: 1,
                waitingThreadsAddress: WaitingThreadsAddress));
        Assert.True(ctx.TryReadUInt32(WaitingThreadsAddress, out var reportedWaiters));
        Assert.Equal(1u, reportedWaiters);

        Assert.True(waiter.Wait(TimeSpan.FromSeconds(10)), "blocked waiter did not return after cancel");
        Assert.Equal((int)OrbisGen2Result.ORBIS_GEN2_ERROR_CANCELED, waiter.Result);

        // Waiter accounting decremented naturally instead of being zeroed by
        // the cancel.
        Assert.True(KernelSemaphoreCompatExports.TryGetSemaphoreWaiterCountForTest(handle, out var remaining));
        Assert.Equal(0, remaining);

        // The post-cancel count is available to new waiters.
        ctx[CpuRegister.Rdi] = handle;
        ctx[CpuRegister.Rsi] = 1;
        ctx[CpuRegister.Rdx] = 0;
        Assert.Equal((int)OrbisGen2Result.ORBIS_GEN2_OK, KernelSemaphoreCompatExports.KernelWaitSema(ctx));

        ctx[CpuRegister.Rdi] = handle;
        Assert.Equal(
            (int)OrbisGen2Result.ORBIS_GEN2_OK,
            KernelSemaphoreCompatExports.KernelDeleteSema(ctx));
    }

    [Fact]
    public void CancelSemaReleasesWaiterEvenWhenCountStaysBelowNeedCount()
    {
        var (_, ctx, handle) = CreateSemaphore(initialCount: 0, maxCount: 10);

        ctx[CpuRegister.Rdi] = handle;
        ctx[CpuRegister.Rsi] = 2; // needs two units
        ctx[CpuRegister.Rdx] = 0;
        var waiter = Task.Run(() => KernelSemaphoreCompatExports.KernelWaitSema(ctx));

        WaitForParkedWaiter(handle);

        // setCount 1 < needCount 2: the old code left such waiters parked
        // forever because their wake predicate re-checked the count.
        Assert.Equal(
            (int)OrbisGen2Result.ORBIS_GEN2_OK,
            KernelSemaphoreCompatExports.KernelCancelSema(ctx, handle, setCount: 1, waitingThreadsAddress: 0));

        Assert.True(waiter.Wait(TimeSpan.FromSeconds(10)), "blocked waiter did not return after cancel");
        Assert.Equal((int)OrbisGen2Result.ORBIS_GEN2_ERROR_CANCELED, waiter.Result);

        ctx[CpuRegister.Rdi] = handle;
        Assert.Equal(
            (int)OrbisGen2Result.ORBIS_GEN2_OK,
            KernelSemaphoreCompatExports.KernelDeleteSema(ctx));
    }

    [Fact]
    public void CancelSemaReleasesCooperativeWaiterAsCanceled()
    {
        var (_, ctx, handle) = CreateSemaphore(initialCount: 0, maxCount: 10);
        var waiter = StageSemaphoreWait(ctx, handle, needCount: 1, timeoutAddress: 0, threadHandle: 0x802);

        // setCount 0 < needCount 1: only the cancel path can release the
        // waiter.
        Assert.Equal(
            (int)OrbisGen2Result.ORBIS_GEN2_OK,
            KernelSemaphoreCompatExports.KernelCancelSema(ctx, handle, setCount: 0, waitingThreadsAddress: 0));

        Assert.True(waiter.TryWake());
        Assert.Equal((int)OrbisGen2Result.ORBIS_GEN2_ERROR_CANCELED, waiter.Resume());
    }

    [Fact]
    public void CooperativeWaiterDiscoversCancelAfterDeadlineExpiry()
    {
        var (memory, ctx, handle) = CreateSemaphore(initialCount: 0, maxCount: 10);
        WriteUInt32(memory, TimeoutAddress, 50_000); // 50 ms
        var waiter = StageSemaphoreWait(ctx, handle, needCount: 1, timeoutAddress: TimeoutAddress, threadHandle: 0x803);

        Thread.Sleep(100); // let the wait deadline pass
        Assert.Equal(
            (int)OrbisGen2Result.ORBIS_GEN2_OK,
            KernelSemaphoreCompatExports.KernelCancelSema(ctx, handle, setCount: 0, waitingThreadsAddress: 0));

        // The cancel wake cannot find this waiter (no scheduler registered
        // it), so the resume handler must observe the cancel itself instead of
        // reporting a misleading TIMED_OUT.
        Assert.Equal((int)OrbisGen2Result.ORBIS_GEN2_ERROR_CANCELED, waiter.Resume());
        Assert.Equal(0u, ReadUInt32(memory, TimeoutAddress));

        ctx[CpuRegister.Rdi] = handle;
        Assert.Equal(
            (int)OrbisGen2Result.ORBIS_GEN2_OK,
            KernelSemaphoreCompatExports.KernelDeleteSema(ctx));
    }

    [Fact]
    public void WaitSemaStillTimesOutWhenNothingSignals()
    {
        var (memory, ctx, handle) = CreateSemaphore(initialCount: 0, maxCount: 10);
        WriteUInt32(memory, TimeoutAddress, 20_000); // 20 ms

        ctx[CpuRegister.Rdi] = handle;
        ctx[CpuRegister.Rsi] = 1;
        ctx[CpuRegister.Rdx] = TimeoutAddress;

        var stopwatch = Stopwatch.StartNew();
        var result = KernelSemaphoreCompatExports.KernelWaitSema(ctx);
        stopwatch.Stop();

        Assert.Equal((int)OrbisGen2Result.ORBIS_GEN2_ERROR_TIMED_OUT, result);
        Assert.Equal(0u, ReadUInt32(memory, TimeoutAddress));
        Assert.True(stopwatch.ElapsedMilliseconds >= 10, $"wait returned early: {stopwatch.ElapsedMilliseconds} ms");
        Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(10));

        ctx[CpuRegister.Rdi] = handle;
        Assert.Equal(
            (int)OrbisGen2Result.ORBIS_GEN2_OK,
            KernelSemaphoreCompatExports.KernelDeleteSema(ctx));
    }

    [Fact]
    public void CooperativeWaiterTimesOutWhenNothingSignals()
    {
        var (memory, ctx, handle) = CreateSemaphore(initialCount: 0, maxCount: 10);
        WriteUInt32(memory, TimeoutAddress, 50_000); // 50 ms
        var waiter = StageSemaphoreWait(ctx, handle, needCount: 1, timeoutAddress: TimeoutAddress, threadHandle: 0x804);

        Assert.False(waiter.TryWake()); // count 0 < needCount 1

        Thread.Sleep(100); // let the deadline pass
        Assert.Equal((int)OrbisGen2Result.ORBIS_GEN2_ERROR_TIMED_OUT, waiter.Resume());
        Assert.Equal(0u, ReadUInt32(memory, TimeoutAddress));

        ctx[CpuRegister.Rdi] = handle;
        Assert.Equal(
            (int)OrbisGen2Result.ORBIS_GEN2_OK,
            KernelSemaphoreCompatExports.KernelDeleteSema(ctx));
    }

    // Orbis semantics store the REMAINING time in the timeout slot on
    // success; the immediate-success path must write 0 exactly like the
    // blocked-success resume path, not echo the original request back.
    [Fact]
    public void WaitSemaImmediateSuccessWritesZeroRemainingTimeout()
    {
        var (memory, ctx, handle) = CreateSemaphore(initialCount: 3, maxCount: 10);
        WriteUInt32(memory, TimeoutAddress, 1_000_000); // 1 s

        ctx[CpuRegister.Rdi] = handle;
        ctx[CpuRegister.Rsi] = 1; // needCount: satisfied without blocking
        ctx[CpuRegister.Rdx] = TimeoutAddress;

        Assert.Equal(
            (int)OrbisGen2Result.ORBIS_GEN2_OK,
            KernelSemaphoreCompatExports.KernelWaitSema(ctx));
        Assert.Equal(0u, ReadUInt32(memory, TimeoutAddress));

        ctx[CpuRegister.Rdi] = handle;
        Assert.Equal(
            (int)OrbisGen2Result.ORBIS_GEN2_OK,
            KernelSemaphoreCompatExports.KernelDeleteSema(ctx));
    }

    private static (FakeCpuMemory Memory, CpuContext Context, uint Handle) CreateSemaphore(
        int initialCount,
        int maxCount)
    {
        var memory = new FakeCpuMemory(MemoryBase, 0x1000);
        var ctx = new CpuContext(memory, Generation.Gen5);
        memory.WriteCString(NameAddress, "imp2_kernel_sync");
        ctx[CpuRegister.Rdi] = HandleAddress;
        ctx[CpuRegister.Rsi] = NameAddress;
        ctx[CpuRegister.Rdx] = 0;
        ctx[CpuRegister.Rcx] = unchecked((ulong)initialCount);
        ctx[CpuRegister.R8] = unchecked((ulong)maxCount);
        ctx[CpuRegister.R9] = 0;
        Assert.Equal(
            (int)OrbisGen2Result.ORBIS_GEN2_OK,
            KernelSemaphoreCompatExports.KernelCreateSema(ctx));
        Assert.True(ctx.TryReadUInt32(HandleAddress, out var handle));
        Assert.NotEqual(0u, handle);
        return (memory, ctx, handle);
    }

    // Stages the cooperative block the way a guest thread under the scheduler
    // would (pattern of KernelEventQueueWaiterLifetimeTests).
    private static IGuestThreadBlockWaiter StageSemaphoreWait(
        CpuContext ctx,
        uint handle,
        int needCount,
        ulong timeoutAddress,
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
            ctx[CpuRegister.Rsi] = unchecked((ulong)needCount);
            ctx[CpuRegister.Rdx] = timeoutAddress;
            Assert.Equal(
                (int)OrbisGen2Result.ORBIS_GEN2_OK,
                KernelSemaphoreCompatExports.KernelWaitSema(ctx));

            Assert.True(GuestThreadExecution.TryConsumeCurrentThreadBlock(
                out var reason,
                out _,
                out _,
                out _,
                out var waiter,
                out _));
            Assert.Equal("sceKernelWaitSema", reason);
            return Assert.IsAssignableFrom<IGuestThreadBlockWaiter>(waiter);
        }
        finally
        {
            GuestThreadExecution.RestoreImportCallFrame(previousFrame);
            GuestThreadExecution.RestoreGuestThread(previousThread);
        }
    }

    private static void WaitForParkedWaiter(uint handle)
    {
        var deadline = Environment.TickCount64 + 5_000;
        while (Environment.TickCount64 < deadline)
        {
            if (KernelSemaphoreCompatExports.TryGetSemaphoreWaiterCountForTest(handle, out var waiters) &&
                waiters >= 1)
            {
                return;
            }

            Thread.Sleep(10);
        }

        Assert.True(false, $"semaphore 0x{handle:X8} waiter never parked");
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
}
