// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Buffers.Binary;
using SharpEmu.HLE;
using SharpEmu.HLE.Host;
using SharpEmu.Libs.Audio;
using Xunit;

namespace SharpEmu.Libs.Tests.Audio;

/// <summary>
/// sceAudioOutOpen format-table coverage: every ORBIS_AUDIO_OUT_MODE must open
/// a port with the right channel count and sample size, because BufferByteLength
/// (frames * channels * bytesPerSample) drives both the guest read size and the
/// host submit size. Each theory case parks its guest buffer at the very end of
/// the mapped region, so a geometry error (reading more bytes than the mode
/// allows) turns into a memory fault instead of a silently corrupted mix.
/// </summary>
[Collection(AudioOutStateCollection.Name)]
public sealed class AudioOutFormatTests : IDisposable
{
    private const int InvalidArgument = unchecked((int)0x80020003); // ORBIS_GEN2_ERROR_INVALID_ARGUMENT
    private const int MemoryFault = unchecked((int)0x80020101);     // ORBIS_GEN2_ERROR_MEMORY_FAULT
    private const ulong MemoryBase = 0x1_0000_0000;
    private const int MemorySize = 0x4000;
    private const ulong VolumeArrayAddress = MemoryBase + 0x200;
    private const int Frames = 2;

    private readonly FakeCpuMemory _memory = new(MemoryBase, MemorySize);
    private readonly CpuContext _ctx;
    private readonly List<RecordingAudioStream> _streams = [];

    public AudioOutFormatTests()
    {
        AudioOutExports.ResetForTests();
        AudioOutExports.SetStreamFactoryForTests(_ =>
        {
            var stream = new RecordingAudioStream();
            _streams.Add(stream);
            return stream;
        });
        _ctx = new CpuContext(_memory, Generation.Gen5);
    }

    [Theory]
    [InlineData(0, 1, 2, 16384, 16384)] // S16 mono
    [InlineData(1, 2, 2, 16384, 0)]     // S16 stereo
    [InlineData(2, 6, 2, 16384, 0)]     // S16 5.1
    [InlineData(3, 8, 2, 16384, 0)]     // S16 7.1
    [InlineData(4, 1, 4, 16384, 16384)] // F32 mono
    [InlineData(5, 2, 4, 16384, 0)]     // F32 stereo
    [InlineData(6, 6, 4, 16384, 0)]     // F32 5.1
    [InlineData(7, 8, 4, 16384, 0)]     // F32 7.1
    public void Output_DecodesEveryModeWithExactBufferGeometry(
        int format,
        int channels,
        int bytesPerSample,
        short expectedLeft,
        short expectedRight)
    {
        var handle = OpenPort(format);
        var bufferByteLength = Frames * channels * bytesPerSample;
        // Half-scale signal in channel 0 of frame 0, silence everywhere else.
        var source = new byte[bufferByteLength];
        if (bytesPerSample == 2)
        {
            BinaryPrimitives.WriteInt16LittleEndian(source, 16384);
        }
        else
        {
            BinaryPrimitives.WriteInt32LittleEndian(
                source,
                BitConverter.SingleToInt32Bits(0.5f));
        }

        // Last bytes of the region: a too-large BufferByteLength cannot read
        // past the buffer without faulting, which pins the geometry exactly.
        var sourceAddress = MemoryBase + (ulong)(MemorySize - bufferByteLength);
        Assert.True(_memory.TryWrite(sourceAddress, source));

        Assert.Equal(0, Output(handle, sourceAddress));

        var submission = Assert.Single(Assert.Single(_streams).Submissions);
        Assert.Equal(Frames * AudioPcmConversion.OutputFrameSize, submission.Length);
        // Frame 0 carries the channel-0 signal (mono duplicates it to both
        // sides; every other layout keeps the right channel silent).
        Assert.Equal(expectedLeft, BinaryPrimitives.ReadInt16LittleEndian(submission.AsSpan(0)));
        Assert.Equal(expectedRight, BinaryPrimitives.ReadInt16LittleEndian(submission.AsSpan(2)));
        // Frame 1 is silence end to end.
        Assert.Equal(0, BinaryPrimitives.ReadInt16LittleEndian(submission.AsSpan(4)));
        Assert.Equal(0, BinaryPrimitives.ReadInt16LittleEndian(submission.AsSpan(6)));
    }

