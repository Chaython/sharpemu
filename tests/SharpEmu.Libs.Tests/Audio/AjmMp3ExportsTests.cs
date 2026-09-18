// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Buffers.Binary;
using SharpEmu.HLE;
using SharpEmu.Libs.Audio;
using Xunit;

namespace SharpEmu.Libs.Tests.Audio;

/// <summary>
/// sceAjmBatchJobRun / RunSplit MP3 (codec 0) coverage. The flag-driven Run
/// path is the only decode entry point modern titles call, so a missing MP3
/// branch there silences every Run-driven MP3 voice (GTA V Enhanced menu
/// music). The embedded payload is five 128 kbps / 44.1 kHz joint-stereo
/// MPEG-1 Layer III frames of a 1 kHz sine (generated once with ffmpeg),
/// followed by a 4-byte partial frame header so NLayer's stream reader also
/// emits the final frame: 2089 + 4 = 2093 bytes decoding to 5 x 1152 samples.
/// </summary>
[Collection(AjmStateCollection.Name)]
public sealed class AjmMp3ExportsTests : IDisposable
{
    private const ulong MemoryBase = 0x1_0000_0000;
    private const ulong ContextAddress = MemoryBase + 0x100;
    private const ulong InstanceAddress = MemoryBase + 0x200;
    private const ulong BatchInfoAddress = MemoryBase + 0x300;
    private const ulong SidebandAddress = MemoryBase + 0x400;
    private const ulong BatchBufferAddress = MemoryBase + 0x500;
    private const ulong InputAddress = MemoryBase + 0x800;
    private const ulong OutputAddress = MemoryBase + 0x3000;
    private const ulong OutputBAddress = MemoryBase + 0x5000;
    private const ulong OutputCAddress = MemoryBase + 0x6000;
    private const ulong StackAddress = MemoryBase + 0xA000;
    private const ulong InputDescriptorsAddress = MemoryBase + 0xA200;
    private const ulong OutputDescriptorsAddress = MemoryBase + 0xA400;

    private const int Mp3InputLength = 2093;
    private const int Mp3FramePcm16Bytes = 1152 * 2 * 2;
    private const ulong StreamSidebandFlag = 1ul << 47;
    private const ulong FormatSidebandFlag = 1ul << 46;
    private const ulong MultipleFramesFlag = 1ul << 12;

    // Instance flags 0x2_0000_0002: channel bound 2, PCM encoding S16.
    private const ulong Mp3InstanceFlags = 0x2_0000_0002;

    private readonly FakeCpuMemory _memory = new(MemoryBase, 0x10000);
    private readonly CpuContext _ctx;

    public AjmMp3ExportsTests()
    {
        AjmExports.ResetForTests();
        _ctx = new CpuContext(_memory, Generation.Gen5);
    }

