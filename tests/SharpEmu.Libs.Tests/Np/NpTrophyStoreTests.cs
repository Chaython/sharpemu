// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Buffers.Binary;
using SharpEmu.HLE;
using SharpEmu.Libs.Np;
using Xunit;

namespace SharpEmu.Libs.Tests.Np;

[CollectionDefinition("NpTrophyState", DisableParallelization = true)]
public sealed class NpTrophyStateCollection;

// The trophy store persists to a real JSON file whose resolved path depends on
// process-wide environment and title configuration. The fixture pins both and
// the collection keeps other environment-mutating tests from running alongside.
[Collection("NpTrophyState")]
public sealed class NpTrophyStoreTests : IDisposable
{
    private const ulong Base = 0x1_0000_0000;
    private const ulong ContextOut = Base + 0x100;
    private const ulong HandleOut = Base + 0x108;
    private const ulong PlatinumOut = Base + 0x110;
    private const ulong FlagArray = Base + 0x200;
    private const ulong CountSlot = Base + 0x500;
    private const string TitleId = "NPTROPHYTEST";

    private readonly FakeCpuMemory _memory = new(Base, 0x4000);
    private readonly CpuContext _ctx;
    private readonly string _root;
    private readonly string? _previousRoot;

    public NpTrophyStoreTests()
    {
        _ctx = new CpuContext(_memory, Generation.Gen5);
        _root = Path.Combine(Path.GetTempPath(), $"sharpemu-nptrophy-{Guid.NewGuid():N}");
        _previousRoot = Environment.GetEnvironmentVariable("SHARPEMU_TROPHY_DIR");
        Environment.SetEnvironmentVariable("SHARPEMU_TROPHY_DIR", _root);
        NpTrophyStore.ConfigureApplicationInfo(TitleId);
        NpTrophyStore.Reload();
    }

    public void Dispose()
    {
        NpTrophyStore.ConfigureApplicationInfo(null);
        Environment.SetEnvironmentVariable("SHARPEMU_TROPHY_DIR", _previousRoot);
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    private string StorePath => NpTrophyStore.ResolvePath(TitleId, _root);

    private CpuContext Reg(ulong rdi = 0, ulong rsi = 0, ulong rdx = 0, ulong rcx = 0)
    {
        _ctx[CpuRegister.Rdi] = rdi;
        _ctx[CpuRegister.Rsi] = rsi;
        _ctx[CpuRegister.Rdx] = rdx;
        _ctx[CpuRegister.Rcx] = rcx;
        return _ctx;
    }

    [Fact]
    public void Unlock_PersistsAcrossStoreReload()
    {
        Assert.True(NpTrophyStore.Unlock(7, out var stamped));

        // A fresh load (as after an emulator restart) still reports the unlock
        // with its original timestamp.
        NpTrophyStore.Reload();
        Assert.True(NpTrophyStore.IsUnlocked(7, out var reloaded));
        Assert.Equal(stamped, reloaded);
    }

    [Fact]
    public void Unlock_AlreadyUnlockedTrophy_KeepsFirstTimestamp()
    {
        Assert.True(NpTrophyStore.Unlock(3, out var first));
        Assert.False(NpTrophyStore.Unlock(3, out var second));
        Assert.Equal(first, second);

        // The persisted file still carries exactly one entry for the id.
        NpTrophyStore.Reload();
        Assert.Single(NpTrophyStore.GetUnlocked(), entry => entry.Id == 3);
    }

    [Fact]
    public void IsUnlocked_LockedTrophy_ReturnsFalseWithZeroTimestamp()
    {
        Assert.False(NpTrophyStore.IsUnlocked(42, out var timestamp));
        Assert.Equal(0, timestamp);
    }

    [Fact]
    public void GetUnlocked_IsOrderedByTrophyId()
    {
        Assert.True(NpTrophyStore.Unlock(9, out _));
        Assert.True(NpTrophyStore.Unlock(1, out _));
        Assert.True(NpTrophyStore.Unlock(5, out _));

        Assert.Equal([1, 5, 9], NpTrophyStore.GetUnlocked().Select(entry => entry.Id).ToArray());
    }

    [Fact]
    public void Unlock_WritesJsonFileThatRoundTrips()
    {
        Assert.True(NpTrophyStore.Unlock(1, out var first));
        Assert.True(NpTrophyStore.Unlock(12, out var second));

        // The file on disk is real JSON with the unlock list...
        Assert.True(File.Exists(StorePath));
        var text = File.ReadAllText(StorePath);
        Assert.Contains("\"unlocked\"", text);
        Assert.Contains("\"id\": 1", text);
        Assert.Contains("\"id\": 12", text);

        // ...and a fresh load decodes the same ids and timestamps.
        NpTrophyStore.Reload();
        var entries = NpTrophyStore.GetUnlocked().ToDictionary(entry => entry.Id);
        Assert.Equal(first, entries[1].UnlockedAtUnixSeconds);
        Assert.Equal(second, entries[12].UnlockedAtUnixSeconds);
    }

    [Fact]
    public void ResolvePath_UsesOverrideDir_AndSanitizesTitleId()
    {
        Assert.Equal(
            Path.Combine(_root, "NPTROPHYTEST", "trophies.json"),
            NpTrophyStore.ResolvePath(TitleId, _root));
        Assert.Equal(
            Path.Combine(_root, "ABC___", "trophies.json"),
            NpTrophyStore.ResolvePath("abc!@#", _root));
        Assert.Equal(
            Path.Combine(_root, "default", "trophies.json"),
            NpTrophyStore.ResolvePath(null, _root));
    }

    [Fact]
    public void CorruptStoreFile_FallsBackToEmpty_AndIsRewrittenOnNextUnlock()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(StorePath)!);
        File.WriteAllText(StorePath, "{ not valid json");

