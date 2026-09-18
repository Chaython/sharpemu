// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.RegularExpressions;
using SharpEmu.Core.Cpu.Native;
using SharpEmu.HLE;
using Xunit;

namespace SharpEmu.Libs.Tests.Cpu;

/// <summary>
/// The import-diagnostic rings on <see cref="DirectExecutionBackend"/> (the
/// recent-import trace, the distinct-NID history, and the import-loop signature
/// history) are shared by every concurrent guest executor attached to the same
/// backend instance — real titles spawn 8-12 concurrent runners (the Astro TBB
/// spawn storm covered by PrewarmNativeGuestWorkers). Those rings must never
/// lose, tear, or corrupt an entry under that concurrency: a torn
/// RecentImportTraceEntry misdirects crash triage, and a corrupted signature
/// window could falsely force-exit a healthy guest.
/// The hammer runs in an isolated worker process (same pattern as
/// Gen5NativeReturnSmokeTests) because constructing the backend installs
/// process-wide POSIX signal handlers and native guest workers.
/// </summary>
public sealed class ImportDiagnosticsRingStressTests
{
    private const string WorkerEnvironmentVariable = "SHARPEMU_IMPORT_DIAG_STRESS_WORKER";
    private static readonly TimeSpan WorkerTimeout = TimeSpan.FromSeconds(90);

    private const int RecentImportRingCapacity = 64;
    private const int RecordsPerRunner = 100_000;

    private static readonly Regex DistinctNidPattern = new("^d-[0-9]+-[0-9]+$", RegexOptions.Compiled);

    [Fact]
    public async Task ConcurrentRunnersKeepImportDiagnosticsRingsConsistent()
    {
        if (string.Equals(
                Environment.GetEnvironmentVariable(WorkerEnvironmentVariable),
                "1",
                StringComparison.Ordinal))
        {
            // Isolated worker: hammer the exact production entry points with the
            // observed 8-12 concurrent runners.
            RunStressHammer(8);
            RunStressHammer(12);
            return;
        }

        var result = await RunIsolatedWorker();
        Assert.True(
            result.Completed,
            $"import diagnostics stress worker did not exit within {WorkerTimeout.TotalSeconds:F0} seconds\n{result.Output}");
        Assert.True(
            result.ExitCode == 0,
            $"import diagnostics stress worker exited with code {result.ExitCode}\n{result.Output}");
    }

    private static void RunStressHammer(int runnerCount)
    {
        using var backend = new DirectExecutionBackend(new ModuleManager());

        var writerFailures = new ConcurrentQueue<Exception>();
        var start = new ManualResetEventSlim(false);
        var writers = new Task[runnerCount];
        for (int runner = 0; runner < runnerCount; runner++)
        {
            int runnerId = runner;
            writers[runner] = Task.Run(() =>
            {
                try
                {
                    start.Wait();
                    for (int i = 0; i < RecordsPerRunner; i++)
                    {
                        // Every field of the record is derived from (runnerId, i) so a
                        // torn entry (fields from two concurrent writers mixed) or a
                        // corrupted slot is detectable by cross-checking them.
                        ulong arg0 = ((ulong)(uint)runnerId << 32) | (uint)i;
                        backend.RecordRecentImportTrace(
                            i,
                            $"nid-{runnerId}-{i}",
                            (ulong)i,
                            arg0,
                            0xDEAD_0000_0000_0000UL + arg0,
                            arg0 * 3UL + 7UL);
                        if ((i & 0x1FF) == 0)
                        {
                            backend.TrackDistinctImportNid($"d-{runnerId}-{i}");
                        }

                        if ((i & 0x7F) == 0)
                        {
                            backend.RecordImportLoopSignature(
                                arg0,
                                (ulong)i,
                                arg0 * 0x9E3779B97F4A7C15UL);
                        }

                        if ((i & 0xFFF) == 0xFFF)
                        {
                            // Writers double as readers: a snapshot taken immediately
                            // after an append has the best odds of overlapping another
                            // runner mid-append, so a torn slot is caught here first.
                            AssertRingInvariants(backend, runnerCount);
                        }
                    }
                }
                catch (Exception ex)
                {
                    writerFailures.Enqueue(ex);
                }
            });
        }

        var stopReading = new ManualResetEventSlim(false);
        var readerFailures = new ConcurrentQueue<Exception>();
        var reader = Task.Run(() =>
        {
            try
            {
                start.Wait();
                while (!stopReading.IsSet)
                {
                    AssertRingInvariants(backend, runnerCount);
                }
            }
            catch (Exception ex)
            {
                readerFailures.Enqueue(ex);
            }
        });

        start.Set();
        Task.WaitAll(writers);
        stopReading.Set();
        reader.Wait();

        RethrowFirst(writerFailures, "writer");
        RethrowFirst(readerFailures, "reader");

        // Final state after all runners joined: the ring is saturated with the
        // last capacity appends, every entry intact and per-runner ordered, and
        // the bounded count never drifted past capacity (a check-then-increment
        // race overwrites that invariant without the ring gate).
        Assert.Equal(RecentImportRingCapacity, backend.RecentImportTraceCount);
        var snapshot = backend.SnapshotRecentImportTrace();
        Assert.Equal(RecentImportRingCapacity, snapshot.Length);
        AssertEntriesConsistent(snapshot, runnerCount);
        Assert.Equal(snapshot.Length, snapshot.Select(e => e.Arg0).Distinct().Count());

        var lastSeq = new long[runnerCount];
        Array.Fill(lastSeq, -1L);
        foreach (var entry in snapshot)
        {
            (int runnerId, long seq) = DecodeArg0(entry.Arg0);
            Assert.True(seq > lastSeq[runnerId],
                $"recent-import ring lost append order for runner {runnerId}: {seq} after {lastSeq[runnerId]}");
            lastSeq[runnerId] = seq;
        }

        var prelude = backend.GetRecentDistinctImportPrelude(maxCount: 5, skipNid: "skip-none");
        Assert.Equal(5, prelude.Count);
        Assert.Equal(5, prelude.Distinct().Count());
        foreach (var item in prelude)
        {
            Assert.Matches(DistinctNidPattern, item);
        }

        // The import-loop watchdog must still work after the concurrent hammer:
        // a strict severe pattern (one import, two call sites, period-6 signature
        // cycle) has to be detected once the rings are reset and fed serially.
        backend.ResetImportLoopPattern();
        Assert.False(backend.HasRepeatingImportLoopPattern());
        for (int i = 0; i < 240; i++)
        {
            int position = i % 6;
            backend.RecordImportLoopSignature(
                0xA000UL,
                0xB000UL + (ulong)(i & 1),
                0xC000UL + (ulong)position);
        }

        Assert.True(backend.HasRepeatingImportLoopPattern());
    }