    [Fact]
    public void BatchJobRun_DecodesMp3ToNonSilentExpectedSamples()
    {
        var instanceId = SetupMp3Instance();
        WriteMp3Input();
        Paint(OutputAddress, 5 * Mp3FramePcm16Bytes, 0xAB);

        var sideband = RunBatchJob(
            instanceId,
            StreamSidebandFlag | MultipleFramesFlag,
            InputAddress,
            Mp3InputLength,
            OutputAddress,
            5 * Mp3FramePcm16Bytes,
            sidebandSize: 0x20);

        Assert.Equal(0, BinaryPrimitives.ReadInt32LittleEndian(sideband));
        Assert.Equal(2089, BinaryPrimitives.ReadInt32LittleEndian(sideband.AsSpan(8)));
        Assert.Equal(5 * Mp3FramePcm16Bytes, BinaryPrimitives.ReadInt32LittleEndian(sideband.AsSpan(12)));
        Assert.Equal(5ul * 1152, BinaryPrimitives.ReadUInt64LittleEndian(sideband.AsSpan(16)));
        Assert.Equal(5u, BinaryPrimitives.ReadUInt32LittleEndian(sideband.AsSpan(24)));

        var pcm = ReadBytes(OutputAddress, 5 * Mp3FramePcm16Bytes);
        // Frame 1 is the encoder-delay frame; frames 2/3/5 open with the
        // expected decoded sine ramp (values captured from the NLayer decode).
        Assert.Equal(0, BinaryPrimitives.ReadInt16LittleEndian(pcm));
        Assert.Equal(
            new short[] { 1103, 1103, 1453, 1453, 1772, 1772, 2054, 2054 },
            ReadPcm16(pcm, Mp3FramePcm16Bytes));
        Assert.Equal(
            new short[] { 2545, 2545, 2667, 2667, 2734, 2734, 2747, 2747 },
            ReadPcm16(pcm, 2 * Mp3FramePcm16Bytes));
        Assert.Equal(
            new short[] { 1122, 1122, 752, 752, 365, 365, -24, -24 },
            ReadPcm16(pcm, 4 * Mp3FramePcm16Bytes));

        var peak = 0;
        var nonZero = 0;
        for (var i = 0; i + 2 <= pcm.Length; i += 2)
        {
            var sample = BinaryPrimitives.ReadInt16LittleEndian(pcm.AsSpan(i));
            peak = Math.Max(peak, Math.Abs(sample));
            if (sample != 0)
            {
                nonZero++;
            }
        }

        Assert.True(peak > 2500, $"decoded MP3 is silent (peak={peak})");
        Assert.True(nonZero > 9000, $"decoded MP3 is mostly silent ({nonZero}/{pcm.Length / 2})");
    }

    [Fact]
    public void BatchJobRun_Mp3FragmentThenRemainderDecodesAcrossJobs()
    {
        var instanceId = SetupMp3Instance();
        WriteMp3Input();
        Paint(OutputAddress, 5 * Mp3FramePcm16Bytes, 0xAB);

        // A 300-byte prefix cannot complete even the first frame, so the job
        // must still succeed, clear the output, and report the whole input
        // consumed so the guest advances instead of spinning.
        var first = RunBatchJob(
            instanceId,
            StreamSidebandFlag | MultipleFramesFlag,
            InputAddress,
            300,
            OutputAddress,
            5 * Mp3FramePcm16Bytes,
            sidebandSize: 0x20);
        Assert.Equal(0, BinaryPrimitives.ReadInt32LittleEndian(first));
        Assert.Equal(300, BinaryPrimitives.ReadInt32LittleEndian(first.AsSpan(8)));
        Assert.Equal(0, BinaryPrimitives.ReadInt32LittleEndian(first.AsSpan(12)));
        Assert.Equal(1u, BinaryPrimitives.ReadUInt32LittleEndian(first.AsSpan(24)));
        Assert.All(ReadBytes(OutputAddress, 5 * Mp3FramePcm16Bytes), value => Assert.Equal(0, value));

        // The guest then submits the remainder; the decoder picks the stream
        // back up and produces three more frames of non-silent PCM (the 4-byte
        // partial-header suffix stays pending, so 1789 of the 1793 input
        // bytes are reported consumed).
        var second = RunBatchJob(
            instanceId,
            StreamSidebandFlag | MultipleFramesFlag,
            InputAddress + 300,
            (ulong)(Mp3Sine.Length - 300),
            OutputAddress,
            5 * Mp3FramePcm16Bytes,
            sidebandSize: 0x20);
        Assert.Equal(0, BinaryPrimitives.ReadInt32LittleEndian(second));
        Assert.Equal(1789, BinaryPrimitives.ReadInt32LittleEndian(second.AsSpan(8)));
        Assert.Equal(3 * Mp3FramePcm16Bytes, BinaryPrimitives.ReadInt32LittleEndian(second.AsSpan(12)));
        Assert.Equal(3u, BinaryPrimitives.ReadUInt32LittleEndian(second.AsSpan(24)));

        var pcm = ReadBytes(OutputAddress, 3 * Mp3FramePcm16Bytes);
        var peak = 0;
        for (var i = 0; i + 2 <= pcm.Length; i += 2)
        {
            peak = Math.Max(peak, Math.Abs(BinaryPrimitives.ReadInt16LittleEndian(pcm.AsSpan(i))));
        }

        Assert.True(peak > 1000, $"resumed MP3 decode is silent (peak={peak})");
    }

