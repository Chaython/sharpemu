// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace SharpEmu.Core.Loader;

/// <summary>
/// Diagnostics helpers for guest executable images (eboot.bin / ELF / fSELF).
/// </summary>
/// <remarks>
/// These helpers exist so that broken game dumps fail fast with an actionable
/// message instead of an opaque loader exception. The canonical incident: a
/// 0-byte eboot.bin (interrupted extraction, cloud placeholder, or antivirus
/// quarantine) surfaced as "Input image is empty." with no hint that the dump
/// — not the emulator — was at fault.
/// </remarks>
public static class ExecutableImageDiagnostics
{
    /// <summary>Size of an ELF header; nothing smaller can be a loadable image.</summary>
    public const int MinExecutableImageSize = 64;

    private static readonly string[] CandidateSearchPatterns =
    {
        "eboot.bin",
        "eboot*.bin",
        "*.elf",
        "*.fself",
        "*.self",
    };

    private static readonly string[] CandidateSearchSubdirectories =
    {
        // Dump tools commonly place the decrypted executable beside the retail
        // tree; keep these sidecar locations in sync with BindApp0Root in
        // SharpEmuRuntime.
        "app0",
        "decrypted",
    };

    public readonly record struct ExecutableImageCandidate(string Path, long SizeInBytes);

    /// <summary>
    /// Finds non-empty executable-looking files near a broken executable so the
    /// user can be pointed at a working image instead of a raw loader error.
    /// </summary>
    /// <param name="ebootPath">The (presumably broken) executable path to search around.</param>
    /// <param name="maxResults">Maximum number of candidates to return.</param>
    /// <returns>Candidates ordered by size (largest first), excluding <paramref name="ebootPath"/> itself.</returns>
    public static IReadOnlyList<ExecutableImageCandidate> FindCandidateExecutables(
        string ebootPath,
        int maxResults = 5)
    {
        if (string.IsNullOrWhiteSpace(ebootPath) || maxResults <= 0)
        {
            return Array.Empty<ExecutableImageCandidate>();
        }

        var searchRoot = Path.GetDirectoryName(Path.GetFullPath(ebootPath));
        if (string.IsNullOrEmpty(searchRoot) || !Directory.Exists(searchRoot))
        {
            return Array.Empty<ExecutableImageCandidate>();
        }

        var searchDirectories = new List<string> { searchRoot };
        foreach (var subdirectory in CandidateSearchSubdirectories)
        {
            var path = Path.Combine(searchRoot, subdirectory);
            if (Directory.Exists(path))
            {
                searchDirectories.Add(path);
            }
        }

        var exclusion = Path.GetFullPath(ebootPath);
        var candidates = new Dictionary<string, long>(OperatingSystem.IsWindows()
            ? StringComparer.OrdinalIgnoreCase
            : StringComparer.Ordinal);

        foreach (var directory in searchDirectories)
        {
            foreach (var pattern in CandidateSearchPatterns)
            {
                IEnumerable<string> matches;
                try
                {
                    matches = Directory.EnumerateFiles(directory, pattern, SearchOption.TopDirectoryOnly);
                }
                catch (IOException)
                {
                    continue;
                }
                catch (UnauthorizedAccessException)
                {
                    continue;
                }

                foreach (var match in matches)
                {
                    var fullPath = Path.GetFullPath(match);
                    if (string.Equals(fullPath, exclusion, OperatingSystem.IsWindows()
                            ? StringComparison.OrdinalIgnoreCase
                            : StringComparison.Ordinal))
                    {
                        continue;
                    }

                    long length;
                    try
                    {
                        length = new FileInfo(fullPath).Length;
                    }
                    catch (IOException)
                    {
                        continue;
                    }
                    catch (UnauthorizedAccessException)
                    {
                        continue;
                    }

                    if (length <= 0 || length > int.MaxValue)
                    {
                        continue;
                    }

                    // First sighting wins; search directories are ordered by
                    // preference (root before sidecars) so the earliest path
                    // shape is kept when two files share a normalized name.
                    candidates.TryAdd(fullPath, length);
                }
            }
        }

        return candidates
            .Select(static pair => new ExecutableImageCandidate(pair.Key, pair.Value))
            .OrderByDescending(static candidate => candidate.SizeInBytes)
            .ThenBy(static candidate => candidate.Path, StringComparer.Ordinal)
            .Take(maxResults)
            .ToArray();
    }

    /// <summary>
    /// Builds the user-facing explanation for an empty (0-byte) executable image,
    /// including nearby candidate executables when any exist.
    /// </summary>
    public static string BuildEmptyImageErrorMessage(string ebootPath, IReadOnlyList<ExecutableImageCandidate>? candidates = null)
    {
        var message = new StringBuilder();
        message.Append("The selected executable is 0 bytes (empty): \"")
            .Append(ebootPath)
            .AppendLine("\".")
            .AppendLine("This is a problem with the game dump, not an emulator crash. Common causes:")
            .AppendLine("  - the archive extraction was interrupted or the download is still in progress")
            .AppendLine("  - the folder is managed by a cloud-sync service and the file is an online-only placeholder")
            .AppendLine("  - antivirus software quarantined the file content")
            .AppendLine("A real PS5 eboot.bin is typically 10-500 MB — re-extract the game and verify the file is non-empty before launching.");

        candidates ??= FindCandidateExecutables(ebootPath);
        if (candidates.Count > 0)
        {
            message.AppendLine("Non-empty executable images were found nearby — try launching one of these instead:");
            foreach (var candidate in candidates)
            {
                message.Append("  - \"")
                    .Append(candidate.Path)
                    .Append("\" (")
                    .Append(candidate.SizeInBytes)
                    .AppendLine(" bytes)");
            }
        }
        else
        {
            message.AppendLine("No non-empty executable image was found in the game folder.");
        }

        message.Append("Note: SharpEmu requires decrypted dumps (bare ELF or fake-signed fSELF); an intact retail-encrypted eboot cannot be loaded either.");
        return message.ToString();
    }

    /// <summary>
    /// Builds the user-facing explanation for an executable that is non-empty but
    /// far too small to contain any ELF/SELF image.
    /// </summary>
    public static string BuildTooSmallImageErrorMessage(string ebootPath, long actualSizeInBytes)
    {
        return
            $"The selected executable is only {actualSizeInBytes} bytes: \"{ebootPath}\". " +
            $"A loadable image needs at least {MinExecutableImageSize} bytes for the ELF header. " +
            "The file is truncated or corrupted — re-extract the game and verify the dump.";
    }
}