    private static void AssertRingInvariants(DirectExecutionBackend backend, int runnerCount)
    {
        Assert.True(backend.RecentImportTraceCount <= RecentImportRingCapacity,
            $"recent-import ring count {backend.RecentImportTraceCount} exceeded capacity {RecentImportRingCapacity}");
        var snapshot = backend.SnapshotRecentImportTrace();
        Assert.True(snapshot.Length <= RecentImportRingCapacity,
            $"recent-import ring count {snapshot.Length} exceeded capacity {RecentImportRingCapacity}");
        AssertEntriesConsistent(snapshot, runnerCount);

        var prelude = backend.GetRecentDistinctImportPrelude(maxCount: 5, skipNid: "skip-none");
        Assert.True(prelude.Count <= 5, $"distinct-NID prelude returned {prelude.Count} items");
        foreach (var item in prelude)
        {
            Assert.Matches(DistinctNidPattern, item);
        }

        // All hammered signatures are distinct, so the severe-pattern check must
        // never fire while the runners are interleaving.
        Assert.False(backend.HasRepeatingImportLoopPattern());
    }

    private static void AssertEntriesConsistent(
        DirectExecutionBackend.RecentImportTraceEntry[] snapshot,
        int runnerCount)
    {
        foreach (var entry in snapshot)
        {
            if (string.IsNullOrEmpty(entry.Nid))
            {
                continue;
            }

            (int runnerId, long seq) = DecodeArg0(entry.Arg0);
            Assert.True(runnerId >= 0 && runnerId < runnerCount,
                $"recent-import entry has out-of-range runner {runnerId}");
            Assert.Equal($"nid-{runnerId}-{seq}", entry.Nid);
            Assert.Equal(seq, entry.DispatchIndex);
            Assert.Equal((ulong)seq, entry.ReturnRip);
            Assert.Equal(0xDEAD_0000_0000_0000UL + entry.Arg0, entry.Arg1);
            Assert.Equal(entry.Arg0 * 3UL + 7UL, entry.Arg2);
        }
    }

    private static (int RunnerId, long Seq) DecodeArg0(ulong arg0) =>
        ((int)(uint)(arg0 >> 32), (int)(uint)arg0);

    private static void RethrowFirst(ConcurrentQueue<Exception> failures, string role)
    {
        if (failures.IsEmpty)
        {
            return;
        }

        throw new InvalidOperationException(
            $"{role} failed during import diagnostics stress",
            failures.First());
    }

    private static async Task<WorkerResult> RunIsolatedWorker()
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = ResolveDotnetHost(),
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        startInfo.ArgumentList.Add("test");
        startInfo.ArgumentList.Add(typeof(ImportDiagnosticsRingStressTests).Assembly.Location);
        startInfo.ArgumentList.Add("--filter");
        startInfo.ArgumentList.Add(
            $"FullyQualifiedName={typeof(ImportDiagnosticsRingStressTests).FullName}.{nameof(ConcurrentRunnersKeepImportDiagnosticsRingsConsistent)}");
        startInfo.Environment[WorkerEnvironmentVariable] = "1";

        using var process = Process.Start(startInfo) ??
            throw new InvalidOperationException("Could not start the import diagnostics stress worker.");
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();

        try
        {
            await process.WaitForExitAsync().WaitAsync(WorkerTimeout);
        }
        catch (TimeoutException)
        {
            process.Kill(entireProcessTree: true);
            await process.WaitForExitAsync();
            return new WorkerResult(false, process.ExitCode, await ReadOutput(stdout, stderr));
        }

        return new WorkerResult(true, process.ExitCode, await ReadOutput(stdout, stderr));
    }

    private static string ResolveDotnetHost()
    {
        var configuredHost = Environment.GetEnvironmentVariable("DOTNET_HOST_PATH");
        if (!string.IsNullOrWhiteSpace(configuredHost))
        {
            return configuredHost;
        }

        var processPath = Environment.ProcessPath;
        if (processPath is not null &&
            string.Equals(
                Path.GetFileNameWithoutExtension(processPath),
                "dotnet",
                StringComparison.OrdinalIgnoreCase))
        {
            return processPath;
        }

        return "dotnet";
    }

    private static async Task<string> ReadOutput(Task<string> stdout, Task<string> stderr) =>
        await stdout + await stderr;

    private sealed record WorkerResult(bool Completed, int ExitCode, string Output);
}