    [Fact]
    public void BatchJobRunSplit_ScattersMp3PcmAcrossOutputDescriptors()
    {
        var instanceId = SetupMp3Instance();
        WriteMp3Input();
        Paint(OutputAddress, Mp3FramePcm16Bytes, 0xAB);
        Paint(OutputBAddress, 4096, 0xAB);
        Paint(OutputCAddress, Mp3FramePcm16Bytes, 0xAB);

        // Input arrives as two descriptors over the single logical stream.
        WriteUInt64(InputDescriptorsAddress, InputAddress);
        WriteUInt64(InputDescriptorsAddress + 8, 500);
        WriteUInt64(InputDescriptorsAddress + 16, InputAddress + 500);
        WriteUInt64(InputDescriptorsAddress + 24, Mp3InputLength - 500);

        // Output is scattered across three descriptors: exactly one frame,
        // then 4096 bytes, then one frame of which only 512 bytes are filled.
        WriteUInt64(OutputDescriptorsAddress, OutputAddress);
        WriteUInt64(OutputDescriptorsAddress + 8, Mp3FramePcm16Bytes);
        WriteUInt64(OutputDescriptorsAddress + 16, OutputBAddress);
        WriteUInt64(OutputDescriptorsAddress + 24, 4096);
        WriteUInt64(OutputDescriptorsAddress + 32, OutputCAddress);
        WriteUInt64(OutputDescriptorsAddress + 40, Mp3FramePcm16Bytes);

        var sideband = RunBatchJobSplit(
            instanceId,
            StreamSidebandFlag | MultipleFramesFlag,
            InputDescriptorsAddress,
            inputCount: 2,
            OutputDescriptorsAddress,
            outputCount: 3,
            sidebandSize: 0x20);

        Assert.Equal(0, BinaryPrimitives.ReadInt32LittleEndian(sideband));
        Assert.Equal(835, BinaryPrimitives.ReadInt32LittleEndian(sideband.AsSpan(8)));
        Assert.Equal(2 * Mp3FramePcm16Bytes, BinaryPrimitives.ReadInt32LittleEndian(sideband.AsSpan(12)));
        Assert.Equal(2ul * 1152, BinaryPrimitives.ReadUInt64LittleEndian(sideband.AsSpan(16)));
        Assert.Equal(2u, BinaryPrimitives.ReadUInt32LittleEndian(sideband.AsSpan(24)));

        // Descriptor 0 is exactly frame 1 (the encoder-delay frame).
        var first = ReadBytes(OutputAddress, Mp3FramePcm16Bytes);
        Assert.Equal(0, BinaryPrimitives.ReadInt16LittleEndian(first));
        // Descriptor 1 starts exactly at frame 2's first samples.
        Assert.Equal(
            new short[] { 1103, 1103, 1453, 1453, 1772, 1772, 2054, 2054 },
            ReadPcm16(ReadBytes(OutputBAddress, 4096), 0));
        // Descriptor 2 starts at output offset 8704 and only receives 512
        // bytes; the rest of the descriptor must be zeroed, not stale 0xAB.
        Assert.Equal(
            new short[] { 2681, 2681, 2568, 2568, 2401, 2401, 2187, 2187 },
            ReadPcm16(ReadBytes(OutputCAddress, Mp3FramePcm16Bytes), 0));
        Assert.All(ReadBytes(OutputCAddress + 512, Mp3FramePcm16Bytes - 512), value => Assert.Equal(0, value));
    }