    [Theory]
    [InlineData(8)]
    [InlineData(9)]
    [InlineData(255)]
    [InlineData(-1)]
    public void Open_RejectsFormatsOutsideTheModeTable(int format)
    {
        _ctx[CpuRegister.Rdi] = 1;
        _ctx[CpuRegister.Rsi] = 0;
        _ctx[CpuRegister.Rdx] = 0;
        _ctx[CpuRegister.Rcx] = Frames;
        _ctx[CpuRegister.R8] = 48000;
        _ctx[CpuRegister.R9] = unchecked((ulong)format);
        Assert.Equal(InvalidArgument, AudioOutExports.AudioOutOpen(_ctx));
    }

    [Fact]
    public void Output_FivePointOneCenterReachesBothStereoChannels()
    {
        var handle = OpenPort(format: 2); // S16 5.1
        // Center-only dialog at half scale plus full-scale LFE: center folds
        // into both stereo channels at -3 dB, LFE (channel 3) is dropped.
        var source = new byte[Frames * 6 * 2];
        for (var frame = 0; frame < Frames; frame++)
        {
            var offset = frame * 12;
            BinaryPrimitives.WriteInt16LittleEndian(source.AsSpan(offset + 4), 16384); // C
            BinaryPrimitives.WriteInt16LittleEndian(source.AsSpan(offset + 6), short.MaxValue); // LFE
        }

        var sourceAddress = MemoryBase + (ulong)(MemorySize - source.Length);
        Assert.True(_memory.TryWrite(sourceAddress, source));

        Assert.Equal(0, Output(handle, sourceAddress));

        var submission = Assert.Single(Assert.Single(_streams).Submissions);
        for (var frame = 0; frame < Frames; frame++)
        {
            var left = BinaryPrimitives.ReadInt16LittleEndian(submission.AsSpan(frame * 4));
            var right = BinaryPrimitives.ReadInt16LittleEndian(submission.AsSpan((frame * 4) + 2));
            // 0.5 * 0.7071 * 32767 ~= 11585.
            Assert.InRange(left, 11580, 11590);
            Assert.InRange(right, 11580, 11590);
            Assert.Equal(left, right);
        }
    }

    [Fact]
    public void Output_SevenPointOneCenterReachesBothStereoChannels()
    {
        var handle = OpenPort(format: 3); // S16 7.1
        var source = new byte[Frames * 8 * 2];
        for (var frame = 0; frame < Frames; frame++)
        {
            BinaryPrimitives.WriteInt16LittleEndian(source.AsSpan((frame * 16) + 4), 16384); // C
            BinaryPrimitives.WriteInt16LittleEndian(source.AsSpan((frame * 16) + 6), short.MaxValue); // LFE
        }

        var sourceAddress = MemoryBase + (ulong)(MemorySize - source.Length);
        Assert.True(_memory.TryWrite(sourceAddress, source));

        Assert.Equal(0, Output(handle, sourceAddress));

        var submission = Assert.Single(Assert.Single(_streams).Submissions);
        for (var frame = 0; frame < Frames; frame++)
        {
            var left = BinaryPrimitives.ReadInt16LittleEndian(submission.AsSpan(frame * 4));
            var right = BinaryPrimitives.ReadInt16LittleEndian(submission.AsSpan((frame * 4) + 2));
            Assert.InRange(left, 11580, 11590);
            Assert.InRange(right, 11580, 11590);
            Assert.Equal(left, right);
        }
    }

    [Fact]
    public void SetVolume_AppliesIndependentGainsPerChannel()
    {
        var handle = OpenPort(format: 1); // S16 stereo
        // Left muted, right at full: only the right channel may survive.
        Assert.Equal(0, SetVolume(handle, channelFlags: 0b11, leftVolume: 0, rightVolume: 32768));

        var source = new byte[Frames * 2 * 2];
        for (var frame = 0; frame < Frames; frame++)
        {
            BinaryPrimitives.WriteInt16LittleEndian(source.AsSpan(frame * 4), 8192);
            BinaryPrimitives.WriteInt16LittleEndian(source.AsSpan((frame * 4) + 2), -8192);
        }

        Assert.True(_memory.TryWrite(MemoryBase + 0x1000, source));
        Assert.Equal(0, Output(handle, MemoryBase + 0x1000));

        var submission = Assert.Single(Assert.Single(_streams).Submissions);
        for (var frame = 0; frame < Frames; frame++)
        {
            Assert.Equal(0, BinaryPrimitives.ReadInt16LittleEndian(submission.AsSpan(frame * 4)));
            Assert.Equal(-8192, BinaryPrimitives.ReadInt16LittleEndian(submission.AsSpan((frame * 4) + 2)));
        }
    }

