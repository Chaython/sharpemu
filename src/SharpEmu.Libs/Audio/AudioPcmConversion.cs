// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Buffers.Binary;

namespace SharpEmu.Libs.Audio;

/// <summary>
/// Converts guest AudioOut submissions (mono/stereo/7.1, s16 or float32) into the
/// interleaved stereo 16-bit PCM that host audio streams accept. Platform-neutral —
/// device specifics live behind IHostAudioStream.
/// </summary>
internal static class AudioPcmConversion
{
    /// <summary>Bytes per output frame: two 16-bit channels.</summary>
    public const int OutputFrameSize = 4;

    /// <summary>
    /// Folds an interleaved guest submission (1/2/6/8 channels, s16 or float32)
    /// down to interleaved stereo 16-bit PCM. 5.1 and 7.1 layouts use the same
    /// fold-down as AudioOut2Exports.MixPortIntoStereo: FL/FR pass through,
    /// Center feeds both sides at -3 dB, backs (and sides on 7.1) feed their
    /// side at -3 dB, and LFE (channel 3) is dropped.
    /// </summary>
    /// <param name="channelGains">
    /// Optional per-channel gains from sceAudioOutSetVolume (index = source
    /// channel). Empty means unity for every channel; the scalar
    /// <paramref name="volume"/> still applies in both cases.
    /// </param>
    public static void ConvertToStereoPcm16(
        ReadOnlySpan<byte> source,
        Span<byte> destination,
        int frames,
        int channels,
        int bytesPerSample,
        bool isFloat,
        float volume,
        ReadOnlySpan<float> channelGains = default)
    {
        var sourceFrameSize = checked(channels * bytesPerSample);
        // Volume is constant for the whole submission, so clamp it once here
        // rather than per sample inside the loop (this runs on every real-time
        // audio buffer, hundreds of frames at a time).
        var clampedVolume = Math.Clamp(volume, 0.0f, 1.0f);
        // Effective gain per source channel = volume * channelGains[c], both
        // clamped; computed once per submission rather than per frame.
        Span<float> effectiveGains = stackalloc float[channels];
        for (var channel = 0; channel < channels; channel++)
        {
            var gain = clampedVolume;
            if (channel < channelGains.Length)
            {
                gain *= Math.Clamp(channelGains[channel], 0.0f, 1.0f);
            }

            effectiveGains[channel] = gain;
        }

        for (var frame = 0; frame < frames; frame++)
        {
            var sourceFrame = source.Slice(frame * sourceFrameSize, sourceFrameSize);
            short left;
            short right;
            if (channels >= 8)
            {
                var fl = ReadNormalizedSample(sourceFrame, 0, bytesPerSample, isFloat) * effectiveGains[0];
                var fr = ReadNormalizedSample(sourceFrame, 1, bytesPerSample, isFloat) * effectiveGains[1];
                var c = ReadNormalizedSample(sourceFrame, 2, bytesPerSample, isFloat) * effectiveGains[2];
                var bl = ReadNormalizedSample(sourceFrame, 4, bytesPerSample, isFloat) * effectiveGains[4];
                var br = ReadNormalizedSample(sourceFrame, 5, bytesPerSample, isFloat) * effectiveGains[5];
                var sl = ReadNormalizedSample(sourceFrame, 6, bytesPerSample, isFloat) * effectiveGains[6];
                var sr = ReadNormalizedSample(sourceFrame, 7, bytesPerSample, isFloat) * effectiveGains[7];
                const float side = 0.70710678f;
                left = ConvertFloatSample(fl + (c * side) + (bl * side) + (sl * side));
                right = ConvertFloatSample(fr + (c * side) + (br * side) + (sr * side));
            }
            else if (channels == 6)
            {
                // 5.1: same fold-down as 7.1 minus the side pair; LFE
                // (channel 3) is dropped exactly like the 7.1 path drops it.
                var fl = ReadNormalizedSample(sourceFrame, 0, bytesPerSample, isFloat) * effectiveGains[0];
                var fr = ReadNormalizedSample(sourceFrame, 1, bytesPerSample, isFloat) * effectiveGains[1];
                var c = ReadNormalizedSample(sourceFrame, 2, bytesPerSample, isFloat) * effectiveGains[2];
                var bl = ReadNormalizedSample(sourceFrame, 4, bytesPerSample, isFloat) * effectiveGains[4];
                var br = ReadNormalizedSample(sourceFrame, 5, bytesPerSample, isFloat) * effectiveGains[5];
                const float side = 0.70710678f;
                left = ConvertFloatSample(fl + (c * side) + (bl * side));
                right = ConvertFloatSample(fr + (c * side) + (br * side));
            }
            else
            {
                left = ReadSample(sourceFrame, 0, bytesPerSample, isFloat);
                right = channels == 1
                    ? left
                    : ReadSample(sourceFrame, 1, bytesPerSample, isFloat);
                left = ApplyVolume(left, effectiveGains[0]);
                right = ApplyVolume(right, effectiveGains[channels == 1 ? 0 : 1]);
            }

            BinaryPrimitives.WriteInt16LittleEndian(destination[(frame * OutputFrameSize)..], left);
            BinaryPrimitives.WriteInt16LittleEndian(destination[((frame * OutputFrameSize) + 2)..], right);
        }
    }

