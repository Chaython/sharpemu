// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Buffers.Binary;
using SharpEmu.HLE;
using SharpEmu.HLE.Host;
using SharpEmu.Libs.Audio;
using Xunit;

namespace SharpEmu.Libs.Tests.Audio;

[CollectionDefinition("AudioOut2State", DisableParallelization = true)]
public sealed class AudioOut2StateCollection
{
    public const string Name = "AudioOut2State";
}

/// <summary>
/// sceAudioOut2ContextBedWrite coverage: the bed (channel-based, non-object
/// ambient/spatial audio) is interleaved float PCM queued on the context and
/// folded into the stereo master mix at the next ContextPush/Advance — the
/// AudioOut2 model consumes every submission exactly once per grain.
/// </summary>
[Collection(AudioOut2StateCollection.Name)]
public sealed class AudioOut2BedWriteExportsTests : IDisposable
{
    private const int MemoryFault = unchecked((int)0x80020101); // ORBIS_GEN2_ERROR_MEMORY_FAULT
    private const ulong MemoryBase = 0x1_0000_0000;
    private const int MemorySize = 0x4000;
    private const ulong ContextParamAddress = MemoryBase + 0x100;
    private const ulong ContextHandleAddress = MemoryBase + 0x200;
    private const ulong BedAddress = MemoryBase + 0x1000;

    private const uint GrainSamples = 256;
    // One stereo grain of interleaved float PCM.
    private const int StereoBedBytes = (int)(GrainSamples * 2 * sizeof(float));

    private readonly FakeCpuMemory _memory = new(MemoryBase, MemorySize);
    private readonly CpuContext _ctx;
    private readonly List<RecordingAudioStream> _streams = [];

    public AudioOut2BedWriteExportsTests()
    {
        AudioOut2Exports.ResetForTests();
        AudioOut2Exports.SetBackendFactoryForTests(_ =>
        {
            var stream = new RecordingAudioStream();
            _streams.Add(stream);
            return stream;
        });
        _ctx = new CpuContext(_memory, Generation.Gen5);
    }

    [Fact]
    public void BedWrite_NullZeroMisalignedAndOversizedPayloadsAreSuccessNoOps()
    {
        var handle = CreateContext();

        // Every degenerate payload is "write nothing" and clears any stale
        // bed, mirroring how PortSetAttributes treats a null PCM pointer.
        Assert.Equal(0, BedWrite(handle, 0, StereoBedBytes));
        Assert.Equal(0, BedWrite(handle, BedAddress, 0));
        Assert.Equal(0, BedWrite(handle, BedAddress, 3)); // not float-aligned
        Assert.Equal(0, BedWrite(handle, BedAddress, (16 * 1024 * 1024) + 4)); // over the cap
        // Unknown handles stay silent successes like every other AudioOut2 export.
        Assert.Equal(0, BedWrite(handle + 999, BedAddress, StereoBedBytes));

        // Nothing was queued, so a push must not reach the backend at all.
        Assert.Equal(0, Push(handle));
        Assert.Empty(_streams);
    }

    [Fact]
    public void ContextPush_MixesPendingBedIntoStereoOutputExactlyOnce()
    {
        var handle = CreateContext();
        WriteStereoBed(BedAddress, 0.25f, -0.5f);

        Assert.Equal(0, BedWrite(handle, BedAddress, StereoBedBytes));
        Assert.Equal(0, Push(handle));

        // The whole grain of interleaved float bed PCM becomes one stereo
        // PCM16 submission: L = 0.25 -> 8192, R = -0.5 -> -16384.
        var submission = Assert.Single(Assert.Single(_streams).Submissions);
        Assert.Equal(256 * 4, submission.Length);
        for (var frame = 0; frame < 256; frame++)
        {
            Assert.Equal(8192, BinaryPrimitives.ReadInt16LittleEndian(submission.AsSpan(frame * 4)));
            Assert.Equal(-16384, BinaryPrimitives.ReadInt16LittleEndian(submission.AsSpan((frame * 4) + 2)));
        }

        // The bed is consumed exactly once: a second push without a new
        // BedWrite must not submit anything (and must not throw).
        Assert.Equal(0, Push(handle));
        Assert.Single(Assert.Single(_streams).Submissions);
    }

