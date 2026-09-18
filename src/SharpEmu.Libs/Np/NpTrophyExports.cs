// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.HLE;
using System.Buffers.Binary;

namespace SharpEmu.Libs.Np;

/// <summary>
/// libSceNpTrophy (the classic, non-&quot;2&quot; trophy library). Contexts and handles
/// are opaque integer ids; unlock state is delegated to
/// <see cref="NpTrophyStore"/> so unlocks persist across emulator restarts.
/// Unlock callbacks, trophy configuration parsing, and the info/icon exports
/// remain documented gaps (sceNpTrophy2GetTrophyInfo keeps its NOT_FOUND
/// stance for the same reason).
/// </summary>
public static class NpTrophyExports
{
    // SceNpTrophyFlagArray: 128 x uint32 flag bits = up to 4096 trophy ids.
    private const int FlagArrayWords = 128;

    private static int _nextContext = 1;
    private static int _nextHandle = 1;

    [SysAbiExport(
        Nid = "XbkjbobZlCY",
        ExportName = "sceNpTrophyCreateContext",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceNpTrophy")]
    public static int NpTrophyCreateContext(CpuContext ctx)
    {
        return WriteIdAndReturn(ctx, ctx[CpuRegister.Rdi], ref _nextContext);
    }

    [SysAbiExport(
        Nid = "q7U6tEAQf7c",
        ExportName = "sceNpTrophyCreateHandle",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceNpTrophy")]
    public static int NpTrophyCreateHandle(CpuContext ctx)
    {
        return WriteIdAndReturn(ctx, ctx[CpuRegister.Rdi], ref _nextHandle);
    }

    [SysAbiExport(
        Nid = "E1Wrwd07Lr8",
        ExportName = "sceNpTrophyDestroyContext",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceNpTrophy")]
    public static int NpTrophyDestroyContext(CpuContext ctx) => ReturnOk(ctx);

    [SysAbiExport(
        Nid = "GNcF4oidY0Y",
        ExportName = "sceNpTrophyDestroyHandle",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceNpTrophy")]
    public static int NpTrophyDestroyHandle(CpuContext ctx) => ReturnOk(ctx);

    [SysAbiExport(
        Nid = "aTnHs7W-9Uk",
        ExportName = "sceNpTrophyAbortHandle",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceNpTrophy")]
    public static int NpTrophyAbortHandle(CpuContext ctx) => ReturnOk(ctx);

    [SysAbiExport(
        Nid = "TJCAxto9SEU",
        ExportName = "sceNpTrophyRegisterContext",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceNpTrophy")]
    public static int NpTrophyRegisterContext(CpuContext ctx) => ReturnOk(ctx);

    [SysAbiExport(
        Nid = "d9jpdPz5f-8",
        ExportName = "sceNpTrophyShowTrophyList",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceNpTrophy")]
    public static int NpTrophyShowTrophyList(CpuContext ctx) => ReturnOk(ctx);

    /// <summary>
    /// Gen4/Gen5 ABI: (context, handle, trophyId, int* platinumId). The unlock
    /// is recorded in <see cref="NpTrophyStore"/> with its timestamp; the
    /// platinum out slot always receives -1 because no platinum chaining is
    /// modeled (a documented gap).
    /// </summary>
    [SysAbiExport(
        Nid = "28xmRUFao68",
        ExportName = "sceNpTrophyUnlockTrophy",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceNpTrophy")]
    public static int NpTrophyUnlockTrophy(CpuContext ctx)
    {
        var trophyId = unchecked((int)ctx[CpuRegister.Rdx]);
        var platinumAddress = ctx[CpuRegister.Rcx];
        if (trophyId < 0)
        {
            return SetReturn(ctx, OrbisGen2Result.ORBIS_GEN2_ERROR_INVALID_ARGUMENT);
        }

        _ = NpTrophyStore.Unlock(trophyId, out _);
        if (platinumAddress != 0 && !ctx.TryWriteUInt32(platinumAddress, unchecked((uint)-1)))
        {
            return SetReturn(ctx, OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT);
        }

        return ReturnOk(ctx);
    }

    /// <summary>
    /// Gen4/Gen5 ABI: (context, handle, SceNpTrophyFlagArray* flagArray,
    /// uint32_t* count). The count operand is the flag-array capacity in
    /// 32-bit words on input and the number of words written on output; bit
    /// <c>(id % 32)</c> of word <c>(id / 32)</c> is set for each unlocked trophy
    /// id, so trophy 0 is the lowest bit of the first word. Ids at or above the
    /// array's 4096-flag capacity cannot be represented and are skipped.
    /// </summary>
    [SysAbiExport(
        Nid = "LHuSmO3SLd8",
        ExportName = "sceNpTrophyGetTrophyUnlockState",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceNpTrophy")]
    public static int NpTrophyGetTrophyUnlockState(CpuContext ctx)
    {
        var flagArrayAddress = ctx[CpuRegister.Rdx];
        var countAddress = ctx[CpuRegister.Rcx];
        if (flagArrayAddress == 0 || countAddress == 0)
        {
            return SetReturn(ctx, OrbisGen2Result.ORBIS_GEN2_ERROR_INVALID_ARGUMENT);
        }

        if (!ctx.TryReadUInt32(countAddress, out var capacityWords))
        {
            return SetReturn(ctx, OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT);
        }

        // The guest-declared capacity bounds the write so a short buffer is
        // never overrun, even if a title passes a smaller count than the full
        // 128-word struct.
        var words = (int)Math.Min(capacityWords, FlagArrayWords);
        Span<byte> flags = stackalloc byte[FlagArrayWords * sizeof(uint)];
        flags.Clear();
        foreach (var unlock in NpTrophyStore.GetUnlocked())
        {
            if (unlock.Id < 0 || unlock.Id >= FlagArrayWords * 32)
            {
                continue;
            }

            flags[unlock.Id / 8] |= (byte)(1u << (unlock.Id % 8));
        }

        if (words > 0)
        {
            if (!ctx.Memory.TryWrite(flagArrayAddress, flags[..(words * sizeof(uint))]) ||
                !ctx.TryWriteUInt32(countAddress, (uint)words))
            {
                return SetReturn(ctx, OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT);
            }
        }
        else if (!ctx.TryWriteUInt32(countAddress, 0))
        {
            return SetReturn(ctx, OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT);
        }

        return ReturnOk(ctx);
    }

    private static int WriteIdAndReturn(CpuContext ctx, ulong outAddress, ref int nextId)
    {
        if (outAddress == 0)
        {
            return SetReturn(ctx, OrbisGen2Result.ORBIS_GEN2_ERROR_INVALID_ARGUMENT);
        }

        Span<byte> idBytes = stackalloc byte[sizeof(int)];
        BinaryPrimitives.WriteInt32LittleEndian(idBytes, nextId);
        if (!ctx.Memory.TryWrite(outAddress, idBytes))
        {
            return SetReturn(ctx, OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT);
        }

        nextId++;
        return ReturnOk(ctx);
    }

    private static int ReturnOk(CpuContext ctx) => SetReturn(ctx, OrbisGen2Result.ORBIS_GEN2_OK);

    private static int SetReturn(CpuContext ctx, OrbisGen2Result result)
    {
        ctx[CpuRegister.Rax] = unchecked((ulong)(int)result);
        return (int)result;
    }
}
