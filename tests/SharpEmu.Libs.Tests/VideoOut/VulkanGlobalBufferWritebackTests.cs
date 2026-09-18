// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Collections;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using SharpEmu.HLE;
using SharpEmu.Libs.VideoOut;
using Xunit;

namespace SharpEmu.Libs.Tests.VideoOut;

[CollectionDefinition("Vulkan guest memory writeback", DisableParallelization = true)]
public sealed class VulkanGuestMemoryWritebackCollection;

[Collection("Vulkan guest memory writeback")]
public sealed class VulkanGlobalBufferWritebackTests
{
    [Fact]
    public void DenseGpuChangesPreserveCpuAllocatorHeaderInTheSamePage()
    {
        using var fixture = new WritebackFixture();
        // Eight of 32 coarse blocks changed; the allocator header did not.
        fixture.GpuBytes.AsSpan(0, 1024).Fill(0x51);
        fixture.Memory.Bytes.AsSpan(2048, 16).Fill(0xA7);

        fixture.WriteBack();

        Assert.All(fixture.Memory.Bytes[..1024], value => Assert.Equal(0x51, value));
        Assert.All(fixture.Memory.Bytes[2048..2064], value => Assert.Equal(0xA7, value));
    }

    [Fact]
    public void FragmentedGpuChangesPreserveCpuBytesBetweenMoreThan64Runs()
    {
        using var fixture = new WritebackFixture();
        for (var offset = 0; offset < 260; offset += 4)
        {
            fixture.GpuBytes[offset] = 0x52;
            fixture.Memory.Bytes[offset + 1] = 0xB8;
        }

        fixture.WriteBack();

        for (var offset = 0; offset < 260; offset += 4)
        {
            Assert.Equal(0x52, fixture.Memory.Bytes[offset]);
            Assert.Equal(0xB8, fixture.Memory.Bytes[offset + 1]);
        }
    }

    [Fact]
    public void CpuWriteImmediatelyBeforePublicationSurvives()
    {
        using var fixture = new WritebackFixture();
        fixture.GpuBytes[32] = 0x53;
        // Models a native guest store after any managed read/merge and just
        // before the memory copy. Publishing the whole page loses this store.
        fixture.Memory.BeforeWrite = () => fixture.Memory.Bytes[2048] = 0xC9;

        fixture.WriteBack();

        Assert.Equal(0x53, fixture.Memory.Bytes[32]);
        Assert.Equal(0xC9, fixture.Memory.Bytes[2048]);
    }

    [Fact]
    public void FailedPublicationRetriesWithoutRepublishingSuccessfulBytes()
    {
        using var fixture = new WritebackFixture();
        fixture.GpuBytes[16] = 0x54;
        fixture.Memory.FailNextWrite = true;

        fixture.WriteBack();
        Assert.Equal(0, fixture.Memory.Bytes[16]);
        fixture.WriteBack();
        Assert.Equal(0x54, fixture.Memory.Bytes[16]);

        fixture.Memory.Bytes[16] = 0xDA;
        fixture.WriteBack();
        Assert.Equal(0xDA, fixture.Memory.Bytes[16]);
    }

    // Run the real writeback method without constructing a Vulkan device.
    // Only its guest-memory boundary and already-completed mapped allocation
    // are supplied; no writeback logic is duplicated in this fixture.
    private sealed class WritebackFixture : IDisposable
    {
        private const ulong GuestBase = 0x6_35EA_0000;
        private const int PageSize = 4096;
        private static readonly Type PresenterType = typeof(VulkanVideoPresenter)
            .GetNestedType("Presenter", BindingFlags.NonPublic)!;
        private static readonly FieldInfo GuestMemoryField = typeof(VulkanVideoPresenter)
            .GetField("_guestMemory", BindingFlags.NonPublic | BindingFlags.Static)!;
        private readonly object _presenter = RuntimeHelpers.GetUninitializedObject(PresenterType);
        private readonly nint _mapped = Marshal.AllocHGlobal(PageSize);
        private readonly object? _previousMemory;

        public byte[] GpuBytes { get; } = new byte[PageSize];
        public WritebackMemory Memory { get; } = new(GuestBase, PageSize);

        public WritebackFixture()
        {
            _previousMemory = GuestMemoryField.GetValue(null);
            GuestMemoryField.SetValue(null, Memory);
            SetField(_presenter, "_completedTimeline", 1UL);
            SetField(_presenter, "_tracedGlobalWritebacks", new HashSet<(ulong, ulong)>());

            var allocationType = PresenterType.GetNestedType("GuestBufferAllocation", BindingFlags.NonPublic)!;
            var allocation = Activator.CreateInstance(allocationType, nonPublic: true)!;
            SetField(allocation, "BaseAddress", GuestBase);
            SetField(allocation, "Size", (ulong)PageSize);
            SetField(allocation, "Mapped", _mapped);
            SetField(allocation, "Shadow", new byte[PageSize]);
            var ranges = (IList)allocationType.GetProperty("DirtyRanges")!.GetValue(allocation)!;
            var rangeType = PresenterType.GetNestedType("DirtyGuestBufferRange", BindingFlags.NonPublic)!;
            ranges.Add(Activator.CreateInstance(rangeType, 0UL, (ulong)PageSize, "test", 1UL)!);

            var allocations = (IList)Activator.CreateInstance(typeof(List<>).MakeGenericType(allocationType))!;
            allocations.Add(allocation);
            SetField(_presenter, "_guestBufferAllocations", allocations);
        }

        public void WriteBack()
        {
            Marshal.Copy(GpuBytes, 0, _mapped, PageSize);
            PresenterType.GetMethod("WriteBackAllDirtyGuestBuffers", BindingFlags.NonPublic | BindingFlags.Instance)!
                .Invoke(_presenter, [null]);
        }

        public void Dispose()
        {
            GuestMemoryField.SetValue(null, _previousMemory);
            Marshal.FreeHGlobal(_mapped);
        }

        private static void SetField(object instance, string name, object value) =>
            instance.GetType().GetField(name, BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Instance)!
                .SetValue(instance, value);
    }

    private sealed class WritebackMemory(ulong baseAddress, int length) : ICpuMemory
    {
        public byte[] Bytes { get; } = new byte[length];
        public Action? BeforeWrite { get; set; }
        public bool FailNextWrite { get; set; }

        public bool TryRead(ulong virtualAddress, Span<byte> destination)
        {
            if (virtualAddress < baseAddress || virtualAddress - baseAddress + (ulong)destination.Length > (ulong)Bytes.Length)
            {
                return false;
            }

            Bytes.AsSpan((int)(virtualAddress - baseAddress), destination.Length).CopyTo(destination);
            return true;
        }

        public bool TryWrite(ulong virtualAddress, ReadOnlySpan<byte> source)
        {
            if (virtualAddress < baseAddress || virtualAddress - baseAddress + (ulong)source.Length > (ulong)Bytes.Length)
            {
                return false;
            }

            BeforeWrite?.Invoke();
            if (FailNextWrite)
            {
                FailNextWrite = false;
                return false;
            }

            source.CopyTo(Bytes.AsSpan((int)(virtualAddress - baseAddress), source.Length));
            return true;
        }
    }
}