    [Fact]
    public void BatchJobRun_Mp3GarbageInputIsConsumedAndOutputCleared()
    {
        var instanceId = SetupMp3Instance();
        var garbage = new byte[64];
        for (var i = 0; i < garbage.Length; i++)
        {
            garbage[i] = (byte)(i % 3 == 0 ? 0x00 : 0xAB);
        }

        Assert.True(_memory.TryWrite(InputAddress, garbage));
        InitializeBatch();
        Paint(OutputAddress, Mp3FramePcm16Bytes, 0xAB);

        var sideband = RunBatchJob(
            instanceId,
            StreamSidebandFlag | MultipleFramesFlag,
            InputAddress,
            (ulong)garbage.Length,
            OutputAddress,
            Mp3FramePcm16Bytes,
            sidebandSize: 0x20);

        Assert.Equal(0, BinaryPrimitives.ReadInt32LittleEndian(sideband));
        Assert.Equal(64, BinaryPrimitives.ReadInt32LittleEndian(sideband.AsSpan(8)));
        Assert.Equal(0, BinaryPrimitives.ReadInt32LittleEndian(sideband.AsSpan(12)));
        Assert.Equal(0ul, BinaryPrimitives.ReadUInt64LittleEndian(sideband.AsSpan(16)));
        Assert.Equal(1u, BinaryPrimitives.ReadUInt32LittleEndian(sideband.AsSpan(24)));
        Assert.All(ReadBytes(OutputAddress, Mp3FramePcm16Bytes), value => Assert.Equal(0, value));
    }

    [Fact]
    public void BatchJobRun_Mp3UnknownInstanceReportsInvalidParameterStatus()
    {
        SetupMp3Instance();
        WriteMp3Input();
        Paint(OutputAddress, Mp3FramePcm16Bytes, 0xAB);

        var sideband = RunBatchJob(
            instanceId: 0x1234ul,
            StreamSidebandFlag | MultipleFramesFlag,
            InputAddress,
            Mp3InputLength,
            OutputAddress,
            Mp3FramePcm16Bytes,
            sidebandSize: 0x20);

        Assert.Equal(Atrac9DecodeState.ResultInvalidParameter, BinaryPrimitives.ReadInt32LittleEndian(sideband));
        Assert.Equal(0, BinaryPrimitives.ReadInt32LittleEndian(sideband.AsSpan(8)));
        Assert.Equal(0, BinaryPrimitives.ReadInt32LittleEndian(sideband.AsSpan(12)));
        // A failed job must not touch the guest's output buffer.
        Assert.All(ReadBytes(OutputAddress, Mp3FramePcm16Bytes), value => Assert.Equal(0xAB, value));
    }

    [Fact]
    public void BatchJobRun_Mp3SidebandFormatReportsStreamLayoutAndEncoding()
    {
        var instanceId = SetupMp3Instance();
        WriteMp3Input();

        var sideband = RunBatchJob(
            instanceId,
            StreamSidebandFlag | FormatSidebandFlag | MultipleFramesFlag,
            InputAddress,
            Mp3InputLength,
            OutputAddress,
            5 * Mp3FramePcm16Bytes,
            sidebandSize: 0x38);

        // Result + stream blocks lead; the format block then reports the
        // layout learned from the decoded frames (joint stereo, 44.1 kHz, S16
        // because the instance flags asked for signed PCM).
        Assert.Equal(0, BinaryPrimitives.ReadInt32LittleEndian(sideband));
        Assert.Equal(5 * Mp3FramePcm16Bytes, BinaryPrimitives.ReadInt32LittleEndian(sideband.AsSpan(12)));
        Assert.Equal(2u, BinaryPrimitives.ReadUInt32LittleEndian(sideband.AsSpan(24)));
        Assert.Equal(0x3u, BinaryPrimitives.ReadUInt32LittleEndian(sideband.AsSpan(28)));
        Assert.Equal(44100u, BinaryPrimitives.ReadUInt32LittleEndian(sideband.AsSpan(32)));
        Assert.Equal((uint)Atrac9PcmEncoding.Signed16, BinaryPrimitives.ReadUInt32LittleEndian(sideband.AsSpan(36)));
        Assert.Equal(5u, BinaryPrimitives.ReadUInt32LittleEndian(sideband.AsSpan(48)));
    }

