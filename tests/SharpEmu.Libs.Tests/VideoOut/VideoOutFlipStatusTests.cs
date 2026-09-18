// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.HLE;
using SharpEmu.Libs.Kernel;
using SharpEmu.Libs.VideoOut;
using Xunit;

namespace SharpEmu.Libs.Tests.VideoOut;

[Collection("Host wait scheduler")]
public sealed class VideoOutFlipStatusTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void CompletedFlipPreservesFullIdentifierAndStatusLayout(bool fromAgc)
    {
        const ulong memoryBase = 0x3_2500_0000;
        const ulong status = memoryBase + 0x100;
        var ctx = new CpuContext(new FakeCpuMemory(memoryBase, 0x1000), Generation.Gen5);
        KernelRuntimeCompatExports.KernelGetProcessTime(ctx);
        Thread.Sleep(10); // The video port opens after the process clock starts.
        var handle = VideoOutExports.VideoOutOpen(ctx);
        Assert.True(handle > 0);
        try
        {
            for (ulong sequence = 0; sequence < 3; sequence++)
            {
                // Unity uses bit 63 to distinguish frame completion identifiers.
                var argument = 0x8000_0000_0000_0000UL | sequence;
                KernelRuntimeCompatExports.KernelReadTsc(ctx);
                var beforeTsc = ctx[CpuRegister.Rax];
                KernelRuntimeCompatExports.KernelGetProcessTime(ctx);
                var beforeProcessTime = ctx[CpuRegister.Rax];
                ctx[CpuRegister.Rdi] = (ulong)handle;
                ctx[CpuRegister.Rsi] = ulong.MaxValue; // Blank buffer: no GPU needed.
                ctx[CpuRegister.Rdx] = 1;
                ctx[CpuRegister.Rcx] = argument;
                Assert.Equal(0, fromAgc
                    ? VideoOutExports.SubmitFlipFromAgc(ctx, handle, -1, 1, unchecked((long)argument))
                    : VideoOutExports.VideoOutSubmitFlip(ctx));
                KernelRuntimeCompatExports.KernelReadTsc(ctx);
                var afterTsc = ctx[CpuRegister.Rax];
                KernelRuntimeCompatExports.KernelGetProcessTime(ctx);
                var afterProcessTime = ctx[CpuRegister.Rax];

                Assert.True(ctx.Memory.TryWrite(status, Enumerable.Repeat((byte)0xA5, 0x48).ToArray()));
                ctx[CpuRegister.Rsi] = status;
                Assert.Equal(0, VideoOutExports.VideoOutGetFlipStatus(ctx));
                Assert.True(ctx.TryReadUInt64(status + 0x18, out var completedArgument));
                Assert.Equal(argument, completedArgument);
                Assert.True(ctx.TryReadUInt64(status, out var count));
                Assert.Equal(sequence + 1, count);
                Assert.True(ctx.TryReadUInt64(status + 0x08, out var processTime));
                Assert.InRange(processTime, beforeProcessTime, afterProcessTime);
                Assert.True(ctx.TryReadUInt64(status + 0x10, out var timestamp));
                Assert.InRange(timestamp, beforeTsc, afterTsc);
                Assert.True(ctx.TryReadUInt64(status + 0x20, out var submittedTimestamp));
                Assert.InRange(submittedTimestamp, beforeTsc, timestamp);
                Assert.True(ctx.TryReadUInt64(status + 0x30, out var queueCounts));
                Assert.Equal(0UL, queueCounts);
                Assert.True(ctx.TryReadUInt32(status + 0x38, out var currentBuffer));
                Assert.Equal(uint.MaxValue, currentBuffer);
                Assert.True(ctx.TryReadUInt64(status + 0x40, out var guard));
                Assert.Equal(0xA5A5_A5A5_A5A5_A5A5UL, guard);
            }
        }
        finally
        {
            ctx[CpuRegister.Rdi] = (ulong)handle;
            VideoOutExports.VideoOutClose(ctx);
        }
    }
}