        NpTrophyStore.Reload();
        Assert.False(NpTrophyStore.IsUnlocked(1, out _));

        // The next unlock rewrites the file with well-formed JSON.
        Assert.True(NpTrophyStore.Unlock(1, out _));
        Assert.Contains("\"id\": 1", File.ReadAllText(StorePath));
    }

    [Fact]
    public void UnlockTrophy_Export_RecordsUnlock_AndClearsPlatinumSlot()
    {
        Assert.Equal(0, NpTrophyExports.NpTrophyCreateContext(Reg(rdi: ContextOut)));
        Assert.Equal(0, NpTrophyExports.NpTrophyCreateHandle(Reg(rdi: HandleOut)));
        Assert.True(_ctx.TryReadInt32(ContextOut, out var context));
        Assert.True(_ctx.TryReadInt32(HandleOut, out var handle));
        Assert.NotEqual(0, context);
        Assert.NotEqual(0, handle);

        var ctx = Reg(rdi: (ulong)(uint)context, rsi: (ulong)(uint)handle, rdx: 4, rcx: PlatinumOut);
        Assert.Equal(0, NpTrophyExports.NpTrophyUnlockTrophy(ctx));

        // No platinum chaining is modeled: the slot reads -1.
        Assert.True(_ctx.TryReadInt32(PlatinumOut, out var platinum));
        Assert.Equal(-1, platinum);
        Assert.True(NpTrophyStore.IsUnlocked(4, out _));
    }

    [Fact]
    public void UnlockTrophy_Export_NegativeTrophyId_ReturnsInvalidArgument()
    {
        var ctx = Reg(rdx: unchecked((ulong)-1), rcx: PlatinumOut);
        Assert.Equal(
            (int)OrbisGen2Result.ORBIS_GEN2_ERROR_INVALID_ARGUMENT,
            NpTrophyExports.NpTrophyUnlockTrophy(ctx));
        Assert.False(NpTrophyStore.IsUnlocked(-1, out _));
    }

    [Fact]
    public void GetTrophyUnlockState_Export_SetsOneBitPerUnlockedTrophy()
    {
        Assert.True(NpTrophyStore.Unlock(0, out _));
        Assert.True(NpTrophyStore.Unlock(5, out _));
        Assert.True(NpTrophyStore.Unlock(33, out _));

        Assert.True(_ctx.TryWriteUInt32(CountSlot, 128));
        _memory.TryWrite(FlagArray, new byte[512]);
        var ctx = Reg(rdx: FlagArray, rcx: CountSlot);
        Assert.Equal(0, NpTrophyExports.NpTrophyGetTrophyUnlockState(ctx));

        Assert.True(_ctx.TryReadUInt32(CountSlot, out var words));
        Assert.Equal(128u, words);
        Assert.True(_ctx.TryReadUInt32(FlagArray, out var word0));
        Assert.True(_ctx.TryReadUInt32(FlagArray + 4, out var word1));
        Assert.Equal((1u << 0) | (1u << 5), word0);
        Assert.Equal(1u << 1, word1); // trophy 33 lives in word 1, bit 1
    }

    [Fact]
    public void GetTrophyUnlockState_Export_RespectsGuestWordCapacity()
    {
        Assert.True(NpTrophyStore.Unlock(40, out _)); // word 1, bit 8 — beyond capacity

        Assert.True(_ctx.TryWriteUInt32(CountSlot, 1));
        _memory.TryWrite(FlagArray, new byte[512]);
        var sentinel = new byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(sentinel, 0xA5A5A5A5);
        Assert.True(_memory.TryWrite(FlagArray + 4, sentinel));

        var ctx = Reg(rdx: FlagArray, rcx: CountSlot);
        Assert.Equal(0, NpTrophyExports.NpTrophyGetTrophyUnlockState(ctx));

        // Only the declared single word is written; the sentinel after it
        // survives and the count reflects the words actually delivered.
        Assert.True(_ctx.TryReadUInt32(CountSlot, out var words));
        Assert.Equal(1u, words);
        Assert.True(_memory.TryRead(FlagArray + 4, sentinel));
        Assert.Equal(0xA5A5A5A5u, BinaryPrimitives.ReadUInt32LittleEndian(sentinel));
    }
}
