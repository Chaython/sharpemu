// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Reflection;
using SharpEmu.Core.Cpu.Native;
using SharpEmu.Core.Loader;
using SharpEmu.Core.Memory;
using SharpEmu.HLE;
using SharpEmu.Libs.Kernel;
using Xunit;

namespace SharpEmu.Libs.Tests.Pthread;

[Collection("Host wait scheduler")]
public sealed class PthreadStackAttributesTests
{
    [Fact]
    public void RegisteredStackMappingSurvivesCallbackContextRegistration()
    {
        const ulong thread = 0x74567;
        const ulong stackBase = 0x7FFF_AB00_0000;
        const ulong stackSize = 0x40_0000;
        const ulong callbackBase = 0x7FFF_AA00_0000;
        var memory = new VirtualMemory();
        memory.Map(stackBase, stackSize, 0, [], ProgramHeaderFlags.Read | ProgramHeaderFlags.Write);
        memory.Map(callbackBase, 0x10000, 0, [], ProgramHeaderFlags.Read | ProgramHeaderFlags.Write);
        var ctx = new CpuContext(memory, Generation.Gen5);
        ctx[CpuRegister.Rsp] = stackBase + stackSize - 8;
        using var backend = new DirectExecutionBackend(new ModuleManager());
        backend.RegisterGuestThreadContext(thread, ctx);
        var callback = new CpuContext(memory, Generation.Gen5);
        callback[CpuRegister.Rsp] = callbackBase + 0xFFF8;
        backend.RegisterGuestThreadContext(thread, callback);

        Assert.True(backend.TryGetGuestThreadStack(thread, out var actualBase, out var actualSize));
        Assert.Equal(stackBase, actualBase);
        Assert.Equal(stackSize, actualSize);
        Assert.False(backend.TryGetGuestThreadStack(thread + 1, out _, out _));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void AttributesUseTheTargetThreadsMappedStackBeyondTheFirst64Slots(bool querySelf)
    {
        const ulong memoryBase = 0x3_2200_0000;
        const ulong thread = 0x71234;
        const ulong stackBase = 0x7FFF_AB00_0000;
        const ulong stackSize = 0x20_0000;
        const ulong attr = memoryBase + 0x100;
        var ctx = new CpuContext(new FakeCpuMemory(memoryBase, 0x1000), Generation.Gen5);
        // Queries made from callbacks must also describe the pthread's original
        // stack, even when the current stack pointer belongs to another region.
        ctx[CpuRegister.Rsp] = 0x7FFF_D100_FF00;
        var scheduler = DispatchProxy.Create<IGuestThreadScheduler, StackScheduler>();
        ((StackScheduler)scheduler).Query = (target, args) =>
        {
            Assert.Equal(thread, target);
            args[1] = stackBase;
            args[2] = stackSize;
            return true;
        };
        var previousScheduler = GuestThreadExecution.Scheduler;
        var previousThread = GuestThreadExecution.EnterGuestThread(querySelf ? thread : thread + 1);
        try
        {
            GuestThreadExecution.Scheduler = scheduler;
            ctx[CpuRegister.Rdi] = thread;
            ctx[CpuRegister.Rsi] = attr;
            Assert.Equal(0, KernelPthreadExtendedCompatExports.PthreadAttrGet(ctx));
            ctx[CpuRegister.Rdi] = attr;
            ctx[CpuRegister.Rsi] = memoryBase + 0x200;
            ctx[CpuRegister.Rdx] = memoryBase + 0x208;
            Assert.Equal(0, KernelPthreadExtendedCompatExports.PthreadAttrGetstack(ctx));
            Assert.True(ctx.TryReadUInt64(memoryBase + 0x200, out var actualBase));
            Assert.True(ctx.TryReadUInt64(memoryBase + 0x208, out var actualSize));
            Assert.Equal(stackBase, actualBase);
            Assert.Equal(stackSize, actualSize);
        }
        finally
        {
            GuestThreadExecution.RestoreGuestThread(previousThread);
            GuestThreadExecution.Scheduler = previousScheduler;
        }
    }

    public class StackScheduler : DispatchProxy
    {
        public Func<ulong, object?[], bool>? Query { get; set; }
        protected override object? Invoke(MethodInfo? method, object?[]? args) =>
            method?.Name == "TryGetGuestThreadStack"
                ? Query!((ulong)args![0]!, args)
                : throw new NotSupportedException(method?.Name);
    }
}
