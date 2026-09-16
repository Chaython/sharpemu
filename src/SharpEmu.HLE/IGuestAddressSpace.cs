// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

namespace SharpEmu.HLE;

/// <summary>
/// Guest address-space manipulation beyond plain allocation: fixed-address
/// mapping and page-protection changes. Guest addresses are identity-mapped
/// onto host pages by the implementing memory, so HLE exports (mmap, mprotect)
/// reach these operations through <c>ctx.Memory</c> instead of calling host
/// APIs directly. Member signatures deliberately mirror the implementation in
/// SharpEmu.Core so existing call sites migrate call-for-call.
/// </summary>
public interface IGuestAddressSpace : IGuestMemoryAllocator
{
    ulong AllocateAt(ulong desiredAddress, ulong size, bool executable = true, bool allowAlternative = true);

    /// <summary>
    /// Backs an entire fixed-address range, matching the guest's
    /// <c>SCE_KERNEL_MAP_FIXED</c> contract. Unlike <see cref="AllocateAt"/>, which
    /// reserves the range in one all-or-nothing host call, this walks the range and
    /// fills only the sub-ranges that are not already backed. That keeps a fixed
    /// mapping whole when part of the requested window is already occupied — the
    /// partial-overlap case where the single-call reservation fails outright and
    /// leaves the remainder unmapped for the guest to fault into.
    /// </summary>
    bool TryBackFixedRange(ulong address, ulong size, bool executable);

    /// <summary>
    /// Ensures every page in an existing guest mapping is committed for native
    /// execution. This is required before returning a raw identity-mapped range
    /// to guest code, which cannot pass through <c>ICpuMemory</c> on each access.
    /// </summary>
    bool TryCommitRange(ulong address, ulong size);

    bool TryAllocateAtOrAbove(ulong desiredAddress, ulong size, bool executable, ulong alignment, out ulong actualAddress);

    /// <summary>
    /// Reports whether <c>[address, address+size)</c> lies entirely inside
    /// guest mappings the backing memory knows about (loader segments,
    /// allocator ranges, fixed mappings), with no gap onto host pages the
    /// guest does not own. Protection-changing exports (mprotect and friends)
    /// must query this before touching host page protections: guest addresses
    /// are identity-mapped onto host pages, so an ungated protect call lets a
    /// guest-chosen address retag emulator- or runtime-owned memory — a
    /// host-memory sandbox escape.
    /// </summary>
    bool IsRangeGuestMapped(ulong address, ulong size);

    /// <summary>
    /// Changes host page protections for a guest range. Implementations must
    /// only accept ranges <see cref="IsRangeGuestMapped"/> (or their own
    /// region bookkeeping) vouches for; callers normally pre-validate with
    /// <see cref="IsRangeGuestMapped"/> so the check here is a backstop.
    /// </summary>
    bool TryProtect(ulong address, ulong size, GuestPageProtection protection);
}