    /// <summary>
    /// Copies interleaved PCM without changing its channel layout. SDL can convert
    /// this directly to the physical device, which preserves surround mixes that
    /// would otherwise be truncated to the first two guest channels.
    /// </summary>
    /// <param name="channelGains">
    /// Optional per-channel gains from sceAudioOutSetVolume: interleaved sample
    /// <c>i</c> belongs to channel <c>i % channels</c>. Empty means the scalar
    /// <paramref name="volume"/> applies to every sample (the legacy behaviour).
    /// </param>
    public static void CopyWithVolume(
        ReadOnlySpan<byte> source,
        Span<byte> destination,
        int channels,
        bool isFloat,
        float volume,
        ReadOnlySpan<float> channelGains = default)
    {
        var clampedVolume = Math.Clamp(volume, 0.0f, 1.0f);
        if (channelGains.IsEmpty)
        {
            if (clampedVolume >= 1.0f)
            {
                source.CopyTo(destination);
                return;
            }

            if (isFloat)
            {
                for (var offset = 0; offset < source.Length; offset += sizeof(float))
                {
                    var sample = BinaryPrimitives.ReadSingleLittleEndian(source.Slice(offset, sizeof(float)));
                    BinaryPrimitives.WriteSingleLittleEndian(
                        destination.Slice(offset, sizeof(float)),
                        sample * clampedVolume);
                }

                return;
            }

            for (var offset = 0; offset < source.Length; offset += sizeof(short))
            {
                var sample = BinaryPrimitives.ReadInt16LittleEndian(source.Slice(offset, sizeof(short)));
                BinaryPrimitives.WriteInt16LittleEndian(
                    destination.Slice(offset, sizeof(short)),
                    ApplyVolume(sample, clampedVolume));
            }

            return;
        }

        // Every channel at (or clamped to) unity with unity volume is a plain
        // copy — the common case, since ports default to full volume.
        if (clampedVolume >= 1.0f && AllGainsAtLeastUnity(channelGains))
        {
            source.CopyTo(destination);
            return;
        }

        var channelCount = Math.Max(1, channels);
        if (isFloat)
        {
            for (var offset = 0; offset < source.Length; offset += sizeof(float))
            {
                var sample = BinaryPrimitives.ReadSingleLittleEndian(source.Slice(offset, sizeof(float)));
                var gain = clampedVolume *
                           Math.Clamp(channelGains[(offset / sizeof(float)) % channelCount], 0.0f, 1.0f);
                BinaryPrimitives.WriteSingleLittleEndian(
                    destination.Slice(offset, sizeof(float)),
                    sample * gain);
            }

            return;
        }

        for (var offset = 0; offset < source.Length; offset += sizeof(short))
        {
            var sample = BinaryPrimitives.ReadInt16LittleEndian(source.Slice(offset, sizeof(short)));
            var gain = clampedVolume *
                       Math.Clamp(channelGains[(offset / sizeof(short)) % channelCount], 0.0f, 1.0f);
            BinaryPrimitives.WriteInt16LittleEndian(
                destination.Slice(offset, sizeof(short)),
                ApplyVolume(sample, gain));
        }
    }

    private static bool AllGainsAtLeastUnity(ReadOnlySpan<float> channelGains)
    {
        foreach (var gain in channelGains)
        {
            if (gain < 1.0f)
            {
                return false;
            }
        }

        return true;
    }

    private static short ReadSample(
        ReadOnlySpan<byte> frame,
        int channel,
        int bytesPerSample,
        bool isFloat)
    {
        var sample = frame.Slice(channel * bytesPerSample, bytesPerSample);
        if (!isFloat)
        {
            return BinaryPrimitives.ReadInt16LittleEndian(sample);
        }

        var bits = BinaryPrimitives.ReadInt32LittleEndian(sample);
        return ConvertFloatSample(BitConverter.Int32BitsToSingle(bits));
    }

    // Normalized [-1, 1] reading of one interleaved sample, the float-domain
    // twin of ReadSample used by the surround fold-down paths (which must
    // accumulate gains across channels before converting back to s16).
    private static float ReadNormalizedSample(
        ReadOnlySpan<byte> frame,
        int channel,
        int bytesPerSample,
        bool isFloat)
    {
        if (!isFloat)
        {
            return BinaryPrimitives.ReadInt16LittleEndian(frame.Slice(channel * bytesPerSample, bytesPerSample)) / 32768f;
        }

        var bits = BinaryPrimitives.ReadInt32LittleEndian(frame.Slice(channel * bytesPerSample, bytesPerSample));
        var value = BitConverter.Int32BitsToSingle(bits);
        return float.IsFinite(value) ? value : 0f;
    }

    private static short ConvertFloatSample(float value)
    {
        if (float.IsNaN(value))
        {
            return 0;
        }

        value = Math.Clamp(value, -1.0f, 1.0f);
        var scale = value < 0.0f ? 32768.0f : short.MaxValue;
        return checked((short)MathF.Round(value * scale));
    }

    // <paramref name="volume"/> is expected pre-clamped to [0, 1] by the caller.
    private static short ApplyVolume(short sample, float volume)
    {
        var scaled = MathF.Round(sample * volume);
        return (short)Math.Clamp(scaled, short.MinValue, short.MaxValue);
    }
}
