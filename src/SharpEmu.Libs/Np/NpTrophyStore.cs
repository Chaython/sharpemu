// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Text.Json;
using System.Text.Json.Serialization;

namespace SharpEmu.Libs.Np;

/// <summary>
/// Persistent per-title trophy unlock state. Each unlock is recorded as a
/// (trophy id, unlock timestamp) pair in a JSON file under
/// <c>user/trophies/&lt;titleId&gt;/trophies.json</c> next to the executable
/// (overridable via <c>SHARPEMU_TROPHY_DIR</c>), so unlock state survives
/// emulator restarts the way it survives console restarts. This type is
/// filesystem + cache logic with no guest interop; the guest-facing exports
/// live in <see cref="NpTrophyExports"/> and <see cref="NpTrophy2Exports"/>.
/// </summary>
public static class NpTrophyStore
{
    private const string StoreFileName = "trophies.json";
    private const string TrophyDirVariableName = "SHARPEMU_TROPHY_DIR";

    private static readonly object Gate = new();
    private static string? _titleId;
    private static Dictionary<int, long>? _unlocked; // trophy id -> unix seconds

    /// <summary>One recorded trophy unlock: the trophy id and when it was unlocked.</summary>
    public sealed record TrophyUnlock
    {
        [JsonPropertyName("id")]
        public int Id { get; init; }

        [JsonPropertyName("unlockedAt")]
        public long UnlockedAtUnixSeconds { get; init; }
    }

    /// <summary>Sets the title whose trophy file is used, dropping any cached state.</summary>
    public static void ConfigureApplicationInfo(string? titleId)
    {
        lock (Gate)
        {
            _titleId = string.IsNullOrWhiteSpace(titleId) ? null : SanitizeTitleId(titleId);
            _unlocked = null;
        }
    }

    /// <summary>
    /// Resolves the store file: <c>&lt;overrideDir&gt;/&lt;titleId&gt;/trophies.json</c>, else the
    /// <c>SHARPEMU_TROPHY_DIR</c> override, else <c>user/trophies/&lt;titleId&gt;/trophies.json</c>
    /// next to the executable.
    /// </summary>
    public static string ResolvePath(string? titleId, string? overrideDir = null)
    {
        var root = overrideDir ?? Environment.GetEnvironmentVariable(TrophyDirVariableName);
        if (string.IsNullOrWhiteSpace(root))
        {
            root = Path.Combine(AppContext.BaseDirectory, "user", "trophies");
        }

        return Path.Combine(
            Path.GetFullPath(root),
            string.IsNullOrWhiteSpace(titleId) ? "default" : SanitizeTitleId(titleId),
            StoreFileName);
    }

    // A title id is a path segment here; anything the host would reject in a
    // file name collapses to underscores and an empty result becomes UNKNOWN,
    // mirroring the pipeline-cache storage sanitizer.
    private static string SanitizeTitleId(string? titleId)
    {
        if (string.IsNullOrWhiteSpace(titleId))
        {
            return "UNKNOWN";
        }

        var source = titleId.Trim();
        Span<char> sanitized = source.Length <= 128
            ? stackalloc char[source.Length]
            : new char[source.Length];
        for (var index = 0; index < source.Length; index++)
        {
            var value = source[index];
            sanitized[index] = char.IsAsciiLetterOrDigit(value) || value is '-' or '_'
                ? char.ToUpperInvariant(value)
                : '_';
        }

        return new string(sanitized);
    }

    /// <summary>
    /// Records an unlock. Returns false (with the original timestamp) when the
    /// trophy is already unlocked — the first unlock wins, matching platform
    /// semantics where an unlock is never re-stamped.
    /// </summary>
    public static bool Unlock(int trophyId, out long unlockedAtUnixSeconds)
    {
        if (trophyId < 0)
        {
            unlockedAtUnixSeconds = 0;
            return false;
        }

        lock (Gate)
        {
            var unlocked = EnsureLoadedLocked();
            if (unlocked.TryGetValue(trophyId, out var existing))
            {
                unlockedAtUnixSeconds = existing;
                return false;
            }

            unlockedAtUnixSeconds = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            unlocked[trophyId] = unlockedAtUnixSeconds;
            SaveLocked();
            return true;
        }
    }

    /// <summary>Queries an unlock; <c>false</c> with a zero timestamp when locked.</summary>
    public static bool IsUnlocked(int trophyId, out long unlockedAtUnixSeconds)
    {
        lock (Gate)
        {
            var unlocked = EnsureLoadedLocked();
            if (unlocked.TryGetValue(trophyId, out var stamp))
            {
                unlockedAtUnixSeconds = stamp;
                return true;
            }
        }

        unlockedAtUnixSeconds = 0;
        return false;
    }

    /// <summary>All recorded unlocks, ordered by trophy id.</summary>
    public static IReadOnlyList<TrophyUnlock> GetUnlocked()
    {
        lock (Gate)
        {
            return EnsureLoadedLocked()
                .Select(pair => new TrophyUnlock { Id = pair.Key, UnlockedAtUnixSeconds = pair.Value })
                .OrderBy(unlock => unlock.Id)
                .ToArray();
        }
    }

    /// <summary>
    /// Drops the cached state so the next query re-reads the file. Used when the
    /// application (and therefore the store file) changes, and by tests that
    /// verify persistence across a fresh load.
    /// </summary>
    public static void Reload()
    {
        lock (Gate)
        {
            _unlocked = null;
        }
    }

    private static Dictionary<int, long> EnsureLoadedLocked()
    {
        if (_unlocked is not null)
        {
            return _unlocked;
        }

        var unlocked = new Dictionary<int, long>();
        var path = ResolvePath(_titleId);
        if (File.Exists(path))
        {
            try
            {
                var parsed = JsonSerializer.Deserialize(
                    File.ReadAllText(path),
                    NpTrophyJsonContext.Default.NpTrophyUnlockFile);
                if (parsed?.Unlocked is not null)
                {
                    foreach (var entry in parsed.Unlocked)
                    {
                        if (entry.Id >= 0)
                        {
                            unlocked[entry.Id] = entry.UnlockedAtUnixSeconds;
                        }
                    }
                }
            }
            catch (Exception exception) when (
                exception is JsonException or IOException or UnauthorizedAccessException)
            {
                // Fall through to empty on a corrupt or unreadable file; the
                // next successful unlock rewrites it.
            }
        }

        _unlocked = unlocked;
        return _unlocked;
    }

    private static void SaveLocked()
    {
        var path = ResolvePath(_titleId);
        var file = new NpTrophyUnlockFile
        {
            Unlocked = _unlocked!
                .Select(pair => new TrophyUnlock { Id = pair.Key, UnlockedAtUnixSeconds = pair.Value })
                .OrderBy(unlock => unlock.Id)
                .ToArray(),
        };

        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, JsonSerializer.Serialize(file, NpTrophyJsonContext.Default.NpTrophyUnlockFile));
    }
}

/// <summary>JSON envelope persisted to <c>trophies.json</c>.</summary>
internal sealed record NpTrophyUnlockFile
{
    [JsonPropertyName("unlocked")]
    public NpTrophyStore.TrophyUnlock[] Unlocked { get; init; } = [];
}

[JsonSerializable(typeof(NpTrophyUnlockFile))]
[JsonSourceGenerationOptions(WriteIndented = true)]
internal sealed partial class NpTrophyJsonContext : JsonSerializerContext;
