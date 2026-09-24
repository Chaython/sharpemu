// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Buffers.Binary;
using SharpEmu.HLE;
using SharpEmu.ShaderCompiler;
using Xunit;

namespace SharpEmu.ShaderCompiler.Tests;

public sealed class Gen5ProgramDecodeControlFlowTests
{
    private const ulong ShaderAddress = 0x1_0000_E000;

    [Fact]
    public void ForwardBranchPastEarlyEndpgmKeepsReachableInstructions()
    {
        var memory = new TestCpuMemory(ShaderAddress, 0x1000);
        uint[] words =
        [
            0xBF820001u, // s_branch +1 -> pc 0x8
            0xBF810000u, // early s_endpgm at pc 0x4
            0xBF800000u, // s_nop 0 at pc 0x8
            0xBF810000u, // reachable terminating s_endpgm
        ];

        Span<byte> bytes = stackalloc byte[words.Length * sizeof(uint)];
        for (var index = 0; index < words.Length; index++)
        {
            BinaryPrimitives.WriteUInt32LittleEndian(
                bytes[(index * sizeof(uint))..],
                words[index]);
        }

        Assert.True(memory.TryWrite(ShaderAddress, bytes));
        var ctx = new CpuContext(memory, Generation.Gen5);

        Assert.True(
            Gen5ShaderTranslator.TryDecodeProgram(
                ctx,
                ShaderAddress,
                out var program,
                out var error),
            error);

        Assert.Equal(
            ["SBranch", "SEndpgm", "SNop", "SEndpgm"],
            program.Instructions.Select(static instruction => instruction.Opcode));
    }

    private sealed class TestCpuMemory(ulong baseAddress, int size) : ICpuMemory
    {
        private readonly byte[] _storage = new byte[size];

        public bool TryRead(ulong virtualAddress, Span<byte> destination)
        {
            if (!TryResolve(virtualAddress, destination.Length, out var offset))
            {
                return false;
            }

            _storage.AsSpan(offset, destination.Length).CopyTo(destination);
            return true;
        }

        public bool TryWrite(ulong virtualAddress, ReadOnlySpan<byte> source)
        {
            if (!TryResolve(virtualAddress, source.Length, out var offset))
            {
                return false;
            }

            source.CopyTo(_storage.AsSpan(offset, source.Length));
            return true;
        }

        private bool TryResolve(ulong virtualAddress, int length, out int offset)
        {
            offset = 0;
            if (virtualAddress < baseAddress)
            {
                return false;
            }

            var relative = virtualAddress - baseAddress;
            if (relative + (ulong)length > (ulong)_storage.Length)
            {
                return false;
            }

            offset = (int)relative;
            return true;
        }
    }
}