    public void Dispose()
    {
        AjmExports.ResetForTests();
    }

    private uint SetupMp3Instance()
    {
        _ctx[CpuRegister.Rdi] = 0;
        _ctx[CpuRegister.Rsi] = ContextAddress;
        Assert.Equal(0, AjmExports.AjmInitialize(_ctx));
        var contextId = ReadUInt32(ContextAddress);

        _ctx[CpuRegister.Rdi] = contextId;
        _ctx[CpuRegister.Rsi] = 0; // AJM codec 0 = MP3
        _ctx[CpuRegister.Rdx] = 0;
        Assert.Equal(0, AjmExports.AjmModuleRegister(_ctx));

        _ctx[CpuRegister.Rdi] = contextId;
        _ctx[CpuRegister.Rsi] = 0;
        _ctx[CpuRegister.Rdx] = Mp3InstanceFlags;
        _ctx[CpuRegister.Rcx] = InstanceAddress;
        Assert.Equal(0, AjmExports.AjmInstanceCreate(_ctx));
        return ReadUInt32(InstanceAddress);
    }

    private void WriteMp3Input()
    {
        Assert.True(_memory.TryWrite(InputAddress, Mp3Sine));
        InitializeBatch();
    }

    private void InitializeBatch()
    {
        _ctx[CpuRegister.Rdi] = BatchBufferAddress;
        _ctx[CpuRegister.Rsi] = 0x200;
        _ctx[CpuRegister.Rdx] = BatchInfoAddress;
        Assert.Equal(0, AjmExports.AjmBatchInitialize(_ctx));
    }

    private byte[] RunBatchJob(
        ulong instanceId,
        ulong flags,
        ulong inputAddress,
        ulong inputSize,
        ulong outputAddress,
        ulong outputSize,
        ulong sidebandSize)
    {
        _ctx[CpuRegister.Rdi] = BatchInfoAddress;
        _ctx[CpuRegister.Rsi] = instanceId;
        _ctx[CpuRegister.Rdx] = flags;
        _ctx[CpuRegister.Rcx] = inputAddress;
        _ctx[CpuRegister.R8] = inputSize;
        _ctx[CpuRegister.R9] = outputAddress;
        WriteStackArgs(outputSize, SidebandAddress, sidebandSize);
        Assert.Equal(0, AjmExports.AjmBatchJobRun(_ctx));
        return ReadBytes(SidebandAddress, 0x40);
    }

    private byte[] RunBatchJobSplit(
        ulong instanceId,
        ulong flags,
        ulong inputDescriptors,
        ulong inputCount,
        ulong outputDescriptors,
        ulong outputCount,
        ulong sidebandSize)
    {
        _ctx[CpuRegister.Rdi] = BatchInfoAddress;
        _ctx[CpuRegister.Rsi] = instanceId;
        _ctx[CpuRegister.Rdx] = flags;
        _ctx[CpuRegister.Rcx] = inputDescriptors;
        _ctx[CpuRegister.R8] = inputCount;
        _ctx[CpuRegister.R9] = outputDescriptors;
        WriteStackArgs(outputCount, SidebandAddress, sidebandSize);
        Assert.Equal(0, AjmExports.AjmBatchJobRunSplit(_ctx));
        return ReadBytes(SidebandAddress, 0x40);
    }

