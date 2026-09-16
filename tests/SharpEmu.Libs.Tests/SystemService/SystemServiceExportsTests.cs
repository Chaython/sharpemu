// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Buffers.Binary;
using SharpEmu.HLE;
using SharpEmu.Libs.SystemService;
using Xunit;

namespace SharpEmu.Libs.Tests.SystemService;

public sealed class SystemServiceExportsTests
{
    private const ulong MemoryBase = 0x1_0000_0000;

    public SystemServiceExportsTests()
    {
        SystemServiceExports.ResetForTests();
    }

    [Fact]
    public void GetNoticeScreenSkipFlagWritesOneByteAtMemoryBoundary()
    {
        var memory = new FakeCpuMemory(MemoryBase, 1);
        var context = new CpuContext(memory, Generation.Gen5);
        Assert.True(memory.TryWrite(MemoryBase, new byte[] { 0xA5 }));
        context[CpuRegister.Rdi] = MemoryBase;

        Assert.Equal(0, SystemServiceExports.SystemServiceGetNoticeScreenSkipFlag(context));
        Assert.Equal(0UL, context[CpuRegister.Rax]);

        Span<byte> flag = stackalloc byte[1];
        Assert.True(memory.TryRead(MemoryBase, flag));
        Assert.Equal(0, flag[0]);
    }

    [Fact]
    public void SetNoticeScreenSkipFlagRoundTripsThroughGetter()
    {
        var memory = new FakeCpuMemory(MemoryBase, 2);
        var context = new CpuContext(memory, Generation.Gen5)
        {
            [CpuRegister.Rdi] = 1,
        };

        Assert.Equal(0, SystemServiceExports.SystemServiceSetNoticeScreenSkipFlag(context));

        context[CpuRegister.Rdi] = MemoryBase;
        Assert.Equal(0, SystemServiceExports.SystemServiceGetNoticeScreenSkipFlag(context));

        Span<byte> flag = stackalloc byte[1];
        Assert.True(memory.TryRead(MemoryBase, flag));
        Assert.Equal(1, flag[0]);
    }

    [Fact]
    public void GetAppTypeWritesNeutralZeroAndValidatesTheOutPointer()
    {
        var memory = new FakeCpuMemory(MemoryBase, sizeof(int));
        var context = new CpuContext(memory, Generation.Gen5)
        {
            [CpuRegister.Rdi] = MemoryBase,
        };

        Assert.Equal(0, SystemServiceExports.SystemServiceGetAppType(context));
        Span<byte> appType = stackalloc byte[sizeof(int)];
        Assert.True(memory.TryRead(MemoryBase, appType));
        Assert.Equal(0, BinaryPrimitives.ReadInt32LittleEndian(appType));

        // Null out pointer and an out-of-range write both report errors
        // instead of touching guest memory.
        context[CpuRegister.Rdi] = 0;
        Assert.NotEqual(0, SystemServiceExports.SystemServiceGetAppType(context));
        context[CpuRegister.Rdi] = MemoryBase + sizeof(int);
        Assert.NotEqual(0, SystemServiceExports.SystemServiceGetAppType(context));
    }

    // The new boot-adjacent stubs must register for BOTH generations: the
    // NID is name-derived (generation-independent), and a PS4-layout title
    // importing them resolves against the same registry (pattern of
    // AudioOutFormatTests.OutputExportRegistersForBothGenerations). The
    // screenshot stub lives in GameServiceStubs, which has no test class of
    // its own, so its registration is verified here.
    [Fact]
    public void GetAppTypeAndScreenShotDisableRegisterForBothGenerations()
    {
        foreach (var generation in new[] { Generation.Gen4, Generation.Gen5 })
        {
            var manager = new ModuleManager();
            manager.RegisterExports(
                SharpEmu.Generated.SysAbiExportRegistry.CreateExports(generation));

            Assert.True(manager.TryGetExport("YLbhAXS20C0", out var appType));
            Assert.Equal("sceSystemServiceGetAppType", appType.Name);
            Assert.Equal("libSceSystemService", appType.LibraryName);

            Assert.True(manager.TryGetExport("tIYf0W5VTi8", out var screenShot));
            Assert.Equal("sceScreenShotDisable", screenShot.Name);
            Assert.Equal("libSceScreenShot", screenShot.LibraryName);
        }
    }
}
