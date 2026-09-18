// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.Core.Memory;
using SharpEmu.HLE;
using SharpEmu.HLE.Host;
using SharpEmu.Libs.Kernel;
using System.Globalization;
using System.Text;
using Xunit;

namespace SharpEmu.Libs.Tests.Kernel;

[CollectionDefinition(KernelMemoryCompatStateCollection.Name, DisableParallelization = true)]
public sealed class KernelMemoryCompatStateCollection
{
    public const string Name = "KernelMemoryCompatState";
}

[Collection(KernelMemoryCompatStateCollection.Name)]
public sealed class KernelMemoryCompatExportsTests
{
    private const ulong GuestMemoryBase = 0x1_0000_0000;
    private const ulong AllocationOutAddress = GuestMemoryBase + 0x100;
    private const ulong SpanStartOutAddress = GuestMemoryBase + 0x108;
    private const ulong SpanSizeOutAddress = GuestMemoryBase + 0x110;

    [Fact]
    public void PosixStat_MissingFileReturnsMinusOne()
    {
        const ulong memoryBase = 0x1_0000_0000;
        const ulong pathAddress = memoryBase + 0x100;
        const ulong statAddress = memoryBase + 0x400;
        var memory = new FakeCpuMemory(memoryBase, 0x1000);
        var context = new CpuContext(memory, Generation.Gen5);
        memory.WriteCString(pathAddress, "/__sharpemu_test_missing__/shader.cache");
        context[CpuRegister.Rdi] = pathAddress;
        context[CpuRegister.Rsi] = statAddress;

        var result = KernelMemoryCompatExports.PosixStat(context);

        Assert.Equal(-1, result);
        Assert.Equal(ulong.MaxValue, context[CpuRegister.Rax]);
    }

    [Fact]
    public void PosixOpen_MissingFileReturnsMinusOne()
    {
        const ulong memoryBase = 0x1_0000_0000;
        const ulong pathAddress = memoryBase + 0x100;
        var memory = new FakeCpuMemory(memoryBase, 0x1000);
        var context = new CpuContext(memory, Generation.Gen5);
        memory.WriteCString(pathAddress, "/__sharpemu_test_missing__/il2cpp.usym");
        context[CpuRegister.Rdi] = pathAddress;
        context[CpuRegister.Rsi] = 0; // O_RDONLY

        var result = KernelMemoryCompatExports.PosixOpen(context);

        // A libc open() failure must be -1, not the raw 0x8002xxxx sentinel the
        // guest would otherwise store as a valid fd and later dereference.
        Assert.Equal(-1, result);
        Assert.Equal(ulong.MaxValue, context[CpuRegister.Rax]);
    }

    [Fact]
    public void PosixFstat_BadDescriptorReturnsMinusOne()
    {
        const ulong memoryBase = 0x1_0000_0000;
        const ulong statAddress = memoryBase + 0x400;
        var memory = new FakeCpuMemory(memoryBase, 0x1000);
        var context = new CpuContext(memory, Generation.Gen5);
        context[CpuRegister.Rdi] = 0x80020002; // the not-found sentinel misused as an fd
        context[CpuRegister.Rsi] = statAddress;

        var result = KernelMemoryCompatExports.PosixFstat(context);

        Assert.Equal(-1, result);
        Assert.Equal(ulong.MaxValue, context[CpuRegister.Rax]);
    }

    [Fact]
    public void PosixClose_BadDescriptorReturnsMinusOne()
    {
        const ulong memoryBase = 0x1_0000_0000;
        var memory = new FakeCpuMemory(memoryBase, 0x1000);
        var context = new CpuContext(memory, Generation.Gen5);
        context[CpuRegister.Rdi] = 0x80020002; // never-opened / sentinel fd

        var result = KernelMemoryCompatExports.PosixClose(context);

        Assert.Equal(-1, result);
        Assert.Equal(ulong.MaxValue, context[CpuRegister.Rax]);
    }

    [Fact]
    public void PosixRead_BadDescriptorReturnsMinusOne()
    {
        const ulong memoryBase = 0x1_0000_0000;
        const ulong bufferAddress = memoryBase + 0x200;
        var memory = new FakeCpuMemory(memoryBase, 0x1000);
        var context = new CpuContext(memory, Generation.Gen5);
        context[CpuRegister.Rdi] = 0x80020002; // never-opened / sentinel fd
        context[CpuRegister.Rsi] = bufferAddress;
        context[CpuRegister.Rdx] = 0x40;

        var result = KernelMemoryCompatExports.PosixRead(context);

        Assert.Equal(-1, result);
        Assert.Equal(ulong.MaxValue, context[CpuRegister.Rax]);
    }

