// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Buffers.Binary;
using System.Text;
using SharpEmu.HLE;
using SharpEmu.Libs.Kernel;
using SharpEmu.Libs.SaveData;
using Xunit;

namespace SharpEmu.Libs.Tests.SaveData;

// Exercises the 16-slot /savedata0-15 mount table end to end: slot
// allocation, per-slot isolation, kernel guest-path resolution, umount cycles
// and exhaustion. Shares the environment-pinning collection so it never runs
// alongside other tests that mutate SHARPEMU_SAVEDATA_DIR or the mount table.
[Collection("SaveDataMemoryState")]
public sealed class SaveDataMountTableTests : IDisposable
{
    private const ulong Base = 0x2_0000_0000;
    private const int UserId = 0x1001;
    private const string TitleId = "SDMOUNTTEST";
    private const int Busy = unchecked((int)0x809F0006);
    private const uint MountModeCreate = 1u << 2;

    private const ulong MountParam = Base + 0x100;
    private const ulong MountResult = Base + 0x200;
    private const ulong DirNamePtr = Base + 0x300;
    private const ulong MountPointStr = Base + 0x340;
    private const ulong MountInfoOut = Base + 0x400;
    private const ulong CountOut = Base + 0x480;
    private const ulong TransferParam = Base + 0x500;
    private const ulong TitleIdPtr = Base + 0x540;

    private readonly FakeCpuMemory _memory = new(Base, 0x4000);
    private readonly CpuContext _ctx;
    private readonly string _root;
    private readonly string? _previousRoot;

    public SaveDataMountTableTests()
    {
        _ctx = new CpuContext(_memory, Generation.Gen5);
        _root = Path.Combine(Path.GetTempPath(), $"sharpemu-sdmount-{Guid.NewGuid():N}");
        _previousRoot = Environment.GetEnvironmentVariable("SHARPEMU_SAVEDATA_DIR");
        Environment.SetEnvironmentVariable("SHARPEMU_SAVEDATA_DIR", _root);
        SaveDataExports.ConfigureApplicationInfo(TitleId);
    }