    /// <summary>
    /// Places the seventh, eighth and ninth SysV arguments where the export
    /// reads them: just past the return address slot at [rsp].
    /// </summary>
    private void WriteStackArgs(ulong outputCount, ulong sidebandAddress, ulong sidebandSize)
    {
        _ctx[CpuRegister.Rsp] = StackAddress;
        WriteUInt64(StackAddress + 8, outputCount);
        WriteUInt64(StackAddress + 16, sidebandAddress);
        WriteUInt64(StackAddress + 24, sidebandSize);
    }

    private static short[] ReadPcm16(byte[] pcm, int byteOffset)
    {
        var samples = new short[8];
        for (var i = 0; i < samples.Length; i++)
        {
            samples[i] = BinaryPrimitives.ReadInt16LittleEndian(pcm.AsSpan(byteOffset + (i * sizeof(short))));
        }

        return samples;
    }

    private void Paint(ulong address, int length, byte fill)
    {
        var paint = new byte[length];
        Array.Fill(paint, fill);
        Assert.True(_memory.TryWrite(address, paint));
    }

    private uint ReadUInt32(ulong address)
    {
        Span<byte> value = stackalloc byte[sizeof(uint)];
        Assert.True(_memory.TryRead(address, value));
        return BinaryPrimitives.ReadUInt32LittleEndian(value);
    }

    private void WriteUInt64(ulong address, ulong value)
    {
        Span<byte> bytes = stackalloc byte[sizeof(ulong)];
        BinaryPrimitives.WriteUInt64LittleEndian(bytes, value);
        Assert.True(_memory.TryWrite(address, bytes));
    }

    private byte[] ReadBytes(ulong address, int length)
    {
        var value = new byte[length];
        Assert.True(_memory.TryRead(address, value));
        return value;
    }