    [Fact]
    public void PosixWrite_BadDescriptorReturnsMinusOne()
    {
        const ulong memoryBase = 0x1_0000_0000;
        const ulong bufferAddress = memoryBase + 0x200;
        var memory = new FakeCpuMemory(memoryBase, 0x1000);
        var context = new CpuContext(memory, Generation.Gen5);
        memory.WriteCString(bufferAddress, "payload");
        context[CpuRegister.Rdi] = 0x80020002; // never-opened / sentinel fd
        context[CpuRegister.Rsi] = bufferAddress;
        context[CpuRegister.Rdx] = 0x7;

        var result = KernelMemoryCompatExports.PosixWrite(context);

        Assert.Equal(-1, result);
        Assert.Equal(ulong.MaxValue, context[CpuRegister.Rax]);
    }

    [Fact]
    public void Sprintf_ReadsVariadicDoubleFromXmmRegister()
    {
        const ulong memoryBase = 0x1_0000_0000;
        const ulong destinationAddress = memoryBase + 0x100;
        const ulong formatAddress = memoryBase + 0x200;
        var memory = new FakeCpuMemory(memoryBase, 0x1000);
        var context = new CpuContext(memory, Generation.Gen5);
        memory.WriteCString(formatAddress, "%.4f");
        context[CpuRegister.Rdi] = destinationAddress;
        context[CpuRegister.Rsi] = formatAddress;
        context.SetXmmRegister(
            0,
            unchecked((ulong)BitConverter.DoubleToInt64Bits(0.5576)),
            0);

        var previousCulture = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("es-ES");

            var result = KernelMemoryCompatExports.Sprintf(context);

            Assert.Equal(0, result);
            Assert.Equal(6UL, context[CpuRegister.Rax]);
            Span<byte> output = stackalloc byte[7];
            Assert.True(memory.TryRead(destinationAddress, output));
            Assert.Equal("0.5576\0", Encoding.UTF8.GetString(output));
        }
        finally
        {
            CultureInfo.CurrentCulture = previousCulture;
        }
    }

    [Fact]
    public void AvailableDirectMemorySize_FragmentedRangeReturnsLargestAlignedSpan()
    {
        const ulong firstAllocationStart = 0x0020_0000;
        const ulong firstAllocationLength = 0x0020_0000;
        const ulong secondAllocationStart = 0x00C0_0000;
        const ulong secondAllocationLength = 0x0040_0000;
        var context = new CpuContext(new FakeCpuMemory(GuestMemoryBase, 0x1000), Generation.Gen5);

        try
        {
            AllocateDirectMemory(context, firstAllocationStart, firstAllocationLength);
            AllocateDirectMemory(context, secondAllocationStart, secondAllocationLength);

            QueryAvailableDirectMemory(context, 0, 0x0100_0000, 0x4000);

            Assert.True(context.TryReadUInt64(SpanStartOutAddress, out var spanStart));
            Assert.True(context.TryReadUInt64(SpanSizeOutAddress, out var spanSize));
            Assert.Equal(0x0040_0000UL, spanStart);
            Assert.Equal(0x0080_0000UL, spanSize);
        }
        finally
        {
            ReleaseDirectMemory(context, firstAllocationStart, firstAllocationLength);
            ReleaseDirectMemory(context, secondAllocationStart, secondAllocationLength);
        }
    }

    [Fact]
    public void AvailableDirectMemorySize_AppliesAlignmentBeforeComparingSpans()
    {
        const ulong allocationStart = 0x0070_0000;
        const ulong allocationLength = 0x0010_0000;
        var context = new CpuContext(new FakeCpuMemory(GuestMemoryBase, 0x1000), Generation.Gen5);

        try
        {
            AllocateDirectMemory(context, allocationStart, allocationLength);

            QueryAvailableDirectMemory(context, 0x0010_0000, 0x00C0_0000, 0x0040_0000);

            Assert.True(context.TryReadUInt64(SpanStartOutAddress, out var spanStart));
            Assert.True(context.TryReadUInt64(SpanSizeOutAddress, out var spanSize));
            Assert.Equal(0x0080_0000UL, spanStart);
            Assert.Equal(0x0040_0000UL, spanSize);
        }
        finally
        {
            ReleaseDirectMemory(context, allocationStart, allocationLength);
        }
    }

    private static void AllocateDirectMemory(CpuContext context, ulong start, ulong length)
    {
        context[CpuRegister.Rdi] = start;
        context[CpuRegister.Rsi] = start + length;
        context[CpuRegister.Rdx] = length;
        context[CpuRegister.Rcx] = 0x4000;
        context[CpuRegister.R8] = 0;
        context[CpuRegister.R9] = AllocationOutAddress;

        Assert.Equal(0, KernelMemoryCompatExports.KernelAllocateDirectMemory(context));
        Assert.True(context.TryReadUInt64(AllocationOutAddress, out var allocatedAddress));
        Assert.Equal(start, allocatedAddress);
    }

    private static void QueryAvailableDirectMemory(
        CpuContext context,
        ulong searchStart,
        ulong searchEnd,
        ulong alignment)
    {
        context[CpuRegister.Rdi] = searchStart;
        context[CpuRegister.Rsi] = searchEnd;
        context[CpuRegister.Rdx] = alignment;
        context[CpuRegister.Rcx] = SpanStartOutAddress;
        context[CpuRegister.R8] = SpanSizeOutAddress;

        Assert.Equal(0, KernelMemoryCompatExports.KernelAvailableDirectMemorySize(context));
    }

    private static void ReleaseDirectMemory(CpuContext context, ulong start, ulong length)
    {
        context[CpuRegister.Rdi] = start;
        context[CpuRegister.Rsi] = length;

        Assert.Equal(0, KernelMemoryCompatExports.KernelReleaseDirectMemory(context));
    }

    [Fact]
    public void MapNamedFlexibleMemory_NullInOutPointerReturnsInvalidArgument()
    {
        var memory = new FakeCpuMemory(0x1_0000_0000, 0x1000);
        var context = new CpuContext(memory, Generation.Gen5);
        context[CpuRegister.Rdi] = 0;
        context[CpuRegister.Rsi] = 0x1000;
        context[CpuRegister.Rdx] = 0x03; // CPU read|write
        context[CpuRegister.Rcx] = 0;

        var result = KernelMemoryCompatExports.KernelMapNamedFlexibleMemory(context);

        Assert.Equal((int)OrbisGen2Result.ORBIS_GEN2_ERROR_INVALID_ARGUMENT, result);
    }

    [Fact]
    public void MapNamedFlexibleMemory_ZeroLengthReturnsInvalidArgument()
    {
        const ulong memoryBase = 0x1_0000_0000;
        const ulong inOutAddress = memoryBase + 0x100;
        var memory = new FakeCpuMemory(memoryBase, 0x1000);
        var context = new CpuContext(memory, Generation.Gen5);
        memory.TryWrite(inOutAddress, BitConverter.GetBytes(0UL));
        context[CpuRegister.Rdi] = inOutAddress;
        context[CpuRegister.Rsi] = 0;
        context[CpuRegister.Rdx] = 0x03;
        context[CpuRegister.Rcx] = 0;

        var result = KernelMemoryCompatExports.KernelMapNamedFlexibleMemory(context);

        Assert.Equal((int)OrbisGen2Result.ORBIS_GEN2_ERROR_INVALID_ARGUMENT, result);
    }

    [Fact]
    public void MapNamedFlexibleMemory_UnreadableInOutPointerReturnsMemoryFault()
    {
        // The in-out pointer points outside the FakeCpuMemory backing store, so
        // the first TryReadUInt64 must fail before any reservation is attempted.
        const ulong memoryBase = 0x1_0000_0000;
        const ulong unreachableInOut = memoryBase + 0x10_0000;
        var memory = new FakeCpuMemory(memoryBase, 0x1000);
        var context = new CpuContext(memory, Generation.Gen5);
        context[CpuRegister.Rdi] = unreachableInOut;
        context[CpuRegister.Rsi] = 0x1000;
        context[CpuRegister.Rdx] = 0x03;
        context[CpuRegister.Rcx] = 0;

        var result = KernelMemoryCompatExports.KernelMapNamedFlexibleMemory(context);

        Assert.Equal((int)OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT, result);
    }

    [Fact]
    public void VirtualQuery_PreservesReservationPastFixedCommitAtSameBase()
    {
        const ulong memoryBase = 0x12_0000_0000;
        const ulong reservedLength = 0x1_0000;
        const ulong committedLength = 0x2000;
        const ulong inOutAddress = memoryBase + 0x1_7000;
        const ulong infoAddress = memoryBase + 0x1_8000;
        var memory = new FakeCpuMemory(memoryBase, 0x2_0000);
        var context = new CpuContext(memory, Generation.Gen5);

        KernelMemoryCompatExports.RegisterReservedVirtualRange(memoryBase, reservedLength);
        Assert.True(memory.TryWrite(inOutAddress, BitConverter.GetBytes(memoryBase)));
        context[CpuRegister.Rdi] = inOutAddress;
        context[CpuRegister.Rsi] = committedLength;
        context[CpuRegister.Rdx] = 0x03;
        context[CpuRegister.Rcx] = 0x10; // fixed mapping

        Assert.Equal(0, KernelMemoryCompatExports.KernelMapNamedFlexibleMemory(context));

        context[CpuRegister.Rdi] = memoryBase + 0x8000;
        context[CpuRegister.Rsi] = 0;
        context[CpuRegister.Rdx] = infoAddress;
        context[CpuRegister.Rcx] = 0x48;

        Assert.Equal(0, KernelMemoryCompatExports.KernelVirtualQuery(context));
        Assert.True(context.TryReadUInt64(infoAddress, out var regionStart));
        Assert.True(context.TryReadUInt64(infoAddress + 8, out var regionEnd));
        Assert.Equal(memoryBase + committedLength, regionStart);
        Assert.Equal(memoryBase + reservedLength, regionEnd);
    }

    [Fact]
    public void Mprotect_ZeroAddressReturnsInvalidArgument()
    {
        var memory = new FakeCpuMemory(0x1_0000_0000, 0x1000);
        var context = new CpuContext(memory, Generation.Gen5);
        context[CpuRegister.Rdi] = 0;
        context[CpuRegister.Rsi] = 0x4000;
        context[CpuRegister.Rdx] = 0x03;

        var result = KernelMemoryCompatExports.KernelMprotect(context);

        Assert.Equal((int)OrbisGen2Result.ORBIS_GEN2_ERROR_INVALID_ARGUMENT, result);
    }

    [Fact]
    public void Mprotect_ZeroLengthReturnsInvalidArgument()
    {
        const ulong memoryBase = 0x1_0000_0000;
        var memory = new FakeCpuMemory(memoryBase, 0x1000);
        var context = new CpuContext(memory, Generation.Gen5);
        context[CpuRegister.Rdi] = memoryBase;
        context[CpuRegister.Rsi] = 0;
        context[CpuRegister.Rdx] = 0x03;

        var result = KernelMemoryCompatExports.KernelMprotect(context);

        Assert.Equal((int)OrbisGen2Result.ORBIS_GEN2_ERROR_INVALID_ARGUMENT, result);
    }

    [Fact]
    public void Mprotect_UnmappedRangeReturnsNotFound()
    {
        // A plausible guest address that FakeCpuMemory does not back, that is
        // in no kernel mapping region, and that has no host reservation.
        // MprotectCore's guest-ownership gate rejects it before any host
        // protection call; with nothing host-committed there either, the
        // result is the NOT_FOUND the raw VirtualProtect path used to return
        // for unmapped ranges.
        const ulong unmappedAddress = 0x2_0000_0000;
        var memory = new FakeCpuMemory(0x1_0000_0000, 0x1000);
        var context = new CpuContext(memory, Generation.Gen5);
        context[CpuRegister.Rdi] = unmappedAddress;
        context[CpuRegister.Rsi] = 0x4000;
        context[CpuRegister.Rdx] = 0x03;

        var result = KernelMemoryCompatExports.KernelMprotect(context);

        Assert.Equal((int)OrbisGen2Result.ORBIS_GEN2_ERROR_NOT_FOUND, result);
    }

    // ---- mprotect host-memory sandbox (stability-mem) ----

    // 1. A valid guest mapping may be re-protected: the call succeeds, the
    //    host page protection actually changes, and the kernel mapping table
    //    reflects the new protection for the covered pages.
    [Fact]
    public void Mprotect_ValidGuestRangeSucceedsAndChangesAccessibility()
    {
        const ulong guestBase = 0x3000_0000_0000;
        using var host = new RecordingGuestHostMemory();
        using var memory = new PhysicalVirtualMemory(host);
        var context = new CpuContext(memory, Generation.Gen5);
        var mappedBase = MapFlexibleFixed(context, memory, host, guestBase, 0x8000, protection: 0x03);
        host.ProtectionCalls.Clear();

        context[CpuRegister.Rdi] = mappedBase;
        context[CpuRegister.Rsi] = 0x4000;
        context[CpuRegister.Rdx] = 0x01; // PROT_CPU_READ: drop write access

        var result = KernelMemoryCompatExports.KernelMprotect(context);

        Assert.Equal((int)OrbisGen2Result.ORBIS_GEN2_OK, result);

        // The host page protection changed for exactly the requested 16 KiB
        // page, through the backing memory's protect seam.
        Assert.Equal([(mappedBase, 0x4000UL, HostPageProtection.ReadOnly)], host.ProtectionCalls);

        // The mapping table reports the first page read-only while the
        // untouched second page keeps its read-write protection.
        var startOut = host.ScratchAddress + 0x100;
        var endOut = host.ScratchAddress + 0x108;
        var protectionOut = host.ScratchAddress + 0x110;
        context[CpuRegister.Rdi] = mappedBase;
        context[CpuRegister.Rsi] = startOut;
        context[CpuRegister.Rdx] = endOut;
        context[CpuRegister.Rcx] = protectionOut;
        Assert.Equal(0, KernelMemoryCompatExports.KernelQueryMemoryProtection(context));
        Assert.True(context.TryReadUInt64(startOut, out var regionStart));
        Assert.True(context.TryReadUInt64(endOut, out var regionEnd));
        Assert.Equal(mappedBase, regionStart);
        Assert.Equal(mappedBase + 0x3FFF, regionEnd);
        Span<byte> protectionBytes = stackalloc byte[4];
        Assert.True(context.Memory.TryRead(protectionOut, protectionBytes));
        Assert.Equal(0x01, BitConverter.ToInt32(protectionBytes));
    }

    [Fact]
    public void Mprotect_RangeAcrossAdjacentHostReservationsSucceeds()
    {
        const ulong guestBase = 0x3300_0000_0000;
        using var host = new RecordingGuestHostMemory();
        using var memory = new PhysicalVirtualMemory(host);
        var context = new CpuContext(memory, Generation.Gen5);
        MapFlexibleFixed(context, memory, host, guestBase, 0x10000, protection: 0x03);
        MapFlexibleFixed(context, memory, host, guestBase + 0x10000, 0x20000, protection: 0x03);
        host.ProtectionCalls.Clear();

        context[CpuRegister.Rdi] = guestBase + 0x8000;
        context[CpuRegister.Rsi] = 0x20000;
        context[CpuRegister.Rdx] = 0x01;

        Assert.Equal(0, KernelMemoryCompatExports.KernelMprotect(context));
        Assert.Equal([
            (guestBase + 0x8000, 0x8000UL, HostPageProtection.ReadOnly),
            (guestBase + 0x10000, 0x18000UL, HostPageProtection.ReadOnly)
        ], host.ProtectionCalls);
    }

    [Fact]
    public void Protect_LaterHostFailureRestoresEarlierReservation()
    {
        const ulong guestBase = 0x3400_0000_0000;
        using var host = new RecordingGuestHostMemory();
        using var memory = new PhysicalVirtualMemory(host);
        Assert.Equal(guestBase, memory.AllocateAt(guestBase, 0x10000, executable: false));
        Assert.Equal(guestBase + 0x10000, memory.AllocateAt(guestBase + 0x10000, 0x10000, executable: false));
        Assert.Equal(guestBase + 0x20000, memory.AllocateAt(guestBase + 0x20000, 0x10000, executable: false));
        host.RawProtections[guestBase] = 0x02; // read-only
        host.RawProtections[guestBase + 0x10000] = 0x20; // execute-read
        host.FailProtectionAt = guestBase + 0x20000;

        Assert.False(memory.TryProtect(guestBase, 0x30000, GuestPageProtection.Read | GuestPageProtection.Write));
        Assert.Equal([
            (guestBase + 0x10000, 0x10000UL, 0x20U),
            (guestBase, 0x10000UL, 0x02U)
        ], host.RawProtectionCalls);
    }

    [Fact]
    public void Protect_UncommittedTailDoesNotChangeEarlierPages()
    {
        const ulong guestBase = 0x3500_0000_0000;
        using var host = new RecordingGuestHostMemory();
        using var memory = new PhysicalVirtualMemory(host);
        Assert.Equal(guestBase, memory.AllocateAt(guestBase, 0x10000, executable: false));
        Assert.Equal(guestBase + 0x10000, memory.AllocateAt(guestBase + 0x10000, 0x10000, executable: false));
        host.UncommittedBase = guestBase + 0x10000;

        Assert.False(memory.TryProtect(guestBase, 0x20000, GuestPageProtection.Read));
        Assert.Empty(host.ProtectionCalls);
    }

    // 2. A host-committed address that is not a guest mapping is the sandbox
    //    escape itself: the old code pushed it straight into VirtualProtect,
    //    which succeeds and retags emulator/runtime-owned pages. It must be
    //    rejected without touching the host protection.
    [Fact]
    public unsafe void Mprotect_NonGuestHostAddressIsRejectedWithoutTouchingHostProtection()
    {
        // Real host pages the guest never mapped. On POSIX this is a
        // HostMemory-tracked allocation — the same class of pages the
        // execution backend's stubs, TLS handlers, and abort stacks live in —
        // and on Windows any committed page.
        const nuint blockSize = 0x10000;
        var block = (ulong)HostMemory.Alloc(
            (void*)0x600_0000_0000,
            blockSize,
            HostMemory.MEM_RESERVE | HostMemory.MEM_COMMIT,
            HostMemory.PAGE_READWRITE);
        if (block == 0)
        {
            block = (ulong)HostMemory.Alloc(
                null,
                blockSize,
                HostMemory.MEM_RESERVE | HostMemory.MEM_COMMIT,
                HostMemory.PAGE_READWRITE);
        }

        Assert.NotEqual(0UL, block);
        try
        {
            // A 16 KiB-aligned window fully inside the block, so the Orbis
            // page alignment cannot pull the request outside it.
            var target = (block + 0x3FFF) & ~0x3FFFUL;
            Assert.True(target + 0x4000 <= block + blockSize);
            Assert.True(HostMemory.Query((void*)target, out var before) != 0);
            Assert.Equal(HostMemory.MEM_COMMIT, before.State);

            var memory = new FakeCpuMemory(0x1_0000_0000, 0x1000);
            var context = new CpuContext(memory, Generation.Gen5);
            context[CpuRegister.Rdi] = target;
            context[CpuRegister.Rsi] = 0x4000;
            context[CpuRegister.Rdx] = 0x03;

            var result = KernelMemoryCompatExports.KernelMprotect(context);

            // EACCES-flavoured rejection, and the host protection is
            // byte-for-byte what it was before the call.
            Assert.Equal((int)OrbisGen2Result.ORBIS_GEN2_ERROR_PERMISSION_DENIED, result);
            Assert.True(HostMemory.Query((void*)target, out var after) != 0);
            Assert.Equal(before.Protect, after.Protect);
            Assert.Equal(HostMemory.MEM_COMMIT, after.State);
        }
        finally
        {
            HostMemory.Free((void*)block, 0, HostMemory.MEM_RELEASE);
        }
    }

    // 3. The POSIX alias (imported by libcohtml's embedded V8 for JIT page
    //    permissions) delegates to the same MprotectCore, so both entry
    //    points accept the same ranges and apply the same protections.
    [Fact]
    public void PosixMprotect_MatchesKernelMprotectBehavior()
    {
        const ulong guestBase = 0x3100_0000_0000;
        using var host = new RecordingGuestHostMemory();
        using var memory = new PhysicalVirtualMemory(host);
        var context = new CpuContext(memory, Generation.Gen5);
        var mappedBase = MapFlexibleFixed(context, memory, host, guestBase, 0x4000, protection: 0x03);
        host.ProtectionCalls.Clear();

        // Valid guest range: both entry points succeed and request the same
        // host protection change.
        context[CpuRegister.Rdi] = mappedBase;
        context[CpuRegister.Rsi] = 0x4000;
        context[CpuRegister.Rdx] = 0x01;
        Assert.Equal((int)OrbisGen2Result.ORBIS_GEN2_OK, KernelMemoryCompatExports.PosixMprotect(context));
        Assert.Equal((int)OrbisGen2Result.ORBIS_GEN2_OK, KernelMemoryCompatExports.KernelMprotect(context));
        Assert.Equal(2, host.ProtectionCalls.Count);
        Assert.All(
            host.ProtectionCalls,
            call => Assert.Equal((mappedBase, 0x4000UL, HostPageProtection.ReadOnly), call));

        // Unmapped range: identical rejection, and no extra host protection
        // changes.
        context[CpuRegister.Rdi] = mappedBase + 0x10000;
        context[CpuRegister.Rsi] = 0x4000;
        context[CpuRegister.Rdx] = 0x03;
        Assert.Equal(
            (int)OrbisGen2Result.ORBIS_GEN2_ERROR_NOT_FOUND,
            KernelMemoryCompatExports.PosixMprotect(context));
        Assert.Equal(
            (int)OrbisGen2Result.ORBIS_GEN2_ERROR_NOT_FOUND,
            KernelMemoryCompatExports.KernelMprotect(context));
        Assert.Equal(2, host.ProtectionCalls.Count);
    }

    // 4. Zero-length requests stay INVALID_ARGUMENT, and a range that starts
    //    inside a guest mapping but runs past its end is rejected outright
    //    (all-or-nothing, POSIX ENOMEM semantics) — never clamped — while a
    //    sub-range that 16 KiB alignment expands to the whole mapping still
    //    succeeds.
    [Fact]
    public void Mprotect_ZeroLengthAndPartialOverlapAreRejected()
    {
        const ulong guestBase = 0x3200_0000_0000;
        using var host = new RecordingGuestHostMemory();
        using var memory = new PhysicalVirtualMemory(host);
        var context = new CpuContext(memory, Generation.Gen5);
        var mappedBase = MapFlexibleFixed(context, memory, host, guestBase, 0x8000, protection: 0x03);
        host.ProtectionCalls.Clear();

        context[CpuRegister.Rdx] = 0x03;
        context[CpuRegister.Rdi] = 0;
        context[CpuRegister.Rsi] = 0x4000;
        Assert.Equal(
            (int)OrbisGen2Result.ORBIS_GEN2_ERROR_INVALID_ARGUMENT,
            KernelMemoryCompatExports.KernelMprotect(context));

        context[CpuRegister.Rdi] = mappedBase;
        context[CpuRegister.Rsi] = 0;
        Assert.Equal(
            (int)OrbisGen2Result.ORBIS_GEN2_ERROR_INVALID_ARGUMENT,
            KernelMemoryCompatExports.KernelMprotect(context));

        // Starts on the mapping's second page but extends a full page past
        // its end: rejected, no host protection changes at all.
        context[CpuRegister.Rdi] = mappedBase + 0x4000;
        context[CpuRegister.Rsi] = 0x8000;
        context[CpuRegister.Rdx] = 0x01;
        Assert.Equal(
            (int)OrbisGen2Result.ORBIS_GEN2_ERROR_NOT_FOUND,
            KernelMemoryCompatExports.KernelMprotect(context));
        Assert.Empty(host.ProtectionCalls);

        // Page alignment expands this interior range to the whole mapping,
        // which is fully guest-owned, so it succeeds.
        context[CpuRegister.Rdi] = mappedBase + 0x2000;
        context[CpuRegister.Rsi] = 0x4000;
        Assert.Equal((int)OrbisGen2Result.ORBIS_GEN2_OK, KernelMemoryCompatExports.KernelMprotect(context));
        Assert.Equal([(mappedBase, 0x8000UL, HostPageProtection.ReadOnly)], host.ProtectionCalls);
    }

    /// <summary>
    /// Maps a fixed flexible-memory range through the real export so the
    /// kernel mapping table and the backing address space agree, exactly like
    /// a game calling sceKernelMapNamedFlexibleMemory. The in/out pointer
    /// lives on one real scratch page allocated through the memory under test
    /// so the address space has a region backing it.
    /// </summary>
    private static ulong MapFlexibleFixed(
        CpuContext context,
        PhysicalVirtualMemory memory,
        RecordingGuestHostMemory host,
        ulong requestedAddress,
        ulong length,
        int protection)
    {
        var inOutAddress = memory.AllocateAt(0, 0x1000, executable: false);
        Assert.Equal(host.ScratchAddress, inOutAddress);
        Assert.True(context.Memory.TryWrite(inOutAddress, BitConverter.GetBytes(requestedAddress)));
        context[CpuRegister.Rdi] = inOutAddress;
        context[CpuRegister.Rsi] = length;
        context[CpuRegister.Rdx] = unchecked((ulong)(uint)protection);
        context[CpuRegister.Rcx] = 0x10; // fixed mapping at *inOutAddress

        Assert.Equal(0, KernelMemoryCompatExports.KernelMapNamedFlexibleMemory(context));
        Assert.True(context.TryReadUInt64(inOutAddress, out var mappedAddress));
        Assert.Equal(requestedAddress, mappedAddress);
        return mappedAddress;
    }

    /// <summary>
    /// Recording stand-in for the host page table behind
    /// <see cref="PhysicalVirtualMemory"/>: tracked allocations answer Query
    /// as committed pages, one real page backs the scratch allocation so the
    /// in/out pointer is genuinely readable/writable, and every Protect call
    /// is recorded so tests can assert exactly which host protections
    /// changed.
    /// </summary>
    private sealed class RecordingGuestHostMemory : IHostMemory, IDisposable
    {
        private readonly nint _scratchAllocation;
        private readonly ulong _scratchAddress;
        private readonly SortedList<ulong, ulong> _backedRanges = new();
        private bool _disposed;

        public RecordingGuestHostMemory()
        {
            _scratchAllocation = System.Runtime.InteropServices.Marshal.AllocHGlobal(0x2000);
            _scratchAddress = (unchecked((ulong)_scratchAllocation) + 0xFFF) & ~0xFFFUL;
            _backedRanges[_scratchAddress] = 0x1000;
        }

        public List<(ulong Address, ulong Size, HostPageProtection Protection)> ProtectionCalls { get; } = [];
        public List<(ulong Address, ulong Size, uint Protection)> RawProtectionCalls { get; } = [];
        public Dictionary<ulong, uint> RawProtections { get; } = [];
        public ulong FailProtectionAt { get; set; }
        public ulong UncommittedBase { get; set; }

        public ulong ScratchAddress => _scratchAddress;

        public ulong Allocate(ulong desiredAddress, ulong size, HostPageProtection protection)
        {
            if (size == 0)
            {
                return 0;
            }

            if (desiredAddress == 0)
            {
                return _scratchAddress;
            }

            if (Overlaps(desiredAddress, size))
            {
                return 0;
            }

            _backedRanges[desiredAddress] = size;
            return desiredAddress;
        }

        public ulong Reserve(ulong desiredAddress, ulong size, HostPageProtection protection) =>
            Allocate(desiredAddress, size, protection);

        public bool Commit(ulong address, ulong size, HostPageProtection protection) => true;

        public bool Free(ulong address) => _backedRanges.Remove(address);

        public bool Protect(ulong address, ulong size, HostPageProtection protection, out uint rawOldProtection)
        {
            ProtectionCalls.Add((address, size, protection));
            var found = Query(address, out var region);
            rawOldProtection = region.RawProtection;
            // VirtualProtect requires every page to belong to one reservation.
            return address != FailProtectionAt && found &&
                region.State == HostRegionState.Committed &&
                size <= region.BaseAddress + region.RegionSize - address;
        }

        public bool ProtectRaw(ulong address, ulong size, uint rawProtection, out uint rawOldProtection)
        {
            RawProtectionCalls.Add((address, size, rawProtection));
            rawOldProtection = 0x04;
            return true;
        }

        public bool Query(ulong address, out HostRegionInfo info)
        {
            foreach (var (baseAddress, size) in _backedRanges)
            {
                if (address >= baseAddress && address < baseAddress + size)
                {
                    info = new HostRegionInfo(
                        baseAddress,
                        baseAddress,
                        size,
                        baseAddress == UncommittedBase ? HostRegionState.Reserved : HostRegionState.Committed,
                        RawState: 0x1000,
                        HostPageProtection.ReadWrite,
                        RawProtection: RawProtections.GetValueOrDefault(baseAddress, 0x04U),
                        RawAllocationProtection: 0x04);
                    return true;
                }
            }

            // Free run up to the next tracked range (or a bounded stride) so
            // VirtualQuery-style walks advance over whole gaps.
            var nextBase = ulong.MaxValue;
            foreach (var baseAddress in _backedRanges.Keys)
            {
                if (baseAddress > address)
                {
                    nextBase = baseAddress;
                    break;
                }
            }

            var regionSize = nextBase == ulong.MaxValue ? 0x10_0000 : nextBase - address;
            info = new HostRegionInfo(
                address,
                0,
                regionSize,
                HostRegionState.Free,
                RawState: 0x10000,
                HostPageProtection.NoAccess,
                RawProtection: 0x01,
                RawAllocationProtection: 0);
            return true;
        }

        public void FlushInstructionCache(ulong address, ulong size)
        {
        }

        private bool Overlaps(ulong address, ulong size)
        {
            foreach (var (baseAddress, trackedSize) in _backedRanges)
            {
                if (address < baseAddress + trackedSize && baseAddress < address + size)
                {
                    return true;
                }
            }

            return false;
        }

        public void Dispose()
        {
            if (!_disposed)
            {
                System.Runtime.InteropServices.Marshal.FreeHGlobal(_scratchAllocation);
                _disposed = true;
            }
        }
    }

    [Fact]
    public void Munmap_ZeroAddressReturnsInvalidArgument()
    {
        var memory = new FakeCpuMemory(0x1_0000_0000, 0x1000);
        var context = new CpuContext(memory, Generation.Gen5);
        context[CpuRegister.Rdi] = 0;
        context[CpuRegister.Rsi] = 0x4000;

        var result = KernelMemoryCompatExports.KernelMunmap(context);

        Assert.Equal((int)OrbisGen2Result.ORBIS_GEN2_ERROR_INVALID_ARGUMENT, result);
    }

    [Fact]
    public void Munmap_OverflowRangeReturnsInvalidArgument()
    {
        // address + length would overflow; KernelMunmap guards this explicitly
        // before touching any region accounting.
        var memory = new FakeCpuMemory(0x1_0000_0000, 0x1000);
        var context = new CpuContext(memory, Generation.Gen5);
        context[CpuRegister.Rdi] = ulong.MaxValue - 0x10;
        context[CpuRegister.Rsi] = 0x20;

        var result = KernelMemoryCompatExports.KernelMunmap(context);

        Assert.Equal((int)OrbisGen2Result.ORBIS_GEN2_ERROR_INVALID_ARGUMENT, result);
    }

    [Fact]
    public void Munmap_UnmappedRangeReturnsNotFound()
    {
        // No flexible region is registered at this address and FakeCpuMemory
        // does not back it, so both physicallyBacked and removedRegions are
        // empty and the export reports NOT_FOUND.
        const ulong unmappedAddress = 0x2_0000_0000;
        var memory = new FakeCpuMemory(0x1_0000_0000, 0x1000);
        var context = new CpuContext(memory, Generation.Gen5);
        context[CpuRegister.Rdi] = unmappedAddress;
        context[CpuRegister.Rsi] = 0x4000;

        var result = KernelMemoryCompatExports.KernelMunmap(context);

        Assert.Equal((int)OrbisGen2Result.ORBIS_GEN2_ERROR_NOT_FOUND, result);
    }
}
