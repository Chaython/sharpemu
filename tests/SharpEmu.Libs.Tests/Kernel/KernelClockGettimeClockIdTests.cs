// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.HLE;
using SharpEmu.Libs.Kernel;
using Xunit;

namespace SharpEmu.Libs.Tests.Kernel;

// libc clock_gettime must resolve the clock id exactly like
// sceKernelClockGettime: id 0 (CLOCK_REALTIME) is the UTC wall clock, every
// other id (CLOCK_MONOTONIC=1, CLOCK_PROCESS_CPUTIME_ID=2, ...) is the
// process-relative monotonic clock. Guests mix both exports (libc++
// steady_clock reads CLOCK_MONOTONIC through clock_gettime), and mixing the
// two sources hands them wall-vs-monotonic deltas of ~1.7e9 s that fire or
// stall every deadline computed from them.
public sealed class KernelClockGettimeClockIdTests
{
    private const ulong MemoryBase = 0x1_0000_0000;
    private const ulong TimespecAddress = MemoryBase + 0x100;
    private const ulong SecondTimespecAddress = MemoryBase + 0x120;

    [Fact]
    public void RealtimeClockIdReturnsNonzeroWallTimeThatAdvances()
    {
        var (_, ctx) = CreateContext();
        var first = InvokeLibcClockGettime(ctx, clockId: 0, TimespecAddress);

        // Any date from late 2020 onwards; the sandbox clock is far past it.
        Assert.True(first.Seconds > 1_600_000_000L, $"realtime tv_sec={first.Seconds}");
        Assert.InRange(first.Nanoseconds, 0L, 999_999_999L);

        // The wall clock ticks at 100 ns granularity, so wait until a later
        // sample actually advances instead of comparing two identical reads.
        var advanced = false;
        var deadline = Environment.TickCount64 + 5_000;
        while (Environment.TickCount64 < deadline)
        {
            var next = InvokeLibcClockGettime(ctx, clockId: 0, TimespecAddress);
            if (next.Seconds > first.Seconds ||
                (next.Seconds == first.Seconds && next.Nanoseconds > first.Nanoseconds))
            {
                advanced = true;
                break;
            }
        }

        Assert.True(advanced, "CLOCK_REALTIME did not advance within 5 s");
    }

    [Theory]
    [InlineData(1)] // CLOCK_MONOTONIC
    [InlineData(2)] // CLOCK_PROCESS_CPUTIME_ID
    public void NonRealtimeClockIdsReturnProcessRelativeMonotonicTime(int clockId)
    {
        var (_, ctx) = CreateContext();
        var first = InvokeLibcClockGettime(ctx, clockId, TimespecAddress);
        var second = InvokeLibcClockGettime(ctx, clockId, TimespecAddress);

        Assert.InRange(first.Nanoseconds, 0L, 999_999_999L);
        Assert.InRange(second.Nanoseconds, 0L, 999_999_999L);
        Assert.True(
            second.Seconds > first.Seconds ||
            (second.Seconds == first.Seconds && second.Nanoseconds >= first.Nanoseconds),
            $"monotonic time went backwards: {first} then {second}");

        // Process-relative: nowhere near the 2026 wall clock (~1.78e9 s).
        var wall = InvokeLibcClockGettime(ctx, clockId: 0, TimespecAddress);
        Assert.True(
            wall.Seconds - second.Seconds > 10_000_000L,
            $"clock {clockId} returned wall time: {second.Seconds} vs wall {wall.Seconds}");
    }

    [Fact]
    public void LibcAndSceKernelClockGettimeShareTheMonotonicClockSource()
    {
        var (_, ctx) = CreateContext();
        var fromLibc = InvokeLibcClockGettime(ctx, clockId: 1, TimespecAddress);
        var fromKernel = InvokeSceKernelClockGettime(ctx, clockId: 1, SecondTimespecAddress);

        // Both are process-relative from the same Stopwatch base, so the two
        // reads must land within a second of each other, not 1.7e9 s apart.
        Assert.True(
            Math.Abs(fromKernel.Seconds - fromLibc.Seconds) <= 60L,
            $"clock sources disagree: libc={fromLibc.Seconds} kernel={fromKernel.Seconds}");
    }

    [Fact]
    public void TimespecIsWrittenAsLongSecondsThenLongNanoseconds()
    {
        var (memory, ctx) = CreateContext();
        PoisonTimespec(memory, TimespecAddress);

        var result = InvokeLibcClockGettime(ctx, clockId: 1, TimespecAddress);

        // The 0xAA poison pattern would decode as a negative tv_sec or a
        // tv_nsec far beyond one second; both fields must have been rewritten.
        Assert.True(result.Seconds >= 0L, $"tv_sec not rewritten: {result.Seconds}");
        Assert.InRange(result.Nanoseconds, 0L, 999_999_999L);
    }

    private static (FakeCpuMemory Memory, CpuContext Context) CreateContext()
    {
        var memory = new FakeCpuMemory(MemoryBase, 0x1000);
        return (memory, new CpuContext(memory, Generation.Gen5));
    }

    private static (long Seconds, long Nanoseconds) InvokeLibcClockGettime(
        CpuContext ctx,
        int clockId,
        ulong timespecAddress)
    {
        // clock_gettime(clockid_t clk_id, struct timespec *tp): rdi id, rsi out.
        ctx[CpuRegister.Rdi] = unchecked((ulong)(uint)clockId);
        ctx[CpuRegister.Rsi] = timespecAddress;
        Assert.Equal((int)OrbisGen2Result.ORBIS_GEN2_OK, KernelMemoryCompatExports.ClockGettime(ctx));
        Assert.Equal(0UL, ctx[CpuRegister.Rax]);
        return ReadTimespec(ctx, timespecAddress);
    }

    private static (long Seconds, long Nanoseconds) InvokeSceKernelClockGettime(
        CpuContext ctx,
        int clockId,
        ulong timespecAddress)
    {
        ctx[CpuRegister.Rdi] = unchecked((ulong)(uint)clockId);
        ctx[CpuRegister.Rsi] = timespecAddress;
        Assert.Equal((int)OrbisGen2Result.ORBIS_GEN2_OK, KernelRuntimeCompatExports.KernelClockGettime(ctx));
        Assert.Equal(0UL, ctx[CpuRegister.Rax]);
        return ReadTimespec(ctx, timespecAddress);
    }

    private static (long Seconds, long Nanoseconds) ReadTimespec(CpuContext ctx, ulong timespecAddress)
    {
        Assert.True(ctx.TryReadUInt64(timespecAddress, out var rawSeconds));
        Assert.True(ctx.TryReadUInt64(timespecAddress + sizeof(ulong), out var rawNanoseconds));
        return (unchecked((long)rawSeconds), unchecked((long)rawNanoseconds));
    }

    private static void PoisonTimespec(FakeCpuMemory memory, ulong address)
    {
        Span<byte> poison = stackalloc byte[2 * sizeof(ulong)];
        poison.Fill(0xAA);
        Assert.True(memory.TryWrite(address, poison));
    }
}