    // Five 417/418-byte 128 kbps 44.1 kHz joint-stereo MPEG-1 Layer III frames
    // of a 1 kHz sine (ffmpeg -f lavfi sine, -write_xing 0) plus a 4-byte
    // partial frame header suffix (FF FB 90 00) so NLayer's reader also emits
    // the fifth frame. 2093 bytes total.
    private static readonly byte[] Mp3Sine = Convert.FromHexString(
        "FFFB9064000002A5114D1565800000000D20A0000116BD4D1E39EA8000000034830000000255A457457425A01D00E916" +
        "CBDDB5874003AA0D14D164E194E58CE16CD32C303081208004008034120C0F17AF5E667EBD783E0802008062B07CFE50" +
        "3129EFE8F3FD1E7FA3DFD1E0F9F97020219007CFE040C77F40005060B215C61821C91188EE22628236862803DEF1183D" +
        "96974C1FC79CE391730C53C688C40DBBC1012472029F0624A12606464781A10DE4D1BE0668398184C680633171A98B6A" +
        "03178D80C0017030C0540C2C1D492EF80903018241006090705888180002BABF818000A1B30371036041FA85C2FFF85A" +
        "089B8357062A18D10543E1FFFC6385042161C91728B94870B985CDFEDFF21A2E517290E1730E713239A414A440BFFA97" +
        "FF2F18974D522F242570757FFFD8B599000FCB2DBE860110002601400C6603B00C6602A0042601F0132608503E261680" +
        "5C6616D2622659C0BAC60E58166606400AA602A00E6601B00240608B01A104168439F5ECDD92450356A4D52AB5756817" +
        "D851A96C5E3EDD36AE865A67D5E2F177CCBB4743BEFE8A7B752F4B7EB6E9300019FFFB92643900F3781B44177E800000" +
        "000D20E000010BBC6F174D784A8000003480000004C8D225AFE6F4D80486AC32094B4C62461984E71FE189E5609ED10C" +
        "9184E0239814808038025BE825CA876D326CFE0D505990CA888E95C3EC230BB452A498429A9BD7397E8B1DB7D16AD16B" +
        "FE6285B47257A8D2B8AA06A05DB90FD6FDFF95010C501C2E1C001D3131A3372E364493E1B630DA0E8538FB8226307200" +
        "693027401A1202241401788400343E6031265A48B7845514548F5BAAAEDB8FC9B537747674697F7AABCFD952E6B35119" +
        "97952FBD2834134A201527555699A681A9725F362D6C5C98F8AB6D015ED1940AF1FFDDF960188424AA1C303660224656" +
        "246C4063FAC61CB02CC72EB003E0E0F1016A669C046461A6021622006171CFD6F5DDFFF7FBBDDED0ABDA6D73C909C590" +
        "3E822698599BD6F084FB16894378A54F166190D290392E9B56329A05D2A4C5C0AA31845274D8B12B45F2635A90BE9195" +
        "5360001FED2C6891135CE61B0E985C1209321A21CC7126E195056ADE02001808104C05194B1F383261E10018285F088A" +
        "9826038964E3EDCF0EECE85FCD3C3CC6908E05FFFB9264918FF38B3DC2837F12A000000D200000010DC04B0A0DFF6A00" +
        "00003480000004F4DBD2C146464C74678640A3C0A8798983A6F18F95997959939098A83A55188101941518F030545CC1" +
        "01804109591956F45748B6DDB8172C041EC5D4A1008980EA5995C526D95AEB81E00584658E99770BC0AE240E9A99A63B" +
        "6ED2177B138BCC430FE462C577F2318CAE5F9D4C31944625949B95C6E7E61CB77E5F9524623162BBF90E733B79E7FF9F" +
        "E1873F3CF54F4F6FF5861CCE929391C3DA0F8067E61E7801100000052481040000033F0DF334D700A81444097F98FF0A" +
        "9A3A5318281998000CFF9B0D329C50798D04260181DFE06C08181A0098190837EC0D5E0F033CA080C086301C010E4462" +
        "703231A80C781303259000C004710B0A4889953C080941632818940806191600085C9938515A3F0020A018180A0120A0" +
        "220913980A0249A32744C50FC2C681BA418CC37806FA19305FF5A9664A747F8B8841514E1F85B85C22128CA0B8D16AAA" +
        "D7FF1730C68C61505C43923A48810A32C3FAEA57AFFFE3984147387F2991122C4089B22039A438879F20AA57F57FFFF9" +
        "34522B957FFFFB9264E080072449CDED73600E00000D20A000011BC1A11F39DA800000003483000000FEA54C414D4533" +
        "2E3130305555555555555555555555555555555555555555555555555555555555555555555555555555555555555555" +
        "555555555555555555555555555555555555555555555555555555555555555555555555555555555555555555555555" +
        "555555555555555555555555555555555555555555555555555555555555555555555555555555555555555555555555" +
        "555555555555555555555555555555555555555555555555555555555555555555555555555555555555555555555555" +
        "555555555555555555555555555555555555555555555555555555555555555555555555555555555555555555555555" +
        "5555555555554C414D45332E313030555555555555555555555555555555555555555555555555555555555555555555" +
        "555555555555555555555555555555555555555555555555555555555555555555555555555555555555555555555555" +
        "555555555555555555555555555555555555555555555555555555555555555555555555555555FFFB9264408FF00000" +
        "690700000800000D20E00001000001A40000002000003480000004555555555555555555555555555555555555555555" +
        "555555555555555555555555555555555555555555555555555555555555555555555555555555555555555555555555" +
        "555555555555555555555555555555555555555555555555555555555555555555555555555555555555555555555555" +
        "555555555555555555555555555555555555555555555555555555555555555555555555555555555555555555555555" +
        "555555555555555555555555555555555555555555555555555555555555555555555555555555555555555555555555" +
        "555555555555555555555555555555555555555555555555555555555555555555555555555555555555555555555555" +
        "555555555555555555555555555555555555555555555555555555555555555555555555555555555555555555555555" +
        "555555555555555555555555555555555555555555555555555555555555555555555555555555555555555555555555" +
        "55555555555555555555555555555555555555555555555555FFFB9000"
    );
}