    [Fact]
    public void SetVolume_UniformVolumesBehaveLikeTheLegacyScalarVolume()
    {
        var handle = OpenPort(format: 1);
        Assert.Equal(0, SetVolume(handle, channelFlags: 0b11, leftVolume: 16384, rightVolume: 16384));

        var source = new byte[Frames * 2 * 2];
        for (var frame = 0; frame < Frames; frame++)
        {
            BinaryPrimitives.WriteInt16LittleEndian(source.AsSpan(frame * 4), 8192);
            BinaryPrimitives.WriteInt16LittleEndian(source.AsSpan((frame * 4) + 2), -8192);
        }

        Assert.True(_memory.TryWrite(MemoryBase + 0x1000, source));
        Assert.Equal(0, Output(handle, MemoryBase + 0x1000));

        var submission = Assert.Single(Assert.Single(_streams).Submissions);
        for (var frame = 0; frame < Frames; frame++)
        {
            Assert.Equal(4096, BinaryPrimitives.ReadInt16LittleEndian(submission.AsSpan(frame * 4)));
            Assert.Equal(-4096, BinaryPrimitives.ReadInt16LittleEndian(submission.AsSpan((frame * 4) + 2)));
        }
    }

    [Fact]
    public void SetVolume_ChannelMaskSelectsWhichSlotsUpdate()
    {
        var handle = OpenPort(format: 1);
        // Only the right channel is in the mask: the left gain stays at unity
        // and the left volume-array slot must be ignored.
        Assert.Equal(0, SetVolume(handle, channelFlags: 0b10, leftVolume: 0, rightVolume: 16384));

        var source = new byte[Frames * 2 * 2];
        for (var frame = 0; frame < Frames; frame++)
        {
            BinaryPrimitives.WriteInt16LittleEndian(source.AsSpan(frame * 4), 8192);
            BinaryPrimitives.WriteInt16LittleEndian(source.AsSpan((frame * 4) + 2), -8192);
        }

        Assert.True(_memory.TryWrite(MemoryBase + 0x1000, source));
        Assert.Equal(0, Output(handle, MemoryBase + 0x1000));

        var submission = Assert.Single(Assert.Single(_streams).Submissions);
        for (var frame = 0; frame < Frames; frame++)
        {
            Assert.Equal(8192, BinaryPrimitives.ReadInt16LittleEndian(submission.AsSpan(frame * 4)));
            Assert.Equal(-4096, BinaryPrimitives.ReadInt16LittleEndian(submission.AsSpan((frame * 4) + 2)));
        }
    }

    [Fact]
    public void SetVolume_RejectsUnknownHandles()
    {
        Assert.Equal(InvalidArgument, SetVolume(99, channelFlags: 0b11, leftVolume: 0, rightVolume: 0));
    }

    [Fact]
    public void SetVolume_UnreadableVolumeArrayReportsMemoryFault()
    {
        var handle = OpenPort(format: 1);
        _ctx[CpuRegister.Rdi] = unchecked((ulong)handle);
        _ctx[CpuRegister.Rsi] = 0b11;
        _ctx[CpuRegister.Rdx] = MemoryBase + MemorySize; // one past the region
        Assert.Equal(MemoryFault, AudioOutExports.AudioOutSetVolume(_ctx));
    }

    [Fact]
    public void OutputExportRegistersForBothGenerations()
    {
        foreach (var generation in new[] { Generation.Gen4, Generation.Gen5 })
        {
            var manager = new ModuleManager();
            manager.RegisterExports(
                SharpEmu.Generated.SysAbiExportRegistry.CreateExports(generation));

            Assert.True(manager.TryGetExport("QOQtbeDqsT4", out var export));
            Assert.Equal("sceAudioOutOutput", export.Name);
            Assert.Equal("libSceAudioOut", export.LibraryName);
        }
    }

