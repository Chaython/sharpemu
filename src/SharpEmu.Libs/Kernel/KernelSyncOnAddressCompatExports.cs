// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Threading;
using SharpEmu.HLE;

namespace SharpEmu.Libs.Kernel;

// libKernel's address-wait primitives (sceKernelSyncOnAddress*) are the PS5's
// futex-style wait/wake: a thread parks on a guest address until the value at
// that address no longer equals the expected value. Guest runtimes (seen
// driving Juicy Realm, PPSA19268) build their own spinlocks/queues on top of
// it and call the wait in a hot loop; left unimplemented, every wait returns
// immediately and the runtime busy-spins forever (millions of calls, no
// forward progress).
//
// This implements the wait as a compare-and-wait over the existing
// cooperative-block scheduler, keyed on the address: wait(addr, expectedValue,
// size, *pTimeout) returns at once when the value already differs, and a
// parked waiter is only released once the guest value no longer matches (or
// its timeout expires). The value is re-checked in the wake predicate, on
// every resume, and on every poll of the non-cooperative host path, so a
// missed or early wake cannot release a waiter whose value has not changed —
// that is what lock-free guest queues built on this primitive rely on. A
// matching wake releases waiters immediately through the same key; the
// per-address generation it bumps closes the register-vs-park race for the
// host path, and infinite waits keep a bounded self-heal deadline so a wait
// that genuinely missed everything still returns as a spurious wakeup the
// guest re-evaluates.
public static class KernelSyncOnAddressCompatExports
{
    // Safety-net poll interval for waits without a guest timeout. Real
    // releases come from the wake side (generation bump +
    // WakeBlockedThreads); this only bounds how long a wait that genuinely
    // raced/missed its wake stays parked before the guest re-evaluates its own
    // condition, which futex callers already tolerate. Kept large: a short
    // interval turns every parked waiter into a hot re-poll that steals
    // scheduler bandwidth from the threads that actually make progress
    // (including the ones that would issue the wake), so it must be a rare
    // last resort, not a spin substitute.
    // Internal so the umtx compat exports (KernelUmtxCompatExports) share
    // the same self-heal cadence for its infinite waits.
    internal static readonly TimeSpan WaitSelfHealTimeout = TimeSpan.FromMilliseconds(100);

    // Poll slice for the non-cooperative (host main thread) fallback, which
    // cannot use the guest-thread scheduler's block mechanism and instead
    // re-reads the guest value each time Monitor.Wait returns. Sleeping in
    // these slices keeps a parked host waiter cheap while bounding how long a
    // value change that was not accompanied by a wake call can go unnoticed.
    internal const int HostWaitPollMilliseconds = 5;

    // Per-address host gate for the non-cooperative (host main thread) fallback,
    // which cannot use the guest-thread scheduler's block mechanism.
    private static readonly ConcurrentDictionary<ulong, object> _hostAddressGates = new();

    // Per-address wake generation. A wait captures the current generation and
    // consumes a bump as "re-check the value now" in the host fallback loop,
    // which closes the register-vs-park race for free: a wake landing in that
    // window bumps the generation, so the waiter re-reads the value instead of
    // sleeping past the release.
    private static readonly ConcurrentDictionary<ulong, long> _wakeGenerations = new();

    internal static long CurrentGeneration(ulong address) =>
        _wakeGenerations.TryGetValue(address, out var generation) ? generation : 0;

    internal static string WakeKey(ulong address) => $"sceKernelSyncOnAddress:{address:X16}";

    internal static object GetOrAddHostAddressGate(ulong address) =>
        _hostAddressGates.GetOrAdd(address, static _ => new object());

    /// <summary>
    /// Releases up to <paramref name="wakeCount"/> waiters parked on a guest
    /// address through any of the address-wait primitives (this class or the
    /// raw _umtx_op exports, which share the wake-key space so a wake issued
    /// through either API releases the other's waiters).
    /// </summary>
    internal static void WakeAddressWaiters(ulong address, int wakeCount)
    {
        // Bump the generation first so a wait that has registered but not yet
        // parked sees the change and resumes instead of missing this wake.
        _wakeGenerations.AddOrUpdate(address, 1, static (_, current) => current + 1);

        GuestThreadExecution.Scheduler?.WakeBlockedThreads(WakeKey(address), wakeCount);

        if (_hostAddressGates.TryGetValue(address, out var gate))
        {
            lock (gate)
            {
                Monitor.PulseAll(gate);
            }
        }
    }

