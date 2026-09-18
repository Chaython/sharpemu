// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.HLE;
using Xunit;

namespace SharpEmu.Libs.Tests.Cpu;

public sealed class GuestCallbackPendingBlockTests
{
    [Fact]
    public void CallbackImportCannotConsumeTheInterruptedImportsBlock()
    {
        var previousThread = GuestThreadExecution.EnterGuestThread(0x1234);
        try
        {
            var outerWaiter = new Waiter();
            StageBlock("outer_umtx", "address:1", 0x10000, 0x20000, 0x30000, 123, outerWaiter);

            WithSuspendedPendingBlock(() =>
            {
                Assert.False(GuestThreadExecution.TryConsumeCurrentThreadBlock(out _));
                var callbackWaiter = new Waiter();
                StageBlock("callback_event", "event:2", 0x40000, 0x50000, 0x60000, 456, callbackWaiter);
                AssertBlock("callback_event", "event:2", 0x40000, 0x50000, 0x60000, 456, callbackWaiter);
            });

            AssertBlock("outer_umtx", "address:1", 0x10000, 0x20000, 0x30000, 123, outerWaiter);
            Assert.False(GuestThreadExecution.TryConsumeCurrentThreadBlock(out _));
        }
        finally
        {
            GuestThreadExecution.RestoreGuestThread(previousThread);
        }
    }

    [Fact]
    public void FailingCallbackRestoresTheInterruptedBlockWithoutLeakingItsOwnRequest()
    {
        var previousThread = GuestThreadExecution.EnterGuestThread(0x1234);
        try
        {
            var outerWaiter = new Waiter();
            StageBlock("outer_umtx", "address:1", 0x10000, 0x20000, 0x30000, 123, outerWaiter);

            Assert.Throws<InvalidOperationException>(() => WithSuspendedPendingBlock(() =>
            {
                StageBlock("callback_event", "event:2", 0x40000, 0x50000, 0x60000, 456, new Waiter());
                throw new InvalidOperationException("callback failed");
            }));

            AssertBlock("outer_umtx", "address:1", 0x10000, 0x20000, 0x30000, 123, outerWaiter);
        }
        finally
        {
            GuestThreadExecution.RestoreGuestThread(previousThread);
        }
    }

    private static void WithSuspendedPendingBlock(Action callback)
    {
        using var scope = GuestThreadExecution.SuspendCurrentThreadBlock();
        callback();
    }

    private static void StageBlock(string reason, string wakeKey, ulong rip, ulong rsp, ulong slot, long deadline, Waiter waiter)
    {
        var context = new CpuContext(new FakeCpuMemory(0x10000, 0x1000), Generation.Gen5);
        context[CpuRegister.Rbx] = 0xABCD;
        var previousFrame = GuestThreadExecution.EnterImportCallFrame(rip, rsp, slot);
        try
        {
            Assert.True(GuestThreadExecution.RequestCurrentThreadBlock(context, reason, wakeKey, waiter, deadline));
        }
        finally
        {
            GuestThreadExecution.RestoreImportCallFrame(previousFrame);
        }
    }

    private static void AssertBlock(string expectedReason, string expectedWakeKey, ulong expectedRip, ulong expectedRsp, ulong expectedSlot, long expectedDeadline, Waiter expectedWaiter)
    {
        Assert.True(GuestThreadExecution.TryConsumeCurrentThreadBlock(out var reason, out var continuation,
            out var hasContinuation, out var wakeKey, out var waiter, out var deadline));
        Assert.Equal(expectedReason, reason);
        Assert.Equal(expectedWakeKey, wakeKey);
        Assert.True(hasContinuation);
        Assert.Equal(expectedRip, continuation.Rip);
        Assert.Equal(expectedRsp, continuation.Rsp);
        Assert.Equal(expectedSlot, continuation.ReturnSlotAddress);
        Assert.Equal(0xABCDUL, continuation.Rbx);
        Assert.Same(expectedWaiter, waiter);
        Assert.Equal(expectedDeadline, deadline);
    }

    private sealed class Waiter : IGuestThreadBlockWaiter
    {
        public bool TryWake() => false;
        public int Resume() => 0;
    }
}