    public void Dispose() => AudioOutExports.ResetForTests();

    private int OpenPort(int format)
    {
        _ctx[CpuRegister.Rdi] = 1;
        _ctx[CpuRegister.Rsi] = 0;
        _ctx[CpuRegister.Rdx] = 0;
        _ctx[CpuRegister.Rcx] = Frames;
        _ctx[CpuRegister.R8] = 48000;
        _ctx[CpuRegister.R9] = unchecked((ulong)format);
        return AudioOutExports.AudioOutOpen(_ctx);
    }

    private int Output(int handle, ulong sourceAddress)
    {
        _ctx[CpuRegister.Rdi] = unchecked((ulong)handle);
        _ctx[CpuRegister.Rsi] = sourceAddress;
        return AudioOutExports.AudioOutOutput(_ctx);
    }

    private int SetVolume(int handle, uint channelFlags, int leftVolume, int rightVolume)
    {
        Span<byte> volumes = stackalloc byte[2 * sizeof(int)];
        BinaryPrimitives.WriteInt32LittleEndian(volumes, leftVolume);
        BinaryPrimitives.WriteInt32LittleEndian(volumes[4..], rightVolume);
        Assert.True(_memory.TryWrite(VolumeArrayAddress, volumes));

        _ctx[CpuRegister.Rdi] = unchecked((ulong)handle);
        _ctx[CpuRegister.Rsi] = channelFlags;
        _ctx[CpuRegister.Rdx] = VolumeArrayAddress;
        return AudioOutExports.AudioOutSetVolume(_ctx);
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

/// <summary>
/// AudioOut2 surround fold-down coverage: a 5.1 port's Center channel (dialog)
/// and LFE must not vanish from the stereo master mix — Center feeds both
/// stereo channels at -3 dB, LFE is dropped, surrounds land at -3 dB.
/// </summary>
[Collection(AudioOut2StateCollection.Name)]
public sealed class AudioOut2SurroundDownmixTests : IDisposable
{
    private const ulong MemoryBase = 0x1_0000_0000;
    private const int MemorySize = 0x4000;
    private const ulong ContextParamAddress = MemoryBase + 0x100;
    private const ulong ContextHandleAddress = MemoryBase + 0x200;
    private const ulong PortParamAddress = MemoryBase + 0x300;
    private const ulong PortHandleAddress = MemoryBase + 0x380;
    private const ulong AttributesAddress = MemoryBase + 0x400;
    private const ulong PcmValueAddress = MemoryBase + 0x480;
    private const ulong PcmAddress = MemoryBase + 0x1000;

    private const uint GrainSamples = 256;

    private readonly FakeCpuMemory _memory = new(MemoryBase, MemorySize);
    private readonly CpuContext _ctx;
    private readonly List<RecordingAudioStream> _streams = [];

    public AudioOut2SurroundDownmixTests()
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
    public void ContextPush_FivePointOneCenterFoldsIntoBothStereoChannels()
    {
        var handle = CreatePortWithContext(channels: 6);
        WriteCenterOnlyPcm(channels: 6, center: 0.5f, lfe: 1.0f);
        AttachPcm(handle);
        Assert.Equal(0, Push());

        var submission = Assert.Single(Assert.Single(_streams).Submissions);
        Assert.Equal((int)GrainSamples * 4, submission.Length);
        for (var frame = 0; frame < (int)GrainSamples; frame++)
        {
            var left = BinaryPrimitives.ReadInt16LittleEndian(submission.AsSpan(frame * 4));
            var right = BinaryPrimitives.ReadInt16LittleEndian(submission.AsSpan((frame * 4) + 2));
            // Center 0.5 * 0.7071 * 32767 ~= 11585 on both sides; the
            // full-scale LFE channel must not leak into the mix.
            Assert.InRange(left, 11580, 11590);
            Assert.InRange(right, 11580, 11590);
            Assert.Equal(left, right);
        }
    }

    [Fact]
    public void ContextPush_SevenPointOneStillFoldsCenterIntoBothChannels()
    {
        var handle = CreatePortWithContext(channels: 8);
        WriteCenterOnlyPcm(channels: 8, center: 0.5f, lfe: 1.0f);
        AttachPcm(handle);
        Assert.Equal(0, Push());

        var submission = Assert.Single(Assert.Single(_streams).Submissions);
        for (var frame = 0; frame < (int)GrainSamples; frame++)
        {
            var left = BinaryPrimitives.ReadInt16LittleEndian(submission.AsSpan(frame * 4));
            var right = BinaryPrimitives.ReadInt16LittleEndian(submission.AsSpan((frame * 4) + 2));
            Assert.InRange(left, 11580, 11590);
            Assert.InRange(right, 11580, 11590);
            Assert.Equal(left, right);
        }
    }

    public void Dispose() => AudioOut2Exports.ResetForTests();

    private ulong CreateContext()
    {
        Span<byte> param = stackalloc byte[0x40];
        param.Clear();
        BinaryPrimitives.WriteUInt32LittleEndian(param[0x0C..], 4);           // queue_depth
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

    private ulong CreatePortWithContext(uint channels)
    {
        var contextHandle = CreateContext();

        // SceAudioOut2PortParam: portType u16, dataFormat u32 (channels << 8 |
        // 0 for float), samplingFrequency u32.
        Span<byte> param = stackalloc byte[0x40];
        param.Clear();
        BinaryPrimitives.WriteUInt32LittleEndian(param[0x04..], channels << 8);
        BinaryPrimitives.WriteUInt32LittleEndian(param[0x08..], 48000);
        Assert.True(_memory.TryWrite(PortParamAddress, param));

        _ctx[CpuRegister.Rdi] = contextHandle;
        _ctx[CpuRegister.Rsi] = PortParamAddress;
        _ctx[CpuRegister.Rdx] = PortHandleAddress;
        Assert.Equal(0, AudioOut2Exports.AudioOut2PortCreate(_ctx));

        Span<byte> portHandle = stackalloc byte[sizeof(ulong)];
        Assert.True(_memory.TryRead(PortHandleAddress, portHandle));
        return BinaryPrimitives.ReadUInt64LittleEndian(portHandle);
    }

    private void WriteCenterOnlyPcm(uint channels, float center, float lfe)
    {
        var pcm = new byte[(int)GrainSamples * (int)channels * sizeof(float)];
        for (var frame = 0; frame < (int)GrainSamples; frame++)
        {
            var offset = frame * (int)channels * sizeof(float);
            BinaryPrimitives.WriteInt32LittleEndian(
                pcm.AsSpan(offset + (2 * sizeof(float))),
                BitConverter.SingleToInt32Bits(center));
            BinaryPrimitives.WriteInt32LittleEndian(
                pcm.AsSpan(offset + (3 * sizeof(float))),
                BitConverter.SingleToInt32Bits(lfe));
        }

        Assert.True(_memory.TryWrite(PcmAddress, pcm));
    }

    private void AttachPcm(ulong portHandle)
    {
        // Attribute entry {id u32, value u64, size u64}; the PCM value points
        // at { const void* data }.
        Span<byte> entry = stackalloc byte[0x18];
        entry.Clear();
        BinaryPrimitives.WriteUInt32LittleEndian(entry, 0);
        BinaryPrimitives.WriteUInt64LittleEndian(entry[0x08..], PcmValueAddress);
        BinaryPrimitives.WriteUInt64LittleEndian(entry[0x10..], 8);
        Assert.True(_memory.TryWrite(AttributesAddress, entry));

        Span<byte> pcmPointer = stackalloc byte[sizeof(ulong)];
        BinaryPrimitives.WriteUInt64LittleEndian(pcmPointer, PcmAddress);
        Assert.True(_memory.TryWrite(PcmValueAddress, pcmPointer));

        _ctx[CpuRegister.Rdi] = portHandle;
        _ctx[CpuRegister.Rsi] = AttributesAddress;
        _ctx[CpuRegister.Rdx] = 1;
        Assert.Equal(0, AudioOut2Exports.AudioOut2PortSetAttributes(_ctx));
    }

    private int Push()
    {
        Span<byte> contextHandle = stackalloc byte[sizeof(ulong)];
        Assert.True(_memory.TryRead(ContextHandleAddress, contextHandle));
        _ctx[CpuRegister.Rdi] = BinaryPrimitives.ReadUInt64LittleEndian(contextHandle);
        _ctx[CpuRegister.Rsi] = 1; // blocking
        return AudioOut2Exports.AudioOut2ContextPush(_ctx);
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