    [SysAbiExport(
        Nid = "Hc4CaR6JBL0",
        ExportName = "sceKernelSyncOnAddressWait",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libKernel")]
    public static int SyncOnAddressWait(CpuContext ctx)
    {
        var address = ctx[CpuRegister.Rdi];
        var expectedValue = ctx[CpuRegister.Rsi];
        var size = ctx[CpuRegister.Rdx];
        var timeoutAddress = ctx[CpuRegister.Rcx];

        if (address == 0 || size is not (1 or 2 or 4 or 8))
        {
            return SetReturn(ctx, OrbisGen2Result.ORBIS_GEN2_ERROR_INVALID_ARGUMENT);
        }

        // Compare-and-wait: only park while the guest value still equals the
        // expected value. A caller whose value already changed returns at once
        // instead of parking at all.
        if (!TryReadGuestWord(ctx, address, (int)size, out var currentValue))
        {
            return SetReturn(ctx, OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT);
        }

        if (currentValue != expectedValue)
        {
            return SetReturn(ctx, OrbisGen2Result.ORBIS_GEN2_OK);
        }

        // The timeout is a pointer to microseconds; a null pointer (or a zero
        // value) means an infinite wait.
        uint timeoutUsec = 0;
        var hasTimeout = timeoutAddress != 0;
        if (hasTimeout && !TryReadUInt32(ctx, timeoutAddress, out timeoutUsec))
        {
            return SetReturn(ctx, OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT);
        }

        hasTimeout = hasTimeout && timeoutUsec != 0;

        var observedGeneration = CurrentGeneration(address);
        var deadline = hasTimeout
            ? GuestThreadExecution.ComputeDeadlineTimestamp(TimeSpan.FromMicroseconds(timeoutUsec))
            : GuestThreadExecution.ComputeDeadlineTimestamp(WaitSelfHealTimeout);

        // Set when the wake predicate releases the thread; distinguishes a
        // real wake from a deadline expiry in the resume handler.
        var releasedByWake = false;

        bool WakePredicate()
        {
            // Release only when the guest value no longer matches (or has
            // become unreadable, which the resume handler reports as a fault).
            // A wake that arrives without a value change keeps the waiter
            // parked; the value re-check below is what queue users rely on.
            if (!TryReadGuestWord(ctx, address, (int)size, out var parkedValue))
            {
                releasedByWake = true;
                return true;
            }

            if (parkedValue == expectedValue)
            {
                return false;
            }

            releasedByWake = true;
            return true;
        }

        int ResumeWait()
        {
            // Re-check the value: the thread may have been released by a wake
            // (value changed) or by a deadline expiry (value unchanged).
            if (!TryReadGuestWord(ctx, address, (int)size, out var resumedValue))
            {
                return (int)OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT;
            }

            if (resumedValue != expectedValue)
            {
                return (int)OrbisGen2Result.ORBIS_GEN2_OK;
            }

            if (!hasTimeout || releasedByWake)
            {
                // No guest deadline: the self-heal tick expired, so release as
                // a spurious wakeup the guest re-evaluates. A real wake whose
                // value has since flipped back is released the same way.
                return (int)OrbisGen2Result.ORBIS_GEN2_OK;
            }

            _ = TryWriteUInt32(ctx, timeoutAddress, 0);
            return (int)OrbisGen2Result.ORBIS_GEN2_ERROR_TIMED_OUT;
        }

        // Cooperative path: stay parked until the guest value at the address
        // differs from the expected value (observed by the wake predicate or
        // by this resume re-check) or the deadline expires.
        if (GuestThreadExecution.RequestCurrentThreadBlock(
                ctx,
                "sceKernelSyncOnAddressWait",
                WakeKey(address),
                ResumeWait,
                WakePredicate,
                deadline))
        {
            return SetReturn(ctx, OrbisGen2Result.ORBIS_GEN2_OK);
        }

        // Non-cooperative caller (host main thread): poll the guest value in
        // bounded Monitor.Wait slices until it changes, the deadline expires,
        // or a matching wake pulses the gate.
        var gate = _hostAddressGates.GetOrAdd(address, static _ => new object());
        lock (gate)
        {
            while (true)
            {
                if (!TryReadGuestWord(ctx, address, (int)size, out var polledValue))
                {
                    return SetReturn(ctx, OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT);
                }

                if (polledValue != expectedValue)
                {
                    return SetReturn(ctx, OrbisGen2Result.ORBIS_GEN2_OK);
                }

                if (hasTimeout && Stopwatch.GetTimestamp() >= deadline)
                {
                    _ = TryWriteUInt32(ctx, timeoutAddress, 0);
                    return SetReturn(ctx, OrbisGen2Result.ORBIS_GEN2_ERROR_TIMED_OUT);
                }

                var currentGeneration = CurrentGeneration(address);
                if (currentGeneration != observedGeneration)
                {
                    // A wake landed while the value was being re-checked:
                    // consume it and re-read the value instead of sleeping
                    // past the release.
                    observedGeneration = currentGeneration;
                    continue;
                }

                var sleepMilliseconds = HostWaitPollMilliseconds;
                if (hasTimeout)
                {
                    var remainingMilliseconds = RemainingMilliseconds(deadline);
                    if (remainingMilliseconds < sleepMilliseconds)
                    {
                        sleepMilliseconds = remainingMilliseconds;
                    }
                }

                Monitor.Wait(gate, sleepMilliseconds);
            }
        }
    }

    [SysAbiExport(
        Nid = "q2y-wDIVWZA",
        ExportName = "sceKernelSyncOnAddressWake",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libKernel")]
    public static int SyncOnAddressWake(CpuContext ctx)
    {
        var address = ctx[CpuRegister.Rdi];
        if (address == 0)
        {
            return SetReturn(ctx, OrbisGen2Result.ORBIS_GEN2_ERROR_INVALID_ARGUMENT);
        }

        // rsi carries the number of waiters to release (1 = wake-one, a large
        // value = wake-all); default to all if it looks unset.
        var requested = unchecked((long)ctx[CpuRegister.Rsi]);
        var wakeCount = requested is > 0 and < int.MaxValue ? (int)requested : int.MaxValue;

        WakeAddressWaiters(address, wakeCount);

        return SetReturn(ctx, OrbisGen2Result.ORBIS_GEN2_OK);
    }

    private static int RemainingMilliseconds(long deadlineTimestamp)
    {
        var remainingTicks = deadlineTimestamp - Stopwatch.GetTimestamp();
        if (remainingTicks <= 0)
        {
            return 0;
        }

        var ticksPerMillisecond = Math.Max(1L, Stopwatch.Frequency / 1000);
        var remainingMilliseconds = remainingTicks / ticksPerMillisecond;
        return (int)Math.Min(int.MaxValue, remainingMilliseconds);
    }

    // Reads the 1/2/4/8-byte compare-and-wait word a guest passed to
    // sceKernelSyncOnAddressWait, zero-extending it to compare against the
    // 64-bit expected value register argument. Internal so the raw _umtx_op
    // exports share the exact same guest-word reads.
    internal static bool TryReadGuestWord(CpuContext ctx, ulong address, int size, out ulong value)
    {
        value = 0;
        Span<byte> buffer = stackalloc byte[sizeof(ulong)];
        if (!ctx.Memory.TryRead(address, buffer[..size]))
        {
            return false;
        }

        value = size switch
        {
            1 => buffer[0],
            2 => BinaryPrimitives.ReadUInt16LittleEndian(buffer),
            4 => BinaryPrimitives.ReadUInt32LittleEndian(buffer),
            _ => BinaryPrimitives.ReadUInt64LittleEndian(buffer),
        };
        return true;
    }

    private static bool TryReadUInt32(CpuContext ctx, ulong address, out uint value)
    {
        value = 0;
        Span<byte> buffer = stackalloc byte[sizeof(uint)];
        if (!ctx.Memory.TryRead(address, buffer))
        {
            return false;
        }

        value = BinaryPrimitives.ReadUInt32LittleEndian(buffer);
        return true;
    }

    private static bool TryWriteUInt32(CpuContext ctx, ulong address, uint value)
    {
        Span<byte> buffer = stackalloc byte[sizeof(uint)];
        BinaryPrimitives.WriteUInt32LittleEndian(buffer, value);
        return ctx.Memory.TryWrite(address, buffer);
    }

    private static int SetReturn(CpuContext ctx, OrbisGen2Result result)
    {
        var value = (int)result;
        ctx[CpuRegister.Rax] = unchecked((ulong)value);
        return value;
    }
}
