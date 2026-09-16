// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.Core.Loader;
using SharpEmu.Core.Runtime;
using Xunit;

namespace SharpEmu.Libs.Tests.Loader;

public sealed class ExecutableImageDiagnosticsTests : IDisposable
{
    private readonly string _tempRoot =
        Directory.CreateTempSubdirectory("sharpemu-diag-tests-").FullName;

    private static readonly Lazy<ISharpEmuRuntime> SharedRuntime = new(
        static () => SharpEmuRuntime.CreateDefault());

    public void Dispose()
    {
        try
        {
            Directory.Delete(_tempRoot, recursive: true);
        }
        catch (IOException)
        {
            // Best-effort cleanup; the OS temp janitor handles the rest.
        }
    }

    private string WriteFile(string relativePath, int sizeInBytes)
    {
        var fullPath = Path.Combine(_tempRoot, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
        if (sizeInBytes == 0)
        {
            File.WriteAllBytes(fullPath, Array.Empty<byte>());
        }
        else
        {
            var payload = new byte[sizeInBytes];
            payload[0] = 0x7F;
            payload[1] = 0x45;
            payload[2] = 0x4C;
            payload[3] = 0x46;
            File.WriteAllBytes(fullPath, payload);
        }

        return fullPath;
    }

    [Fact]
    public void FindCandidateExecutables_FindsNonEmptySiblings_AndExcludesBrokenInput()
    {
        var broken = WriteFile("eboot.bin", sizeInBytes: 0);
        var good = WriteFile("game.elf", sizeInBytes: 128);

        var candidates = ExecutableImageDiagnostics.FindCandidateExecutables(broken);

        var candidate = Assert.Single(candidates);
        Assert.Equal(Path.GetFullPath(good), candidate.Path);
        Assert.Equal(128, candidate.SizeInBytes);
    }

    [Fact]
    public void FindCandidateExecutables_ReturnsEmpty_WhenOnlyEmptyFilesExist()
    {
        var broken = WriteFile("eboot.bin", sizeInBytes: 0);
        WriteFile("other.elf", sizeInBytes: 0);

        var candidates = ExecutableImageDiagnostics.FindCandidateExecutables(broken);

        Assert.Empty(candidates);
    }

    [Fact]
    public void FindCandidateExecutables_SearchesSidecarDirectories()
    {
        var broken = WriteFile("eboot.bin", sizeInBytes: 0);
        var decrypted = WriteFile(Path.Combine("decrypted", "eboot.bin"), sizeInBytes: 256);

        var candidates = ExecutableImageDiagnostics.FindCandidateExecutables(broken);

        var candidate = Assert.Single(candidates);
        Assert.Equal(Path.GetFullPath(decrypted), candidate.Path);
        Assert.Equal(256, candidate.SizeInBytes);
    }

    [Fact]
    public void FindCandidateExecutables_OrdersBySizeDescending_AndHonorsMaxResults()
    {
        var broken = WriteFile("eboot.bin", sizeInBytes: 0);
        WriteFile("small.elf", sizeInBytes: 64);
        WriteFile("large.elf", sizeInBytes: 4096);
        WriteFile("medium.self", sizeInBytes: 1024);

        var candidates = ExecutableImageDiagnostics.FindCandidateExecutables(broken, maxResults: 2);

        Assert.Equal(2, candidates.Count);
        Assert.Equal(4096, candidates[0].SizeInBytes);
        Assert.Equal(1024, candidates[1].SizeInBytes);
    }

    [Fact]
    public void FindCandidateExecutables_IgnoresNonExecutableExtensions()
    {
        var broken = WriteFile("eboot.bin", sizeInBytes: 0);
        WriteFile("movie.mp4", sizeInBytes: 8192);
        WriteFile("data.prx", sizeInBytes: 2048);

        var candidates = ExecutableImageDiagnostics.FindCandidateExecutables(broken);

        Assert.Empty(candidates);
    }

    [Fact]
    public void BuildEmptyImageErrorMessage_IncludesPathCausesAndCandidates()
    {
        var broken = WriteFile("eboot.bin", sizeInBytes: 0);
        var good = WriteFile("game.elf", sizeInBytes: 512);

        var message = ExecutableImageDiagnostics.BuildEmptyImageErrorMessage(broken);

        Assert.Contains("0 bytes", message);
        Assert.Contains(broken, message);
        Assert.Contains("re-extract", message);
        Assert.Contains(good, message);
        Assert.Contains("decrypted", message);
    }

    [Fact]
    public void BuildEmptyImageErrorMessage_WithoutCandidates_AdvisesReExtraction()
    {
        var broken = WriteFile("eboot.bin", sizeInBytes: 0);

        var message = ExecutableImageDiagnostics.BuildEmptyImageErrorMessage(broken);

        Assert.Contains("0 bytes", message);
        Assert.DoesNotContain("try launching one of these", message, StringComparison.Ordinal);
        Assert.Contains("re-extract the game", message, StringComparison.Ordinal);
        Assert.Contains("No non-empty executable image was found", message, StringComparison.Ordinal);
    }

    [Fact]
    public void BuildTooSmallImageErrorMessage_IncludesSizes()
    {
        const int actualSize = 16;

        var message = ExecutableImageDiagnostics.BuildTooSmallImageErrorMessage(
            "/games/eboot.bin",
            actualSize);

        Assert.Contains("16 bytes", message);
        Assert.Contains($"{ExecutableImageDiagnostics.MinExecutableImageSize} bytes", message);
        Assert.Contains("truncated or corrupted", message);
    }

    [Fact]
    public void LoadImage_EmptyExecutable_FailsWithActionableMessage()
    {
        // Regression guard for the round-4 user report: a 0-byte eboot.bin used
        // to surface as an opaque "Input image is empty." from deep inside
        // SelfLoader. LoadImage must now reject it with the diagnostic text
        // that explains the broken dump.
        var broken = WriteFile("eboot.bin", sizeInBytes: 0);

        var exception = Record.Exception(() => SharedRuntime.Value.LoadImage(broken));

        var invalidData = Assert.IsType<System.IO.InvalidDataException>(exception);
        Assert.Contains("0 bytes", invalidData.Message);
        Assert.Contains("re-extract", invalidData.Message);
    }

    [Fact]
    public void LoadImage_TruncatedExecutable_FailsWithActionableMessage()
    {
        var truncated = WriteFile("eboot.bin", sizeInBytes: 32);

        var exception = Record.Exception(() => SharedRuntime.Value.LoadImage(truncated));

        var invalidData = Assert.IsType<System.IO.InvalidDataException>(exception);
        Assert.Contains("32 bytes", invalidData.Message);
        Assert.Contains("truncated or corrupted", invalidData.Message);
    }
}