    public void Dispose()
    {
        SaveDataExports.ConfigureApplicationInfo(null);
        Environment.SetEnvironmentVariable("SHARPEMU_SAVEDATA_DIR", _previousRoot);
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    private string SlotDir(string dirName) => Path.Combine(_root, TitleId, dirName);

    private void WriteAscii(ulong address, string value)
    {
        var bytes = new byte[value.Length + 1];
        Encoding.ASCII.GetBytes(value).CopyTo(bytes, 0);
        Assert.True(_memory.TryWrite(address, bytes));
    }

    private CpuContext Reg(ulong rdi = 0, ulong rsi = 0, ulong rdx = 0, ulong rcx = 0)
    {
        _ctx[CpuRegister.Rdi] = rdi;
        _ctx[CpuRegister.Rsi] = rsi;
        _ctx[CpuRegister.Rdx] = rdx;
        _ctx[CpuRegister.Rcx] = rcx;
        return _ctx;
    }

    // Mounts one dir and returns (result, mount point reported in the result).
    private (int Result, string MountPoint) Mount(string dirName, uint mountMode = MountModeCreate)
    {
        WriteAscii(DirNamePtr, dirName);
        Span<byte> param = stackalloc byte[0x30];
        param.Clear();
        BinaryPrimitives.WriteInt32LittleEndian(param, UserId);
        BinaryPrimitives.WriteUInt64LittleEndian(param[0x08..], DirNamePtr);
        BinaryPrimitives.WriteUInt32LittleEndian(param[0x20..], mountMode);
        Assert.True(_memory.TryWrite(MountParam, param));

        _memory.TryWrite(MountResult, new byte[0x40]);
        var result = SaveDataExports.SaveDataMount3(Reg(rdi: MountParam, rsi: MountResult));

        var raw = new byte[16];
        Assert.True(_memory.TryRead(MountResult, raw));
        var terminator = Array.IndexOf(raw, (byte)0);
        var mountPoint = Encoding.ASCII.GetString(raw, 0, terminator < 0 ? 16 : terminator);
        return (result, mountPoint);
    }

    private int Umount(string mountPoint)
    {
        WriteAscii(MountPointStr, mountPoint);
        return SaveDataExports.SaveDataUmount2(Reg(rdi: MountPointStr));
    }

    private static string DirNameForSlot(int slot) => $"SAVE{slot:D4}";

    [Fact]
    public void Mount_SequentialDirs_TakeSequentialSlots()
    {
        var (result0, point0) = Mount(DirNameForSlot(0));
        Assert.Equal(0, result0);
        Assert.Equal("/savedata0", point0);
        var (result1, point1) = Mount(DirNameForSlot(1));
        Assert.Equal(0, result1);
        Assert.Equal("/savedata1", point1);
        var (result2, point2) = Mount(DirNameForSlot(2));
        Assert.Equal(0, result2);
        Assert.Equal("/savedata2", point2);
        Assert.True(Directory.Exists(SlotDir(DirNameForSlot(0))));
        Assert.True(Directory.Exists(SlotDir(DirNameForSlot(1))));
        Assert.True(Directory.Exists(SlotDir(DirNameForSlot(2))));
    }

    [Fact]
    public void Mount_AllSixteenSlots_ResolveThroughKernelGuestPathTable()
    {
        for (var slot = 0; slot < 16; slot++)
        {
            var (result, mountPoint) = Mount(DirNameForSlot(slot));
            Assert.Equal(0, result);
            Assert.Equal($"/savedata{slot}", mountPoint);

            // The kernel guest-path mount table (not savedata code) resolves
            // the slot to its own host directory.
            var resolved = KernelMemoryCompatExports.ResolveGuestPath(mountPoint);
            Assert.Equal(Path.GetFullPath(SlotDir(DirNameForSlot(slot))), resolved);
        }
    }

    [Fact]
    public void Mount_ExhaustingAllSixteenSlots_ReturnsBusy()
    {
        for (var slot = 0; slot < 16; slot++)
        {
            Assert.Equal(0, Mount(DirNameForSlot(slot)).Result);
        }

        var (result, _) = Mount("SAVETOOMANY");
        Assert.Equal(Busy, result);
    }

    [Fact]
    public void Umount_FreesSlotForReuse()
    {
        Assert.Equal(0, Mount(DirNameForSlot(0)).Result);
        Assert.Equal(0, Mount(DirNameForSlot(1)).Result);
        Assert.Equal(0, Umount("/savedata0"));

        // The lowest freed slot is handed out again.
        var (result, mountPoint) = Mount("SAVEREUSED");
        Assert.Equal(0, result);
        Assert.Equal("/savedata0", mountPoint);
    }

    [Fact]
    public void Mount_SameDirWhileMounted_ReusesSameSlotWithoutLeaking()
    {
        var (created, point) = Mount(DirNameForSlot(0));
        Assert.Equal(0, created);
        Assert.Equal("/savedata0", point);

        // Remounting the live dir is idempotent: the same mount point is
        // reported and no second slot is consumed.
        var (again, samePoint) = Mount(DirNameForSlot(0), mountMode: 0);
        Assert.Equal(0, again);
        Assert.Equal(point, samePoint);

        Assert.Equal(0, SaveDataExports.SaveDataGetMountedSaveDataCount(Reg(rsi: CountOut)));
        Assert.True(_ctx.TryReadUInt32(CountOut, out var mounted));
        Assert.Equal(1u, mounted);
    }

    [Fact]
    public void Mount_SlotIsolation_WritesThroughOneSlotDoNotReachAnother()
    {
        Assert.Equal(0, Mount(DirNameForSlot(0)).Result);
        Assert.Equal(0, Mount(DirNameForSlot(1)).Result);

        var throughSlot0 = KernelMemoryCompatExports.ResolveGuestPath("/savedata0/save.bin");
        var throughSlot1 = KernelMemoryCompatExports.ResolveGuestPath("/savedata1/save.bin");
        Assert.NotEqual(throughSlot0, throughSlot1);

        Directory.CreateDirectory(Path.GetDirectoryName(throughSlot0)!);
        File.WriteAllText(throughSlot0, "slot0");
        Assert.True(File.Exists(Path.Combine(SlotDir(DirNameForSlot(0)), "save.bin")));
        Assert.False(File.Exists(throughSlot1));
    }

    [Fact]
    public void Umount_ClearsKernelTableEntry_PathResolvesToNothing()
    {
        Assert.Equal(0, Mount(DirNameForSlot(0)).Result);
        Assert.NotEqual(string.Empty, KernelMemoryCompatExports.ResolveGuestPath("/savedata0"));

        Assert.Equal(0, Umount("/savedata0"));

        // Default-deny: the unmounted slot no longer resolves through the
        // kernel table, so guest file access to it fails closed.
        Assert.Equal(string.Empty, KernelMemoryCompatExports.ResolveGuestPath("/savedata0/save.bin"));
        Assert.Equal(0, SaveDataExports.SaveDataIsMounted(Reg(rsi: CountOut)));
        Assert.True(_ctx.TryReadUInt32(CountOut, out var mounted));
        Assert.Equal(0u, mounted);
    }

    [Fact]
    public void GetMountedSaveDataCount_TracksEveryLiveSlot()
    {
        Assert.Equal(0, Mount(DirNameForSlot(0)).Result);
        Assert.Equal(0, Mount(DirNameForSlot(1)).Result);
        Assert.Equal(0, Mount(DirNameForSlot(2)).Result);

        Assert.Equal(0, SaveDataExports.SaveDataGetMountedSaveDataCount(Reg(rsi: CountOut)));
        Assert.True(_ctx.TryReadUInt32(CountOut, out var three));
        Assert.Equal(3u, three);

        Assert.Equal(0, Umount("/savedata1"));
        Assert.Equal(0, SaveDataExports.SaveDataGetMountedSaveDataCount(Reg(rsi: CountOut)));
        Assert.True(_ctx.TryReadUInt32(CountOut, out var two));
        Assert.Equal(2u, two);
    }

    [Fact]
    public void GetMountInfo_ReportsEachSlotsOwnDirectory()
    {
        Assert.Equal(0, Mount(DirNameForSlot(0)).Result);
        Assert.Equal(0, Mount(DirNameForSlot(1)).Result);

        // Slot 0 carries data; slot 1 stays empty. The per-slot used-block
        // count (freeBlocks slot reused as used) must differ, proving each
        // mount point reports its own directory.
        Directory.CreateDirectory(Path.Combine(SlotDir(DirNameForSlot(0)), "sce_sys"));
        File.WriteAllText(
            Path.Combine(SlotDir(DirNameForSlot(0)), "sce_sys", "pad.bin"),
            new string('x', 40960));

        WriteAscii(MountPointStr, "/savedata0");
        Assert.Equal(0, SaveDataExports.SaveDataGetMountInfo(Reg(rdi: MountPointStr, rsi: MountInfoOut)));
        Assert.True(_ctx.TryReadUInt64(MountInfoOut + 0x08, out var usedSlot0));

        WriteAscii(MountPointStr, "/savedata1");
        Assert.Equal(0, SaveDataExports.SaveDataGetMountInfo(Reg(rdi: MountPointStr, rsi: MountInfoOut)));
        Assert.True(_ctx.TryReadUInt64(MountInfoOut + 0x08, out var usedSlot1));

        Assert.True(usedSlot0 > usedSlot1);
        Assert.Equal(0u, (uint)usedSlot1);
    }

    [Fact]
    public void ConfigureApplicationInfo_UnregistersEveryKernelMount()
    {
        Assert.Equal(0, Mount(DirNameForSlot(0)).Result);
        Assert.Equal(0, Mount(DirNameForSlot(1)).Result);

        SaveDataExports.ConfigureApplicationInfo("OTHERTITLE");

        Assert.Equal(string.Empty, KernelMemoryCompatExports.ResolveGuestPath("/savedata0"));
        Assert.Equal(string.Empty, KernelMemoryCompatExports.ResolveGuestPath("/savedata1"));
        Assert.Equal(0, SaveDataExports.SaveDataIsMounted(Reg(rsi: CountOut)));
        Assert.True(_ctx.TryReadUInt32(CountOut, out var mounted));
        Assert.Equal(0u, mounted);
    }

    [Fact]
    public void TransferringMount_TakesLowestFreeSlot()
    {
        // Transferring mounts are read-only and carry their own title id (a
        // 10-byte field), so the slot dir must exist under that title first.
        var transferringTitle = TitleId[..10];
        var prepared = Path.Combine(_root, transferringTitle, DirNameForSlot(0));
        Directory.CreateDirectory(prepared);

        WriteAscii(TitleIdPtr, transferringTitle);
        WriteAscii(DirNamePtr, DirNameForSlot(0));
        Span<byte> param = stackalloc byte[0x20];
        param.Clear();
        BinaryPrimitives.WriteInt32LittleEndian(param, UserId);
        BinaryPrimitives.WriteUInt64LittleEndian(param[0x08..], TitleIdPtr);
        BinaryPrimitives.WriteUInt64LittleEndian(param[0x10..], DirNamePtr);
        Assert.True(_memory.TryWrite(TransferParam, param));

        _memory.TryWrite(MountResult, new byte[0x40]);
        Assert.Equal(0, SaveDataExports.SaveDataTransferringMount(Reg(rdi: TransferParam, rsi: MountResult)));

        var raw = new byte[16];
        Assert.True(_memory.TryRead(MountResult, raw));
        var terminator = Array.IndexOf(raw, (byte)0);
        Assert.Equal("/savedata0", Encoding.ASCII.GetString(raw, 0, terminator));
        Assert.Equal(Path.GetFullPath(prepared), KernelMemoryCompatExports.ResolveGuestPath("/savedata0"));
    }

    [Fact]
    public void MountUmountCycles_AcrossWholeTable_SlotsStayUsable()
    {
        for (var round = 0; round < 2; round++)
        {
            for (var slot = 0; slot < 16; slot++)
            {
                var (result, mountPoint) = Mount(DirNameForSlot(slot), mountMode: round == 0 ? MountModeCreate : 0);
                Assert.Equal(0, result);
                Assert.Equal($"/savedata{slot}", mountPoint);
            }

            for (var slot = 0; slot < 16; slot++)
            {
                Assert.Equal(0, Umount($"/savedata{slot}"));
            }

            Assert.Equal(0, SaveDataExports.SaveDataGetMountedSaveDataCount(Reg(rsi: CountOut)));
            Assert.True(_ctx.TryReadUInt32(CountOut, out var mounted));
            Assert.Equal(0u, mounted);
        }
    }

    [Fact]
    public void Umount_UnknownMountPoint_RemainsLenientSuccess()
    {
        Assert.Equal(0, Mount(DirNameForSlot(0)).Result);
        Assert.Equal(0, Umount("/savedata9"));

        // The live mount is untouched by the stray umount.
        Assert.Equal(0, SaveDataExports.SaveDataGetMountedSaveDataCount(Reg(rsi: CountOut)));
        Assert.True(_ctx.TryReadUInt32(CountOut, out var mounted));
        Assert.Equal(1u, mounted);
    }
}
