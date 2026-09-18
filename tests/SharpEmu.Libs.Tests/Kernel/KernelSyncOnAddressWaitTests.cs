// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Buffers.Binary;
using System.Diagnostics;
using SharpEmu.HLE;
using SharpEmu.Libs.Kernel;
using Xunit;

namespace SharpEmu.Libs.Tests.Kernel;

// sceKernelSyncOnAddressWait(addr, expectedValue, size, *pTimeout) is a
// compare-and-wait: the caller parks until the value at addr no longer equals
// the expected value, or the timeout (microseconds, 0/null = infinite)
// expires. These tests pin that contract for the immediate path, the host
// fallback poll loop, and the cooperative block waiter.
public sealed class KernelSyncOnAddressWaitTests
{
    private const ulong MemoryBase = 0x1_0000_0000;
    private const ulong ValueAddress = MemoryBase + 0x100;
    private const ulong TimeoutAddress = MemoryBase + 0x200;

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(4)]
    [InlineData(8)]
    public void WaitReturnsOkImmediatelyWhenComparedWidthDiffers(ulong size)
    {
        var (memory, ctx) = CreateContext();
        WriteUInt64(memory, ValueAddress, 1); // differs from 0 in byte 0 only
        SetWaitArguments(ctx, ValueAddress, expectedValue: 0, size, timeoutAddress: 0);

        var result = KernelSyncOnAddressCompatExports.SyncOnAddressWait(ctx);

        Assert.Equal((int)OrbisGen2Result.ORBIS_GEN2_OK, result);
        Assert.Equal(0UL, ctx[CpuRegister.Rax]);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(4)]
    public void WaitComparesOnlyTheDeclaredWidth(ulong size)
    {
        // The stored value differs from the expected one only BEYOND the
        // compared width, so the wait must keep treating it as equal and run
        // into the timeout instead of returning.
        var (memory, ctx) = CreateContext();
        WriteUInt64(memory, ValueAddress, 1UL << (int)(8 * size));
        WriteUInt32(memory, TimeoutAddress, 20_000);
        SetWaitArguments(ctx, ValueAddress, expectedValue: 0, size, TimeoutAddress);

        var result = KernelSyncOnAddressCompatExports.SyncOnAddressWait(ctx);

        Assert.Equal((int)OrbisGen2Result.ORBIS_GEN2_ERROR_TIMED_OUT, result);
        Assert.Equal(0u, ReadUInt32(memory, TimeoutAddress));
    }

    [Fact]
    public void WaitWithTimeoutReturnsTimedOutAndWritesZeroRemaining()
    {
        var (memory, ctx) = CreateContext();
        WriteUInt32(memory, ValueAddress, 0x2A);
        WriteUInt32(memory, TimeoutAddress, 20_000); // 20 ms
        SetWaitArguments(ctx, ValueAddress, expectedValue: 0x2A, size: 4, TimeoutAddress);

        var stopwatch = Stopwatch.StartNew();
        var result = KernelSyncOnAddressCompatExports.SyncOnAddressWait(ctx);
        stopwatch.Stop();

        Assert.Equal((int)OrbisGen2Result.ORBIS_GEN2_ERROR_TIMED_OUT, result);
        Assert.Equal(unchecked((ulong)(int)OrbisGen2Result.ORBIS_GEN2_ERROR_TIMED_OUT), ctx[CpuRegister.Rax]);
        Assert.Equal(0u, ReadUInt32(memory, TimeoutAddress));
        Assert.True(stopwatch.ElapsedMilliseconds >= 10, $"wait returned early: {stopwatch.ElapsedMilliseconds} ms");
        Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(10));
    }

    [Fact]
    public void WaitParksUntilAnotherThreadChangesTheValue()
    {
        var (memory, ctx) = CreateContext();
        WriteUInt32(memory, ValueAddress, 0x2A);
        SetWaitArguments(ctx, ValueAddress, expectedValue: 0x2A, size: 4, timeoutAddress: 0);

        var changed = new ManualResetEventSlim();
        var changer = Task.Run(() =>
        {
            Thread.Sleep(100);
            WriteUInt32(memory, ValueAddress, 0x2B);
            changed.Set();
        });

        var stopwatch = Stopwatch.StartNew();
        var result = KernelSyncOnAddressCompatExports.SyncOnAddressWait(ctx);
        stopwatch.Stop();

        // The value only ever changes after the changer's 100 ms sleep, so an
        // OK return proves the wait observed the new value instead of parking
        // forever or returning spuriously.
        Assert.True(changed.IsSet, "wait returned before the value changed");
        Assert.Equal((int)OrbisGen2Result.ORBIS_GEN2_OK, result);
        Assert.True(stopwatch.ElapsedMilliseconds >= 90, $"wait returned early: {stopwatch.ElapsedMilliseconds} ms");
        Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(10));
        Assert.True(changer.Wait(TimeSpan.FromSeconds(5)));
    }

    [Fact]
    public void CooperativeWaiterStaysParkedWhileValueMatchesAndResumesWhenItDiffers()
    {
        var (memory, ctx) = CreateContext();
        WriteUInt32(memory, ValueAddress, 0x2A);
        SetWaitArguments(ctx, ValueAddress, expectedValue: 0x2A, size: 4, timeoutAddress: 0);

        var waiter = StageAddressWait(ctx, threadHandle: 0x901);

        // A wake attempt while the value still matches must keep the waiter
        // parked: this is what lock-free queues built on the primitive rely on.
        Assert.False(waiter.TryWake());

        WriteUInt32(memory, ValueAddress, 0x2B);
        Assert.True(waiter.TryWake());
        Assert.Equal((int)OrbisGen2Result.ORBIS_GEN2_OK, waiter.Resume());
    }

    [Fact]
    public void CooperativeWaiterTimesOutAndWritesZeroRemaining()
    {
        var (memory, ctx) = CreateContext();
        WriteUInt32(memory, ValueAddress, 0x2A);
        WriteUInt32(memory, TimeoutAddress, 50_000); // 50 ms
        SetWaitArguments(ctx, ValueAddress, expectedValue: 0x2A, size: 4, TimeoutAddress);

        var waiter = StageAddressWait(ctx, threadHandle: 0x902);

        Assert.False(waiter.TryWake());

        Thread.Sleep(100); // let the deadline pass
        // Models the scheduler's deadline-expiry wake, which readies the
        // thread without consulting the wake predicate.
        Assert.Equal((int)OrbisGen2Result.ORBIS_GEN2_ERROR_TIMED_OUT, waiter.Resume());
        Assert.Equal(0u, ReadUInt32(memory, TimeoutAddress));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(3)]
    [InlineData(5)]
    [InlineData(16)]
    public void WaitRejectsUnsupportedCompareSizes(ulong size)
    {
        var (memory, ctx) = CreateContext();
        WriteUInt32(memory, ValueAddress, 0x2A);
        SetWaitArguments(ctx, ValueAddress, expectedValue: 0x2A, size, timeoutAddress: 0);

        Assert.Equal(
            (int)OrbisGen2Result.ORBIS_GEN2_ERROR_INVALID_ARGUMENT,
            KernelSyncOnAddressCompatExports.SyncOnAddressWait(ctx));
    }

    [Fact]
    public void WaitReportsMemoryFaultForUnmappedAddress()
    {
        var (_, ctx) = CreateContext();
        SetWaitArguments(ctx, MemoryBase + 0x10_0000, expectedValue: 1, size: 4, timeoutAddress: 0);

        Assert.Equal(
            (int)OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT,
            KernelSyncOnAddressCompatExports.SyncOnAddressWait(ctx));
    }

    private static (FakeCpuMemory Memory, CpuContext Context) CreateContext()
    {
        var memory = new FakeCpuMemory(MemoryBase, 0x1000);
        return (memory, new CpuContext(memory, Generation.Gen5));
    }

    private static void SetWaitArguments(
        CpuContext ctx,
        ulong address,
        ulong expectedValue,
        ulong size,
        ulong timeoutAddress)
    {
        ctx[CpuRegister.Rdi] = address;
        ctx[CpuRegister.Rsi] = expectedValue;
        ctx[CpuRegister.Rdx] = size;
        ctx[CpuRegister.Rcx] = timeoutAddress;
    }

    // Stages the cooperative block the way a guest thread under the scheduler
    // would: the export returns after registering the block, and the test
    // drives the resulting waiter directly (pattern of
    // KernelEventQueueWaiterLifetimeTests).
    private static IGuestThreadBlockWaiter StageAddressWait(CpuContext ctx, ulong threadHandle)
    {
        var previousThread = GuestThreadExecution.EnterGuestThread(threadHandle);
        var previousFrame = GuestThreadExecution.EnterImportCallFrame(
            returnRip: 0x1_0000 + threadHandle,
            resumeRsp: 0x2_0000 + threadHandle,
            returnSlotAddress: 0x3_0000 + threadHandle);
        try
        {
            Assert.Equal(
                (int)OrbisGen2Result.ORBIS_GEN2_OK,
                KernelSyncOnAddressCompatExports.SyncOnAddressWait(ctx));

            Assert.True(GuestThreadExecution.TryConsumeCurrentThreadBlock(
                out var reason,
                out _,
                out _,
                out _,
                out var waiter,
                out _));
            Assert.Equal("sceKernelSyncOnAddressWait", reason);
            return Assert.IsAssignableFrom<IGuestThreadBlockWaiter>(waiter);
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

    private static void WriteUInt64(FakeCpuMemory memory, ulong address, ulong value)
    {
        Span<byte> bytes = stackalloc byte[sizeof(ulong)];
        BinaryPrimitives.WriteUInt64LittleEndian(bytes, value);
        Assert.True(memory.TryWrite(address, bytes));
    }
}
