// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Buffers.Binary;
using System.Diagnostics;
using SharpEmu.HLE;
using SharpEmu.Libs.Kernel;
using Xunit;

namespace SharpEmu.Libs.Tests.Kernel;

// _umtx_op(obj, op, val, uaddr, uaddr2) is the raw FreeBSD umtx syscall
// multiplexer the PS5 C runtime exposes (NID 04AjkP0jO9U). These tests pin
// the futex contract for the ops the guest runtimes actually drive: the
// compare-and-wait immediate/timeout/host-park paths, the wake interop with
// the sceKernelSyncOnAddress key space, and the mutex/rwlock/semaphore state
// machines over the guest lock words.
public sealed class KernelUmtxCompatExportsTests
{
    private const ulong MemoryBase = 0x1_0000_0000;
    private const ulong WaitAddress = MemoryBase + 0x100;
    private const ulong TimeoutAddress = MemoryBase + 0x200;
    private const ulong MutexAddress = MemoryBase + 0x300;

    // Internal errno values are exposed through TLS by the libc entry.
    private const int ErrnoOk = 0;

    private const int OpWait = 2;
    private const int OpWake = 3;
    private const int OpMutexTrylock = 4;
    private const int OpMutexLock = 5;
    private const int OpMutexUnlock = 6;
    private const int OpWaitUint = 11;
    private const int OpRwRdlock = 12;
    private const int OpRwWrlock = 13;
    private const int OpRwUnlock = 14;

    private const uint UmutexContested = 0x80000000u;
    private const uint UrwlockWriteOwner = 0x80000000u;

    [Theory]
    [InlineData(OpWait)]
    [InlineData(OpWaitUint)]
    public void WaitReturnsSuccessWhenValueAlreadyDiffers(int op)
    {
        var (memory, ctx) = CreateContext();
        WriteUInt32(memory, WaitAddress, 0x2A);
        SetUmtxArguments(ctx, WaitAddress, op, expected: 0x2B, timeoutAddress: 0);

        var result = KernelUmtxCompatExports.UmtxOp(ctx);

        // The word moved before the wait; return so the caller can re-read.
        Assert.Equal(ErrnoOk, result);
    }