    [Fact]
    public void BedWrite_ZeroLengthAfterValidBedClearsPendingBed()
    {
        var handle = CreateContext();
        WriteStereoBed(BedAddress, 0.25f, -0.5f);

        Assert.Equal(0, BedWrite(handle, BedAddress, StereoBedBytes));
        Assert.Equal(0, BedWrite(handle, 0, 0));

        Assert.Equal(0, Push(handle));
        Assert.Empty(_streams);
    }

    [Fact]
    public void BedWrite_UnreadableGuestPointerReportsMemoryFault()
    {
        var handle = CreateContext();
        WriteStereoBed(BedAddress, 0.25f, -0.5f);

        // A plausible guest pointer that is not mapped must fault rather than
        // queue garbage or silently succeed.
        Assert.Equal(MemoryFault, BedWrite(handle, MemoryBase + 0x100000, StereoBedBytes));

        // The failed write must not leave a pending bed behind.
        Assert.Equal(0, Push(handle));
        Assert.Empty(_streams);
    }

    public void Dispose()
    {
        AudioOut2Exports.ResetForTests();
    }

    private ulong CreateContext()
    {
        Span<byte> param = stackalloc byte[0x40];
        param.Clear();
        BinaryPrimitives.WriteUInt32LittleEndian(param[0x0C..], 4);       // queue_depth
        BinaryPrimitives.WriteUInt32LittleEndian(param[0x10..], GrainSamples); // num_grains
        Assert.True(_memory.TryWrite(ContextParamAddress, param));

        _ctx[CpuRegister.Rdi] = ContextParamAddress;
        _ctx[CpuRegister.Rsi] = MemoryBase + 0x800; // context memory block
        _ctx[CpuRegister.Rdx] = MemorySize;
        _ctx[CpuRegister.Rcx] = ContextHandleAddress;
        Assert.Equal(0, AudioOut2Exports.AudioOut2ContextCreate(_ctx));

        Span<byte> handle = stackalloc byte[sizeof(ulong)];
        Assert.True(_memory.TryRead(ContextHandleAddress, handle));
        return BinaryPrimitives.ReadUInt64LittleEndian(handle);
    }

    private int BedWrite(ulong handle, ulong bedAddress, ulong byteLength)
    {
        _ctx[CpuRegister.Rdi] = handle;
        _ctx[CpuRegister.Rsi] = bedAddress;
        _ctx[CpuRegister.Rdx] = byteLength;
        return AudioOut2Exports.AudioOut2ContextBedWrite(_ctx);
    }

    private int Push(ulong handle)
    {
        _ctx[CpuRegister.Rdi] = handle;
        _ctx[CpuRegister.Rsi] = 1; // blocking
        return AudioOut2Exports.AudioOut2ContextPush(_ctx);
    }

    private void WriteStereoBed(ulong address, float left, float right)
    {
        var bed = new byte[StereoBedBytes];
        for (var frame = 0; frame < GrainSamples; frame++)
        {
            BinaryPrimitives.WriteInt32LittleEndian(
                bed.AsSpan(frame * 2 * sizeof(float)),
                BitConverter.SingleToInt32Bits(left));
            BinaryPrimitives.WriteInt32LittleEndian(
                bed.AsSpan((frame * 2 * sizeof(float)) + sizeof(float)),
                BitConverter.SingleToInt32Bits(right));
        }

        Assert.True(_memory.TryWrite(address, bed));
    }

    private sealed class RecordingAudioStream : IHostAudioStream
    {
        public List<byte[]> Submissions { get; } = [];

        public bool Submit(ReadOnlySpan<byte> stereoPcm16)
        {
            Submissions.Add(stereoPcm16.ToArray());
            return true;
        }

        public void Dispose()
        {
        }
    }
}
