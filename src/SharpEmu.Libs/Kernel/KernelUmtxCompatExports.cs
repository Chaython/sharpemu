// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Collections.Concurrent;
using System.Diagnostics;
using System.Threading;
using SharpEmu.HLE;

namespace SharpEmu.Libs.Kernel;

// The PS5 kernel is FreeBSD-derived, and its C runtime exposes the raw
// _umtx_op(2) syscall multiplexer (NID 04AjkP0jO9U). Every libthr lock
// (mutexes, condvars, rwlocks, semaphores) and every lock-free runtime queue
// bottoms out in these compare-and-wait/wake primitives. Left unimplemented,
// the unresolved-import stub returned "not found" instantly, so callers that
// legitimately wait for a memory value to change re-checked the unchanged
// value and called again — a multi-million-call busy loop that starved every
// other guest thread (observed: two worker threads stuck at import #190k+,
// game window frozen, no video output, log flooded with identical warnings).
//
// This implements the op table over the same cooperative-block scheduler the
// sceKernelSyncOnAddress* exports use, sharing their wake-key space so an
// address-wait wake releases an umtx waiter and vice versa. Helpers use
// FreeBSD errno values internally; the exported libc entry returns -1 and
// writes guest TLS errno on failure, including after a cooperative resume.
// Waits park the guest thread for real: a parked thread consumes no
// CPU, so the threads that are supposed to change the value and issue the
// wake finally get scheduled.
//
// The value each wait parks on is re-checked in the wake predicate, on every
// resume, and in every poll slice of the non-cooperative host path, so a
// missed or early wake can never release a waiter whose value has not
// changed (beyond the spurious-wakeup release futex callers must already
// tolerate). Infinite waits keep the same bounded self-heal deadline as
// sceKernelSyncOnAddressWait.
//
// Lock ops (mutex/rwlock/sem) follow one shape: a fast acquire attempt under
// the per-address gate, then a park whose resume handler completes the
// acquisition. The cooperative path returns from the export as soon as the
// block is staged — the scheduler parks the thread and the resume handler
// runs the completion — while the non-cooperative host path parks inline and
// completes before returning, so both paths end the syscall with the lock
// word in the acquired state (best-effort: a racing guest fast path can
// still win the word, in which case the lenient completion reports success
// rather than deadlocking the guest).
public static class KernelUmtxCompatExports
{
    // FreeBSD 11 errno values (the PS5 kernel's base).
    private const int ErrnoOk = 0;
    private const int ErrnoFault = 14;
    private const int ErrnoBusy = 16;
    private const int ErrnoInvalid = 22;
    private const int ErrnoTimedout = 60;

    // FreeBSD 11 umtx op codes (sys/sys/umtx.h).
    private const int UmtxOpLock = 0; // legacy umtx lock
    private const int UmtxOpUnlock = 1;
    private const int UmtxOpWait = 2;
    private const int UmtxOpWake = 3;
    private const int UmtxOpMutexTrylock = 4;
    private const int UmtxOpMutexLock = 5;
    private const int UmtxOpMutexUnlock = 6;
    private const int UmtxOpSetCeiling = 7;
    private const int UmtxOpCvWait = 8;
    private const int UmtxOpCvSignal = 9;
    private const int UmtxOpCvBroadcast = 10;
    private const int UmtxOpWaitUint = 11;
    private const int UmtxOpRwRdlock = 12;
    private const int UmtxOpRwWrlock = 13;
    private const int UmtxOpRwUnlock = 14;
    private const int UmtxOpWaitUintPrivate = 15;
    private const int UmtxOpWakePrivate = 16;
    private const int UmtxOpMutexWait = 17;
    private const int UmtxOpMutexWake = 18; // deprecated
    private const int UmtxOpSemWait = 19; // deprecated
    private const int UmtxOpSemWake = 20; // deprecated
    private const int UmtxOpNwakePrivate = 21;
    private const int UmtxOpMutexWake2 = 22;
    private const int UmtxOpSem2Wait = 23;
    private const int UmtxOpSem2Wake = 24;
    private const int UmtxOpShm = 25;
    private const int UmtxOpRobustLists = 26;
    private const int UmtxOpNanosleep = 30; // FreeBSD 12+; accepted defensively

    // umutex.m_owner bits (sys/sys/umtx.h).
    private const uint UmutexUnowned = 0;
    private const uint UmutexContested = 0x80000000u;

    // urwlock.rw_state bits.
    private const uint UrwlockWriteOwner = 0x80000000u;
    private const uint UrwlockMaxReaders = 0x1fffffffu;

    // _usem2._count bits.
    private const uint UsemHasWaiters = 0x80000000u;
    private const uint UsemMaxCount = 0x7fffffffu;

    // _umtx_time._flags / CVWAIT flags.
    private const uint UmtxAbstime = 0x01u;

    // Host-side per-address gate serializing the read-decide-write sequences
    // of the lock-word state machines below. Guest fast paths may still race
    // (their real lock cmpxchg runs on the guest), but every syscall slow
    // path funneling through here is serialized per address, which is what
    // the kernel's atomic CAS on the same words guarantees.
    private static readonly ConcurrentDictionary<ulong, object> _umtxAddressGates = new();

    // Unknown op codes seen at runtime; each logs exactly once.
    private static readonly ConcurrentDictionary<int, long> _unknownOpCounts = new();

