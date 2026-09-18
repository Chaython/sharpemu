// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Reflection;
using SharpEmu.HLE;
using SharpEmu.Libs.Kernel;
using Xunit;

namespace SharpEmu.Libs.Tests.Pthread;

[CollectionDefinition("Host wait scheduler", DisableParallelization = true)]
public sealed class HostWaitSchedulerCollection;

[Collection("Host wait scheduler")]
public sealed class PthreadHostWaitExceptionTests
{
    [Theory]
    [InlineData(2)]
    [InlineData(11)]
    public void HostAddressWaitServicesExceptionsOutsideItsWakeGate(int operation)
    {
        const ulong memoryBase = 0x3_2100_0000;
        const ulong address = memoryBase + 0x100;
        const ulong timeout = memoryBase + 0x200;
        var memory = new FakeCpuMemory(memoryBase, 0x1000);
        var ctx = new CpuContext(memory, Generation.Gen5);
        Assert.True(ctx.TryWriteUInt64(timeout + 8, 200_000_000));
        ctx[CpuRegister.Rdi] = address;
        ctx[CpuRegister.Rsi] = (ulong)operation;
        ctx[CpuRegister.R8] = timeout;
        var scheduler = DispatchProxy.Create<IGuestThreadScheduler, ExceptionScheduler>();
        var delivered = false;
        ((ExceptionScheduler)scheduler).Deliver = () =>
        {
            if (delivered) return;
            delivered = true;
            var wake = Task.Run(() =>
            {
                var other = new CpuContext(memory, Generation.Gen5);
                Assert.True(other.TryWriteUInt64(address, 1));
                other[CpuRegister.Rdi] = address;
                other[CpuRegister.Rsi] = 3;
                other[CpuRegister.Rdx] = 1;
                Assert.Equal(0, KernelUmtxCompatExports.UmtxOp(other));
            });
            Assert.True(wake.Wait(TimeSpan.FromSeconds(2)), "Exception delivery held the wake gate.");
        };
        var previousScheduler = GuestThreadExecution.Scheduler;
        try
        {
            GuestThreadExecution.Scheduler = scheduler;
            Assert.Equal(0, KernelUmtxCompatExports.UmtxOp(ctx));
            Assert.True(delivered);
        }
        finally
        {
            GuestThreadExecution.Scheduler = previousScheduler;
        }
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void HostConditionWaitServicesExceptionsWithoutConsumingTheCondition(bool signalCondition)
    {
        const ulong memoryBase = 0x3_2000_0000;
        const ulong mutex = memoryBase + 0x100;
        const ulong cond = memoryBase + 0x200;
        var memory = new FakeCpuMemory(memoryBase, 0x10000);
        var ctx = new CpuContext(memory, Generation.Gen5);
        ctx[CpuRegister.Rdi] = mutex;
        Assert.Equal(0, KernelPthreadCompatExports.PthreadMutexInit(ctx));
        Assert.Equal(0, KernelPthreadCompatExports.PthreadMutexLock(ctx));
        ctx[CpuRegister.Rdi] = cond;
        Assert.Equal(0, KernelPthreadCompatExports.PthreadCondInit(ctx));

        var scheduler = DispatchProxy.Create<IGuestThreadScheduler, ExceptionScheduler>();
        var proxy = (ExceptionScheduler)scheduler;
        var delivered = false;
        proxy.Deliver = () =>
        {
            if (delivered) return;
            delivered = true;
            // An exception acknowledgement lets the other guest thread signal
            // startup. It must be able to acquire both synchronization locks.
            if (signalCondition)
            {
                var signal = Task.Run(() =>
                {
                    var other = new CpuContext(memory, Generation.Gen5);
                    other[CpuRegister.Rdi] = mutex;
                    Assert.Equal(0, KernelPthreadCompatExports.PthreadMutexLock(other));
                    other[CpuRegister.Rdi] = cond;
                    Assert.Equal(0, KernelPthreadCompatExports.PthreadCondSignal(other));
                    other[CpuRegister.Rdi] = mutex;
                    Assert.Equal(0, KernelPthreadCompatExports.PthreadMutexUnlock(other));
                });
                Assert.True(signal.Wait(TimeSpan.FromSeconds(2)), "Exception delivery held a synchronization lock.");
            }
        };

        var previousScheduler = GuestThreadExecution.Scheduler;
        try
        {
            GuestThreadExecution.Scheduler = scheduler;
            ctx[CpuRegister.Rdi] = cond;
            ctx[CpuRegister.Rsi] = mutex;
            ctx[CpuRegister.Rdx] = 200_000;
            var result = KernelPthreadCompatExports.PthreadCondTimedwait(ctx);

            Assert.True(delivered, "The waiting thread never serviced its pending exception.");
            Assert.Equal(signalCondition ? 0 : (int)OrbisGen2Result.ORBIS_GEN2_ERROR_TIMED_OUT, result);
            ctx[CpuRegister.Rdi] = mutex;
            Assert.Equal(0, KernelPthreadCompatExports.PthreadMutexUnlock(ctx));
        }
        finally
        {
            GuestThreadExecution.Scheduler = previousScheduler;
            ctx[CpuRegister.Rdi] = cond;
            KernelPthreadCompatExports.PthreadCondDestroy(ctx);
            ctx[CpuRegister.Rdi] = mutex;
            KernelPthreadCompatExports.PthreadMutexDestroy(ctx);
        }
    }

    public class ExceptionScheduler : DispatchProxy
    {
        public Action? Deliver { get; set; }

        protected override object? Invoke(MethodInfo? method, object?[]? args)
        {
            if (method?.Name == "PollPendingExceptions") Deliver?.Invoke();
            if (method?.Name == nameof(IGuestThreadScheduler.WakeBlockedThreads)) return 0;
            return method?.ReturnType == typeof(void) ? null : throw new NotSupportedException(method?.Name);
        }
    }
}