    [Theory]
    [InlineData(OpWait)]
    [InlineData(OpWaitUint)]
    public void WaitWithTimespecTimeoutReturnsEtimedout(int op)
    {
        var (memory, ctx) = CreateContext();
        WriteUInt32(memory, WaitAddress, 0x2A);
        // struct timespec { tv_sec; tv_nsec; } — 30 ms relative wait.
        WriteUInt64(memory, TimeoutAddress, 0);
        WriteUInt64(memory, TimeoutAddress + 8, 30_000_000);
        SetUmtxArguments(ctx, WaitAddress, op, expected: 0x2A, TimeoutAddress);

        var stopwatch = Stopwatch.StartNew();
        var result = KernelUmtxCompatExports.UmtxOp(ctx);
        stopwatch.Stop();

        Assert.Equal(-1, result);
        Assert.True(stopwatch.ElapsedMilliseconds >= 20, $"wait returned early: {stopwatch.ElapsedMilliseconds} ms");
        Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(10));
    }

    [Fact]
    public void WaitUintComparesOnlyTheLow32Bits()
    {
        var (memory, ctx) = CreateContext();
        // The stored value differs from 0 only beyond the 32-bit compare
        // width, so a WAIT_UINT must keep waiting (into the timeout) while a
        // 64-bit WAIT would return immediately.
        WriteUInt64(memory, WaitAddress, 1UL << 32);
        WriteUInt64(memory, TimeoutAddress, 0);
        WriteUInt64(memory, TimeoutAddress + 8, 20_000_000);
        SetUmtxArguments(ctx, WaitAddress, OpWaitUint, expected: 0, TimeoutAddress);

        var result = KernelUmtxCompatExports.UmtxOp(ctx);

        Assert.Equal(-1, result);
    }

    [Fact]
    public void WaitParksUntilAnotherThreadChangesValueAndWakes()
    {
        var (memory, ctx) = CreateContext();
        WriteUInt64(memory, WaitAddress, 0);
        SetUmtxArguments(ctx, WaitAddress, OpWait, expected: 0, timeoutAddress: 0);

        var changed = new ManualResetEventSlim();
        var changer = Task.Run(() =>
        {
            Thread.Sleep(100);
            WriteUInt64(memory, WaitAddress, 0x1234);
            // The wake arrives through the shared address-wait key space.
            SetUmtxArguments(ctx, WaitAddress, OpWake, val: 1, timeoutAddress: 0);
            _ = KernelUmtxCompatExports.UmtxOp(ctx);
            changed.Set();
        });

        var stopwatch = Stopwatch.StartNew();
        var result = KernelUmtxCompatExports.UmtxOp(ctx);
        stopwatch.Stop();

        Assert.True(changed.IsSet, "wait returned before the value changed");
        Assert.Equal(ErrnoOk, result);
        Assert.True(stopwatch.ElapsedMilliseconds >= 90, $"wait returned early: {stopwatch.ElapsedMilliseconds} ms");
        Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(10));
        Assert.True(changer.Wait(TimeSpan.FromSeconds(5)));
    }

    [Fact]
    public void WakeWithoutWaitersReturnsOk()
    {
        var (_, ctx) = CreateContext();
        SetUmtxArguments(ctx, WaitAddress, OpWake, val: int.MaxValue, timeoutAddress: 0);

        Assert.Equal(ErrnoOk, KernelUmtxCompatExports.UmtxOp(ctx));
    }

    [Fact]
    public void CooperativeWaiterStaysParkedWhileValueMatchesAndResumesWhenItDiffers()
    {
        var (memory, ctx) = CreateContext();
        WriteUInt64(memory, WaitAddress, 7);
        SetUmtxArguments(ctx, WaitAddress, OpWait, expected: 7, timeoutAddress: 0);

        var waiter = StageUmtxWait(ctx, threadHandle: 0xA10, expectedReason: "_umtx_op_wait");

        // A wake while the value still matches must keep the waiter parked:
        // futex waiters may only be released by a value change (or a
        // spurious wakeup the caller re-evaluates).
        Assert.False(waiter.TryWake());

        WriteUInt64(memory, WaitAddress, 8);
        Assert.True(waiter.TryWake());
        Assert.Equal(ErrnoOk, waiter.Resume());
    }

    [Fact]
    public void MutexTrylockLockUnlockRoundTrip()
    {
        var (memory, ctx) = CreateContext();
        WriteUInt32(memory, MutexAddress, 0);

        SetUmtxArguments(ctx, MutexAddress, OpMutexTrylock);
        Assert.Equal(ErrnoOk, KernelUmtxCompatExports.UmtxOp(ctx));
        var owner = ReadUInt32(memory, MutexAddress);
        Assert.NotEqual(0u, owner);
        Assert.Equal(0u, owner & UmutexContested);

        // A second trylock while held reports EBUSY.
        SetUmtxArguments(ctx, MutexAddress, OpMutexTrylock);
        Assert.Equal(-1, KernelUmtxCompatExports.UmtxOp(ctx));

        SetUmtxArguments(ctx, MutexAddress, OpMutexUnlock);
        Assert.Equal(ErrnoOk, KernelUmtxCompatExports.UmtxOp(ctx));
        Assert.Equal(0u, ReadUInt32(memory, MutexAddress));

        // Free again after the unlock.
        SetUmtxArguments(ctx, MutexAddress, OpMutexTrylock);
        Assert.Equal(ErrnoOk, KernelUmtxCompatExports.UmtxOp(ctx));
    }

    [Fact]
    public void MutexLockParksUntilUnlockAndThenAcquires()
    {
        var (memory, ctx) = CreateContext();
        WriteUInt32(memory, MutexAddress, 0x70000); // held by another tid

        SetUmtxArguments(ctx, MutexAddress, OpMutexLock, timeoutAddress: 0);

        var waiter = StageUmtxWait(ctx, threadHandle: 0xA11, expectedReason: "_umtx_op_mutex_lock");

        // Still held: the wake predicate must keep the waiter parked.
        Assert.False(waiter.TryWake());

        // The unlock clears the owner word and wakes the address.
        WriteUInt32(memory, MutexAddress, 0);
        SetUmtxArguments(ctx, MutexAddress, OpMutexUnlock);
        Assert.Equal(ErrnoOk, KernelUmtxCompatExports.UmtxOp(ctx));

        Assert.True(waiter.TryWake());
        Assert.Equal(ErrnoOk, waiter.Resume());
        // The resume handler completed the acquisition: the owner word is
        // set again (by the parking thread's tid).
        Assert.NotEqual(0u, ReadUInt32(memory, MutexAddress));
    }

    [Fact]
    public void RwLockWriterBlocksReadersUntilUnlock()
    {
        var (memory, ctx) = CreateContext();
        WriteUInt32(memory, WaitAddress, 0);

        // Writer acquires the free lock.
        SetUmtxArguments(ctx, WaitAddress, OpRwWrlock);
        Assert.Equal(ErrnoOk, KernelUmtxCompatExports.UmtxOp(ctx));
        Assert.Equal(UrwlockWriteOwner, ReadUInt32(memory, WaitAddress));

        // A reader must not acquire while the writer holds it: park the
        // reader cooperatively and verify it stays parked.
        SetUmtxArguments(ctx, WaitAddress, OpRwRdlock, timeoutAddress: 0);
        var reader = StageUmtxWait(ctx, threadHandle: 0xA12, expectedReason: "_umtx_op_rw_rdlock");
        Assert.False(reader.TryWake());

        // Unlock releases the writer bit; the reader resumes and acquires.
        SetUmtxArguments(ctx, WaitAddress, OpRwUnlock);
        Assert.Equal(ErrnoOk, KernelUmtxCompatExports.UmtxOp(ctx));
        Assert.Equal(0u, ReadUInt32(memory, WaitAddress));

        Assert.True(reader.TryWake());
        Assert.Equal(ErrnoOk, reader.Resume());
        // The reader completion incremented the reader count.
        Assert.Equal(1u, ReadUInt32(memory, WaitAddress));
    }

    private static (FakeCpuMemory Memory, CpuContext Context) CreateContext()
    {
        var memory = new FakeCpuMemory(MemoryBase, 0x1000);
        return (memory, new CpuContext(memory, Generation.Gen5));
    }

    private static void SetUmtxArguments(
        CpuContext ctx,
        ulong address,
        int op,
        ulong expected = 0,
        ulong timeoutAddress = 0,
        ulong val = 0,
        ulong uaddr2 = 0)
    {
        ctx[CpuRegister.Rdi] = address;
        ctx[CpuRegister.Rsi] = unchecked((ulong)op);
        ctx[CpuRegister.Rdx] = expected != 0 ? expected : val;
        ctx[CpuRegister.Rcx] = op is OpWait or OpWaitUint ? 0 : timeoutAddress;
        ctx[CpuRegister.R8] = op is OpWait or OpWaitUint ? timeoutAddress : uaddr2;
    }

    // Stages the cooperative block the way a guest thread under the scheduler
    // would: the export returns after registering the block, and the test
    // drives the resulting waiter directly (pattern of
    // KernelSyncOnAddressWaitTests.StageAddressWait).
    private static IGuestThreadBlockWaiter StageUmtxWait(CpuContext ctx, ulong threadHandle, string expectedReason)
    {
        var previousThread = GuestThreadExecution.EnterGuestThread(threadHandle);
        var previousFrame = GuestThreadExecution.EnterImportCallFrame(
            returnRip: 0x1_0000 + threadHandle,
            resumeRsp: 0x2_0000 + threadHandle,
            returnSlotAddress: 0x3_0000 + threadHandle);
        try
        {
            Assert.Equal(ErrnoOk, KernelUmtxCompatExports.UmtxOp(ctx));

            Assert.True(GuestThreadExecution.TryConsumeCurrentThreadBlock(
                out var reason,
                out _,
                out _,
                out _,
                out var waiter,
                out _));
            Assert.Equal(expectedReason, reason);
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