    [SysAbiExport(
        Nid = "04AjkP0jO9U",
        ExportName = "_umtx_op",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libKernel")]
    public static int UmtxOp(CpuContext ctx)
    {
        var obj = ctx[CpuRegister.Rdi];
        var op = unchecked((int)ctx[CpuRegister.Rsi]);
        var val = ctx[CpuRegister.Rdx];
        var uaddr = ctx[CpuRegister.Rcx];
        var uaddr2 = ctx[CpuRegister.R8];

        var error = op switch
        {
            UmtxOpWait => DoWait(ctx, obj, val, uaddr2, uaddr, size: 8),
            UmtxOpWaitUint or UmtxOpWaitUintPrivate => DoWait(ctx, obj, val, uaddr2, uaddr, size: 4),
            UmtxOpWake or UmtxOpWakePrivate => DoWake(ctx, obj, val),
            UmtxOpNwakePrivate => DoNwake(ctx, obj, val),

            UmtxOpMutexTrylock => DoMutexTrylock(ctx, obj),
            UmtxOpMutexLock => DoMutexLock(ctx, obj, uaddr),
            UmtxOpMutexUnlock => DoMutexUnlock(ctx, obj),
            UmtxOpMutexWait => DoMutexWait(ctx, obj, uaddr),
            UmtxOpMutexWake or UmtxOpMutexWake2 => DoWake(ctx, obj, int.MaxValue),

            UmtxOpCvWait => DoCvWait(ctx, obj, val, uaddr, uaddr2),
            UmtxOpCvSignal => DoCvSignal(ctx, obj, wakeAll: false),
            UmtxOpCvBroadcast => DoCvSignal(ctx, obj, wakeAll: true),

            UmtxOpRwRdlock => DoRwRdlock(ctx, obj, uaddr),
            UmtxOpRwWrlock => DoRwWrlock(ctx, obj, uaddr),
            UmtxOpRwUnlock => DoRwUnlock(ctx, obj),

            UmtxOpSem2Wait => DoSem2Wait(ctx, obj, uaddr),
            UmtxOpSem2Wake => DoSem2Wake(ctx, obj),

            UmtxOpSetCeiling => DoSetCeiling(ctx, obj, val, uaddr2),
            UmtxOpRobustLists => ErrnoOk, // acknowledge registration
            UmtxOpShm => ErrnoOk, // no shared-memory mapping bookkeeping needed

            UmtxOpLock => DoLegacyLock(ctx, obj),
            UmtxOpUnlock => DoLegacyUnlock(ctx, obj),
            UmtxOpNanosleep => DoNanosleep(ctx, obj),

            // Deprecated sem ops (19/20) share the usem layout of sem2 minus
            // the flags word; treat them like sem2 — the semantics of "wait
            // until count > 0 then decrement" / "increment and wake" hold.
            UmtxOpSemWait => DoSem2Wait(ctx, obj, uaddr),
            UmtxOpSemWake => DoSem2Wake(ctx, obj),

            _ => LogUnknownOp(op),
        };
        return PosixResult(ctx, error);
    }

    private static int PosixResult(CpuContext ctx, int error)
    {
        if (error == ErrnoOk)
        {
            ctx[CpuRegister.Rax] = 0;
            return 0;
        }

        KernelRuntimeCompatExports.TrySetErrno(ctx, error);
        ctx[CpuRegister.Rax] = ulong.MaxValue;
        return -1;
    }

    // ---- UMTX_OP_WAIT / UMTX_OP_WAIT_UINT ----
    //
    // If the word at obj no longer equals val, return success immediately
    // so the caller re-reads it; otherwise park until
    // the value differs, a matching wake arrives, or the deadline expires.
    private static int DoWait(CpuContext ctx, ulong address, ulong expected, ulong timeoutAddress, ulong timeoutSize, int size)
    {
        if (address == 0)
        {
            return ErrnoFault;
        }

        if (!KernelSyncOnAddressCompatExports.TryReadGuestWord(ctx, address, size, out var current))
        {
            return ErrnoFault;
        }

        if (current != expected)
        {
            // The value already moved between the caller's load and this
            // call; the caller must re-read rather than sleep.
            return ErrnoOk;
        }

        var timeoutError = ReadWaitTimeout(ctx, timeoutAddress, timeoutSize, out var timeout, out var absolute);
        if (timeoutError != ErrnoOk)
        {
            return timeoutError;
        }

        if (absolute)
        {
            var remaining = AbsoluteRemaining(timeout);
            if (remaining <= TimeSpan.Zero)
            {
                return ErrnoTimedout;
            }

            timeout = remaining;
        }

        var hasTimeout = timeoutAddress != 0;
        var deadline = hasTimeout
            ? GuestThreadExecution.ComputeDeadlineTimestamp(timeout)
            : GuestThreadExecution.ComputeDeadlineTimestamp(KernelSyncOnAddressCompatExports.WaitSelfHealTimeout);

        var releasedByWake = false;

        bool WakePredicate()
        {
            if (!KernelSyncOnAddressCompatExports.TryReadGuestWord(ctx, address, size, out var parkedValue))
            {
                releasedByWake = true;
                return true;
            }

            if (parkedValue == expected)
            {
                return false;
            }

            releasedByWake = true;
            return true;
        }

        int ResumeWait()
        {
            if (!KernelSyncOnAddressCompatExports.TryReadGuestWord(ctx, address, size, out var resumedValue))
            {
                return ErrnoFault;
            }

            if (resumedValue != expected)
            {
                return ErrnoOk;
            }

            if (!hasTimeout || releasedByWake)
            {
                // Self-heal tick or a wake that raced a value flip-back:
                // release as the spurious wakeup futex callers tolerate.
                return ErrnoOk;
            }

            return ErrnoTimedout;
        }

        if (GuestThreadExecution.RequestCurrentThreadBlock(
                ctx,
                "_umtx_op_wait",
                KernelSyncOnAddressCompatExports.WakeKey(address),
                () => PosixResult(ctx, ResumeWait()),
                WakePredicate,
                deadline))
        {
            return ErrnoOk;
        }

        // Non-cooperative caller (host thread): bounded Monitor.Wait slices.
        return ParkHostThread(ctx, address, expected, size, hasTimeout, deadline);
    }

    // ---- UMTX_OP_WAKE / WAKE_PRIVATE / MUTEX_WAKE / MUTEX_WAKE2 ----

    private static int DoWake(CpuContext ctx, ulong address, ulong requested)
    {
        if (address == 0)
        {
            return ErrnoFault;
        }

        var wakeCount = requested is > 0 and < int.MaxValue
            ? unchecked((int)requested)
            : int.MaxValue;
        KernelSyncOnAddressCompatExports.WakeAddressWaiters(address, wakeCount);
        return ErrnoOk;
    }

    // UMTX_OP_NWAKE_PRIVATE: obj is an array of addresses, val the count.
    private static int DoNwake(CpuContext ctx, ulong addressArray, ulong count)
    {
        if (addressArray == 0 || count == 0)
        {
            return ErrnoOk;
        }

        for (var index = 0UL; index < count; index++)
        {
            if (!ctx.TryReadUInt64(addressArray + index * sizeof(ulong), out var target))
            {
                return ErrnoFault;
            }

            KernelSyncOnAddressCompatExports.WakeAddressWaiters(target, int.MaxValue);
        }

        return ErrnoOk;
    }

    // ---- umutex ops ----
    //
    // struct umutex { u32 m_owner; u32 m_flags; u32 m_ceilings[2]; } — the
    // owner word is the lock: 0 unowned, tid when held, high bit set while
    // waiters are queued. Guest libthr fast paths acquire it with a real
    // cmpxchg before falling back to these syscalls, so the slow path only
    // needs to park until the owner word is free and claim it.

    private static uint CurrentTid()
    {
        // Stable per guest thread; kept clear of the low special-marker range
        // and the contested bit, matching FreeBSD's tid placement rules.
        var uniqueId = KernelPthreadState.GetCurrentThreadUniqueId();
        var tid = (uint)(uniqueId & 0x7fffffffu);
        return tid < 0x10000u ? tid | 0x10000u : tid;
    }

    private static int DoMutexTrylock(CpuContext ctx, ulong address)
    {
        if (address == 0)
        {
            return ErrnoFault;
        }

        var gate = _umtxAddressGates.GetOrAdd(address, static _ => new object());
        lock (gate)
        {
            if (!ctx.TryReadUInt32(address, out var owner))
            {
                return ErrnoFault;
            }

            if (owner != UmutexUnowned)
            {
                return ErrnoBusy;
            }

            if (!ctx.TryWriteUInt32(address, CurrentTid()))
            {
                return ErrnoFault;
            }

            return ErrnoOk;
        }
    }

    private static int DoMutexLock(CpuContext ctx, ulong address, ulong timeoutAddress)
    {
        if (address == 0)
        {
            return ErrnoFault;
        }

        var tid = CurrentTid();
        if (!TryReadUmtxTimeout(ctx, timeoutAddress, out var timeout, out var absolute))
        {
            return ErrnoFault;
        }

        if (absolute)
        {
            var remaining = AbsoluteRemaining(timeout);
            if (remaining <= TimeSpan.Zero)
            {
                return ErrnoTimedout;
            }

            timeout = remaining;
        }

        var hasTimeout = timeout > TimeSpan.Zero;
        var deadline = hasTimeout
            ? GuestThreadExecution.ComputeDeadlineTimestamp(timeout)
            : GuestThreadExecution.ComputeDeadlineTimestamp(KernelSyncOnAddressCompatExports.WaitSelfHealTimeout);

        var gate = _umtxAddressGates.GetOrAdd(address, static _ => new object());
        lock (gate)
        {
            if (!ctx.TryReadUInt32(address, out var owner))
            {
                return ErrnoFault;
            }

            if (owner == UmutexUnowned)
            {
                if (!ctx.TryWriteUInt32(address, tid))
                {
                    return ErrnoFault;
                }

                return ErrnoOk;
            }

            if ((owner & ~UmutexContested) == tid)
            {
                // Re-lock by the same guest thread (recursive mutex reaching
                // the syscall path): claim success rather than deadlocking.
                return ErrnoOk;
            }

            // Mark contested so a racing unlock wakes this waiter.
            _ = ctx.TryWriteUInt32(address, owner | UmutexContested);
        }

        return ParkAndComplete(
            ctx,
            address,
            hasTimeout,
            deadline,
            reason: "_umtx_op_mutex_lock",
            releaseWhen: word => word == UmutexUnowned,
            complete: () => AcquireMutexLenient(ctx, address, tid));
    }

    private static int DoMutexUnlock(CpuContext ctx, ulong address)
    {
        if (address == 0)
        {
            return ErrnoFault;
        }

        var gate = _umtxAddressGates.GetOrAdd(address, static _ => new object());
        lock (gate)
        {
            if (!ctx.TryReadUInt32(address, out _))
            {
                return ErrnoFault;
            }

            // Lenient on ownership (the guest's fast-path cmpxchg may have
            // claimed it under a different tid scheme): release unconditionally.
            if (!ctx.TryWriteUInt32(address, UmutexUnowned))
            {
                return ErrnoFault;
            }
        }

        KernelSyncOnAddressCompatExports.WakeAddressWaiters(address, int.MaxValue);
        return ErrnoOk;
    }

    // UMTX_OP_MUTEX_WAIT: park while the mutex is owned by another thread;
    // the caller re-runs its own acquire afterwards.
    private static int DoMutexWait(CpuContext ctx, ulong address, ulong timeoutAddress)
    {
        if (address == 0)
        {
            return ErrnoFault;
        }

        var tid = CurrentTid();
        if (!ctx.TryReadUInt32(address, out var owner))
        {
            return ErrnoFault;
        }

        if (owner == UmutexUnowned || (owner & ~UmutexContested) == tid)
        {
            return ErrnoOk;
        }

        if (!TryReadUmtxTimeout(ctx, timeoutAddress, out var timeout, out var absolute))
        {
            return ErrnoFault;
        }

        if (absolute)
        {
            var remaining = AbsoluteRemaining(timeout);
            if (remaining <= TimeSpan.Zero)
            {
                return ErrnoTimedout;
            }

            timeout = remaining;
        }

        var hasTimeout = timeout > TimeSpan.Zero;
        var deadline = hasTimeout
            ? GuestThreadExecution.ComputeDeadlineTimestamp(timeout)
            : GuestThreadExecution.ComputeDeadlineTimestamp(KernelSyncOnAddressCompatExports.WaitSelfHealTimeout);

        return ParkAndComplete(
            ctx,
            address,
            hasTimeout,
            deadline,
            reason: "_umtx_op_mutex_wait",
            releaseWhen: word => word == UmutexUnowned || (word & ~UmutexContested) == tid,
            complete: static () => ErrnoOk);
    }

    // ---- ucond ops ----
    //
    // libthr drives condvars by bumping ucond.c_seq in user space, then
    // calling CV_WAIT on the seq word (val = the seq it observed) and
    // CV_SIGNAL/BROADCAST on the same word when signaling. CV_WAIT's uaddr2
    // is the paired umutex: the kernel releases it before parking and
    // reacquires it on wake — pthread_cond_wait's atomic unlock/wait/relock.
    private static int DoCvWait(CpuContext ctx, ulong address, ulong expectedSeq, ulong timeoutAddress, ulong mutexAddress)
    {
        if (address == 0)
        {
            return ErrnoFault;
        }

        if (!TryReadUmtxTimeout(ctx, timeoutAddress, out var timeout, out var absolute))
        {
            return ErrnoFault;
        }

        if (absolute)
        {
            var remaining = AbsoluteRemaining(timeout);
            if (remaining <= TimeSpan.Zero)
            {
                return ErrnoTimedout;
            }

            timeout = remaining;
        }

        var hasTimeout = timeout > TimeSpan.Zero;
        var deadline = hasTimeout
            ? GuestThreadExecution.ComputeDeadlineTimestamp(timeout)
            : GuestThreadExecution.ComputeDeadlineTimestamp(KernelSyncOnAddressCompatExports.WaitSelfHealTimeout);

        // Release the paired mutex before parking (its owner word lives at a
        // different address; the unlock itself wakes that address's waiters).
        if (mutexAddress != 0)
        {
            _ = DoMutexUnlock(ctx, mutexAddress);
        }

        var releasedByWake = false;

        bool WakePredicate()
        {
            if (!KernelSyncOnAddressCompatExports.TryReadGuestWord(ctx, address, 4, out var parkedSeq))
            {
                releasedByWake = true;
                return true;
            }

            if (parkedSeq == expectedSeq)
            {
                return false;
            }

            releasedByWake = true;
            return true;
        }

        int ResumeWait()
        {
            var result = ErrnoOk;
            if (KernelSyncOnAddressCompatExports.TryReadGuestWord(ctx, address, 4, out var resumedSeq) &&
                resumedSeq == expectedSeq &&
                hasTimeout &&
                !releasedByWake)
            {
                result = ErrnoTimedout;
            }

            // Reacquire the paired mutex before returning to the guest. This
            // runs on the scheduler's resume path, so it must not request
            // another cooperative block; the host-side acquire loop is
            // bounded by the self-heal deadline.
            if (mutexAddress != 0)
            {
                _ = AcquireMutexFromHost(ctx, mutexAddress);
            }

            return result;
        }

        if (GuestThreadExecution.RequestCurrentThreadBlock(
                ctx,
                "_umtx_op_cv_wait",
                KernelSyncOnAddressCompatExports.WakeKey(address),
                () => PosixResult(ctx, ResumeWait()),
                WakePredicate,
                deadline))
        {
            return ErrnoOk;
        }

        // Non-cooperative host thread: poll slices, then reacquire inline.
        var waitResult = ParkHostThread(ctx, address, expectedSeq, 4, hasTimeout, deadline);
        if (mutexAddress != 0)
        {
            _ = AcquireMutexFromHost(ctx, mutexAddress);
        }

        return waitResult;
    }

    private static int DoCvSignal(CpuContext ctx, ulong address, bool wakeAll)
    {
        if (address == 0)
        {
            return ErrnoFault;
        }

        KernelSyncOnAddressCompatExports.WakeAddressWaiters(address, wakeAll ? int.MaxValue : 1);
        return ErrnoOk;
    }

    // ---- urwlock ops ----
    //
    // struct urwlock { i32 rw_state; i32 rw_flags; u32 blocked_readers; u32
    // blocked_writers; } — rw_state packs the write-owner bit, waiter bits,
    // and the reader count.

    private static int DoRwRdlock(CpuContext ctx, ulong address, ulong timeoutAddress)
    {
        if (address == 0)
        {
            return ErrnoFault;
        }

        if (!TryReadUmtxTimeout(ctx, timeoutAddress, out var timeout, out var absolute))
        {
            return ErrnoFault;
        }

        if (absolute)
        {
            var remaining = AbsoluteRemaining(timeout);
            if (remaining <= TimeSpan.Zero)
            {
                return ErrnoTimedout;
            }

            timeout = remaining;
        }

        var hasTimeout = timeout > TimeSpan.Zero;
        var deadline = hasTimeout
            ? GuestThreadExecution.ComputeDeadlineTimestamp(timeout)
            : GuestThreadExecution.ComputeDeadlineTimestamp(KernelSyncOnAddressCompatExports.WaitSelfHealTimeout);

        uint parkedAgainst;
        var gate = _umtxAddressGates.GetOrAdd(address, static _ => new object());
        lock (gate)
        {
            if (!ctx.TryReadUInt32(address, out var state))
            {
                return ErrnoFault;
            }

            if ((state & UrwlockWriteOwner) == 0 && (state & UrwlockMaxReaders) < UrwlockMaxReaders)
            {
                if (!ctx.TryWriteUInt32(address, state + 1))
                {
                    return ErrnoFault;
                }

                return ErrnoOk;
            }

            parkedAgainst = state;
        }

        return ParkAndComplete(
            ctx,
            address,
            hasTimeout,
            deadline,
            reason: "_umtx_op_rw_rdlock",
            releaseWhen: word => word != parkedAgainst,
            complete: () => AcquireReadLockLenient(ctx, address));
    }

    private static int DoRwWrlock(CpuContext ctx, ulong address, ulong timeoutAddress)
    {
        if (address == 0)
        {
            return ErrnoFault;
        }

        if (!TryReadUmtxTimeout(ctx, timeoutAddress, out var timeout, out var absolute))
        {
            return ErrnoFault;
        }

        if (absolute)
        {
            var remaining = AbsoluteRemaining(timeout);
            if (remaining <= TimeSpan.Zero)
            {
                return ErrnoTimedout;
            }

            timeout = remaining;
        }

        var hasTimeout = timeout > TimeSpan.Zero;
        var deadline = hasTimeout
            ? GuestThreadExecution.ComputeDeadlineTimestamp(timeout)
            : GuestThreadExecution.ComputeDeadlineTimestamp(KernelSyncOnAddressCompatExports.WaitSelfHealTimeout);

        var gate = _umtxAddressGates.GetOrAdd(address, static _ => new object());
        lock (gate)
        {
            if (!ctx.TryReadUInt32(address, out var state))
            {
                return ErrnoFault;
            }

            if (state == 0)
            {
                if (!ctx.TryWriteUInt32(address, UrwlockWriteOwner))
                {
                    return ErrnoFault;
                }

                return ErrnoOk;
            }
        }

        return ParkAndComplete(
            ctx,
            address,
            hasTimeout,
            deadline,
            reason: "_umtx_op_rw_wrlock",
            releaseWhen: word => word == 0,
            complete: () => AcquireWriteLockLenient(ctx, address));
    }

    private static int DoRwUnlock(CpuContext ctx, ulong address)
    {
        if (address == 0)
        {
            return ErrnoFault;
        }

        var gate = _umtxAddressGates.GetOrAdd(address, static _ => new object());
        lock (gate)
        {
            if (!ctx.TryReadUInt32(address, out var state))
            {
                return ErrnoFault;
            }

            uint nextState;
            if ((state & UrwlockWriteOwner) != 0)
            {
                nextState = 0;
            }
            else
            {
                var readers = state & UrwlockMaxReaders;
                nextState = readers > 0 ? state - 1 : state;
            }

            if (!ctx.TryWriteUInt32(address, nextState))
            {
                return ErrnoFault;
            }
        }

        KernelSyncOnAddressCompatExports.WakeAddressWaiters(address, int.MaxValue);
        return ErrnoOk;
    }

    // ---- _usem2 ops ----
    //
    // struct _usem2 { u32 _count; u32 _flags; } — bit 31 of _count marks
    // waiters, the low 31 bits are the semaphore count.

    private static int DoSem2Wait(CpuContext ctx, ulong address, ulong timeoutAddress)
    {
        if (address == 0)
        {
            return ErrnoFault;
        }

        if (!TryReadUmtxTimeout(ctx, timeoutAddress, out var timeout, out var absolute))
        {
            return ErrnoFault;
        }

        if (absolute)
        {
            var remaining = AbsoluteRemaining(timeout);
            if (remaining <= TimeSpan.Zero)
            {
                return ErrnoTimedout;
            }

            timeout = remaining;
        }

        var hasTimeout = timeout > TimeSpan.Zero;
        var deadline = hasTimeout
            ? GuestThreadExecution.ComputeDeadlineTimestamp(timeout)
            : GuestThreadExecution.ComputeDeadlineTimestamp(KernelSyncOnAddressCompatExports.WaitSelfHealTimeout);

        uint parkedAgainst;
        var gate = _umtxAddressGates.GetOrAdd(address, static _ => new object());
        lock (gate)
        {
            if (!ctx.TryReadUInt32(address, out var count))
            {
                return ErrnoFault;
            }

            if ((count & UsemMaxCount) > 0)
            {
                if (!ctx.TryWriteUInt32(address, count - 1))
                {
                    return ErrnoFault;
                }

                return ErrnoOk;
            }

            if ((count & UsemHasWaiters) == 0)
            {
                _ = ctx.TryWriteUInt32(address, count | UsemHasWaiters);
                count |= UsemHasWaiters;
            }

            parkedAgainst = count;
        }

        return ParkAndComplete(
            ctx,
            address,
            hasTimeout,
            deadline,
            reason: "_umtx_op_sem2_wait",
            releaseWhen: word => word != parkedAgainst,
            complete: () => AcquireSemaphoreLenient(ctx, address));
    }

    private static int DoSem2Wake(CpuContext ctx, ulong address)
    {
        if (address == 0)
        {
            return ErrnoFault;
        }

        var gate = _umtxAddressGates.GetOrAdd(address, static _ => new object());
        lock (gate)
        {
            if (!ctx.TryReadUInt32(address, out var count))
            {
                return ErrnoFault;
            }

            var nextCount = (count & UsemHasWaiters) | Math.Min((count & UsemMaxCount) + 1, UsemMaxCount);
            if (!ctx.TryWriteUInt32(address, nextCount))
            {
                return ErrnoFault;
            }
        }

        KernelSyncOnAddressCompatExports.WakeAddressWaiters(address, int.MaxValue);
        return ErrnoOk;
    }

    // ---- UMTX_OP_SET_CEILING ----
    //
    // Stores the ceiling at m_ceilings[0] (obj+8) and reports the previous
    // value through uaddr2 when provided.

    private static int DoSetCeiling(CpuContext ctx, ulong address, ulong ceiling, ulong previousAddress)
    {
        if (address == 0)
        {
            return ErrnoFault;
        }

        var gate = _umtxAddressGates.GetOrAdd(address, static _ => new object());
        lock (gate)
        {
            if (!ctx.TryReadUInt32(address + 8, out var previous))
            {
                return ErrnoFault;
            }

            if (!ctx.TryWriteUInt32(address + 8, unchecked((uint)ceiling)))
            {
                return ErrnoFault;
            }

            if (previousAddress != 0 && !ctx.TryWriteUInt32(previousAddress, previous))
            {
                return ErrnoFault;
            }
        }

        return ErrnoOk;
    }

    // ---- legacy umtx lock (op 0/1): a plain sleep lock word ----

    private static int DoLegacyLock(CpuContext ctx, ulong address)
    {
        if (address == 0)
        {
            return ErrnoFault;
        }

        var gate = _umtxAddressGates.GetOrAdd(address, static _ => new object());
        lock (gate)
        {
            if (!ctx.TryReadUInt32(address, out var word))
            {
                return ErrnoFault;
            }

            if (word == 0)
            {
                if (!ctx.TryWriteUInt32(address, 1))
                {
                    return ErrnoFault;
                }

                return ErrnoOk;
            }
        }

        var deadline = GuestThreadExecution.ComputeDeadlineTimestamp(
            KernelSyncOnAddressCompatExports.WaitSelfHealTimeout);
        return ParkAndComplete(
            ctx,
            address,
            hasTimeout: false,
            deadline,
            reason: "_umtx_op_lock",
            releaseWhen: word => word == 0,
            complete: () =>
            {
                // Best effort: the release path saw the word clear.
                _ = ctx.TryWriteUInt32(address, 1);
                return ErrnoOk;
            });
    }

    private static int DoLegacyUnlock(CpuContext ctx, ulong address)
    {
        if (address == 0)
        {
            return ErrnoFault;
        }

        if (!ctx.TryWriteUInt32(address, 0))
        {
            return ErrnoFault;
        }

        KernelSyncOnAddressCompatExports.WakeAddressWaiters(address, int.MaxValue);
        return ErrnoOk;
    }

    private static int DoNanosleep(CpuContext ctx, ulong address)
    {
        if (address == 0)
        {
            return ErrnoFault;
        }

        if (!ctx.TryReadUInt64(address, out var seconds) ||
            !ctx.TryReadUInt64(address + 8, out var nanoseconds))
        {
            return ErrnoFault;
        }

        var duration = TimeSpan.FromSeconds(seconds) + TimeSpan.FromTicks(
            unchecked((long)nanoseconds) * TimeSpan.TicksPerSecond / 1_000_000_000);
        if (duration > TimeSpan.Zero)
        {
            Thread.Sleep(duration);
        }

        return ErrnoOk;
    }

    // ---- shared park + completion helper ----
    //
    // The single shape every lock op's slow path uses: park until
    // releaseWhen(word) holds for the 4-byte lock word at address, then run
    // the completion that claims the lock. On the cooperative path the
    // completion runs inside the scheduler's resume handler (the export has
    // already returned); on the host path it runs inline before returning.

    private static int ParkAndComplete(
        CpuContext ctx,
        ulong address,
        bool hasTimeout,
        long deadline,
        string reason,
        Func<uint, bool> releaseWhen,
        Func<int> complete)
    {
        var releasedByWake = false;

        bool WakePredicate()
        {
            if (!KernelSyncOnAddressCompatExports.TryReadGuestWord(ctx, address, 4, out var parkedWord))
            {
                releasedByWake = true;
                return true;
            }

            if (!releaseWhen(unchecked((uint)parkedWord)))
            {
                return false;
            }

            releasedByWake = true;
            return true;
        }

        int ResumeWait()
        {
            if (!KernelSyncOnAddressCompatExports.TryReadGuestWord(ctx, address, 4, out var resumedWord))
            {
                return ErrnoFault;
            }

            if (releaseWhen(unchecked((uint)resumedWord)))
            {
                return complete();
            }

            // Self-heal tick with the lock word still held: a spurious
            // release would let the guest run unlocked, but a timed-out
            // report lets a lock with a deadline fail forward. Infinite
            // waits keep re-parking via the caller's next syscall.
            return !hasTimeout || releasedByWake ? complete() : ErrnoTimedout;
        }

        if (GuestThreadExecution.RequestCurrentThreadBlock(
                ctx,
                reason,
                KernelSyncOnAddressCompatExports.WakeKey(address),
                () => PosixResult(ctx, ResumeWait()),
                WakePredicate,
                deadline))
        {
            return ErrnoOk;
        }

        // Non-cooperative caller: bounded Monitor slices, then complete.
        var waitResult = ParkHostWordLoop(
            ctx, address, size: 4, hasTimeout, deadline,
            word => releaseWhen(unchecked((uint)word)));
        if (waitResult != ErrnoOk)
        {
            return waitResult;
        }

        return complete();
    }

    /// <summary>
    /// Host-thread park for the WAIT/WAIT_UINT ops: re-reads the guest value
    /// each Monitor slice until it differs, the deadline expires, or a
    /// matching wake pulses the shared per-address gate.
    /// </summary>
    private static int ParkHostThread(
        CpuContext ctx, ulong address, ulong expected, int size, bool hasTimeout, long deadline)
    {
        return ParkHostWordLoop(ctx, address, size, hasTimeout, deadline, word => word != expected);
    }

    private static int ParkHostWordLoop(
        CpuContext ctx,
        ulong address,
        int size,
        bool hasTimeout,
        long deadline,
        Func<ulong, bool> releaseWhen)
    {
        var gate = KernelSyncOnAddressCompatExports.GetOrAddHostAddressGate(address);
        var observedGeneration = KernelSyncOnAddressCompatExports.CurrentGeneration(address);
        while (true)
        {
            // A host-side wait must still acknowledge guest exceptions (for
            // example, a GC suspension). The handler may wait for another
            // thread to wake this address, so deliver it outside the gate.
            GuestThreadExecution.Scheduler?.PollPendingExceptions(ctx);
            lock (gate)
            {
                if (!KernelSyncOnAddressCompatExports.TryReadGuestWord(ctx, address, size, out var polledValue))
                {
                    return ErrnoFault;
                }

                if (releaseWhen(polledValue))
                {
                    return ErrnoOk;
                }

                if (hasTimeout && Stopwatch.GetTimestamp() >= deadline)
                {
                    return ErrnoTimedout;
                }

                var currentGeneration = KernelSyncOnAddressCompatExports.CurrentGeneration(address);
                if (currentGeneration != observedGeneration)
                {
                    // A wake landed while the value was re-checked: consume it
                    // and re-read instead of sleeping past the release.
                    observedGeneration = currentGeneration;
                    continue;
                }

                var sleepMilliseconds = KernelSyncOnAddressCompatExports.HostWaitPollMilliseconds;
                if (hasTimeout)
                {
                    var remaining = deadline - Stopwatch.GetTimestamp();
                    var ticksPerMillisecond = Math.Max(1L, Stopwatch.Frequency / 1000);
                    var remainingMilliseconds = (int)Math.Min(int.MaxValue, remaining / ticksPerMillisecond);
                    if (remainingMilliseconds < sleepMilliseconds)
                    {
                        sleepMilliseconds = Math.Max(1, remainingMilliseconds);
                    }
                }

                Monitor.Wait(gate, sleepMilliseconds);
            }
        }
    }

    // ---- lenient completions ----
    //
    // Each claims its lock word under the per-address gate when the word is
    // actually free; when a racing guest fast path won the word in the
    // release-to-completion window, they report success anyway — the guest
    // believes it owns the lock and proceeds, which beats a livelock or a
    // spuriously failed acquire. The per-address gate keeps every syscall
    // slow path serialized, so the race window is guest-fast-path-only.

    private static int AcquireMutexLenient(CpuContext ctx, ulong address, uint tid)
    {
        var gate = _umtxAddressGates.GetOrAdd(address, static _ => new object());
        lock (gate)
        {
            if (ctx.TryReadUInt32(address, out var owner) && owner == UmutexUnowned)
            {
                _ = ctx.TryWriteUInt32(address, tid);
            }
        }

        return ErrnoOk;
    }

    private static int AcquireReadLockLenient(CpuContext ctx, ulong address)
    {
        var gate = _umtxAddressGates.GetOrAdd(address, static _ => new object());
        lock (gate)
        {
            if (ctx.TryReadUInt32(address, out var state) &&
                (state & UrwlockWriteOwner) == 0 &&
                (state & UrwlockMaxReaders) < UrwlockMaxReaders)
            {
                _ = ctx.TryWriteUInt32(address, state + 1);
            }
        }

        return ErrnoOk;
    }

    private static int AcquireWriteLockLenient(CpuContext ctx, ulong address)
    {
        var gate = _umtxAddressGates.GetOrAdd(address, static _ => new object());
        lock (gate)
        {
            if (ctx.TryReadUInt32(address, out var state) && state == 0)
            {
                _ = ctx.TryWriteUInt32(address, UrwlockWriteOwner);
            }
        }

        return ErrnoOk;
    }

    private static int AcquireSemaphoreLenient(CpuContext ctx, ulong address)
    {
        var gate = _umtxAddressGates.GetOrAdd(address, static _ => new object());
        lock (gate)
        {
            if (ctx.TryReadUInt32(address, out var count) && (count & UsemMaxCount) > 0)
            {
                _ = ctx.TryWriteUInt32(address, count - 1);
            }
        }

        return ErrnoOk;
    }

    /// <summary>
    /// Acquires an umutex from a context that cannot request another
    /// cooperative block (the CV resume path): CAS-style retry under the
    /// per-address gate plus bounded host parking.
    /// </summary>
    private static int AcquireMutexFromHost(CpuContext ctx, ulong address)
    {
        var tid = CurrentTid();
        var deadline = GuestThreadExecution.ComputeDeadlineTimestamp(
            KernelSyncOnAddressCompatExports.WaitSelfHealTimeout);

        while (true)
        {
            var gate = _umtxAddressGates.GetOrAdd(address, static _ => new object());
            lock (gate)
            {
                if (!ctx.TryReadUInt32(address, out var owner))
                {
                    return ErrnoFault;
                }

                if (owner == UmutexUnowned)
                {
                    if (!ctx.TryWriteUInt32(address, tid))
                    {
                        return ErrnoFault;
                    }

                    return ErrnoOk;
                }

                if ((owner & ~UmutexContested) == tid)
                {
                    return ErrnoOk;
                }
            }

            var waitResult = ParkHostWordLoop(
                ctx, address, size: 4, hasTimeout: false, deadline,
                word => word == UmutexUnowned);
            if (waitResult != ErrnoOk)
            {
                return waitResult;
            }
        }
    }

    // ---- timeout parsing ----

    // WAIT's fourth argument is a structure size, and its fifth is the
    // timeout pointer. Size <= 16 selects a plain timespec; reading flags
    // beyond it would interpret unrelated guest stack data as an absolute
    // deadline. Only a null pointer means an indefinite wait.
    private static int ReadWaitTimeout(
        CpuContext ctx, ulong address, ulong size, out TimeSpan timeout, out bool absolute)
    {
        timeout = Timeout.InfiniteTimeSpan;
        absolute = false;
        if (address == 0) return ErrnoOk;
        if (!ctx.TryReadUInt64(address, out var seconds) ||
            !ctx.TryReadUInt64(address + 8, out var nanoseconds)) return ErrnoFault;
        if (unchecked((long)seconds) < 0 || nanoseconds >= 1_000_000_000) return ErrnoInvalid;
        if (size > 16)
        {
            if (!ctx.TryReadUInt32(address + 16, out var flags) ||
                !ctx.TryReadUInt32(address + 20, out _)) return ErrnoFault;
            absolute = (flags & UmtxAbstime) != 0;
        }
        var fractionalTicks = (nanoseconds + 99) / 100;
        var maxSeconds = ((ulong)TimeSpan.MaxValue.Ticks - fractionalTicks) / TimeSpan.TicksPerSecond;
        timeout = seconds > maxSeconds
            ? TimeSpan.MaxValue
            : TimeSpan.FromTicks((long)(seconds * TimeSpan.TicksPerSecond + fractionalTicks));
        return ErrnoOk;
    }
    //
    // The timeout pointer may address a struct _umtx_time { timespec; u32
    // flags; u32 clockid; } or a plain struct timespec; the flags word is
    // optional (a fault reading it just means no flags). A null pointer, or
    // an all-zero timespec, means wait indefinitely.

    private static bool TryReadUmtxTimeout(CpuContext ctx, ulong timeoutAddress, out TimeSpan timeout, out bool absolute)
    {
        timeout = Timeout.InfiniteTimeSpan;
        absolute = false;
        if (timeoutAddress == 0)
        {
            return true;
        }

        if (!ctx.TryReadUInt64(timeoutAddress, out var seconds) ||
            !ctx.TryReadUInt64(timeoutAddress + 8, out var nanoseconds))
        {
            return false;
        }

        if (nanoseconds > 999_999_999)
        {
            // Malformed timespec; the kernel rejects with EINVAL, surfaced
            // by callers as a fault-class error.
            timeout = Timeout.InfiniteTimeSpan;
            return false;
        }

        if (seconds == 0 && nanoseconds == 0)
        {
            return true; // zero timeout == infinite, matching the sync-on-address behavior
        }

        timeout = TimeSpan.FromSeconds(seconds) + TimeSpan.FromTicks(
            unchecked((long)nanoseconds) * TimeSpan.TicksPerSecond / 1_000_000_000);

        if (ctx.TryReadUInt32(timeoutAddress + 16, out var flags))
        {
            absolute = (flags & UmtxAbstime) != 0;
        }

        return true;
    }

    private static TimeSpan AbsoluteRemaining(TimeSpan absoluteDeadline)
    {
        // The guest's realtime clock tracks host UTC closely enough for
        // deadline math on lock waits.
        var guestRealtime = DateTimeOffset.UtcNow - DateTimeOffset.FromUnixTimeSeconds(0);
        return absoluteDeadline - guestRealtime;
    }

    private static int LogUnknownOp(int op)
    {
        var count = _unknownOpCounts.AddOrUpdate(op, 1, static (_, current) => current + 1);
        if (count == 1 || count == 100_000)
        {
            Console.Error.WriteLine(
                $"[UMTX][WARN] Unhandled _umtx_op {op} (seen {count}x); returning success");
        }

        return ErrnoOk;
    }
}
