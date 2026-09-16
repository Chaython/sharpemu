// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Text;
using System.Threading;
using SharpEmu.HLE;
using SharpEmu.Libs.Kernel;
using SharpEmu.Libs.Network;
using Xunit;

namespace SharpEmu.Libs.Tests.Network;

// Loopback coverage for the socket/DNS exports: every test builds its sockets on
// 127.0.0.1 so the suite needs no external network access. The libSceNet socket
// table is process-global, so each test closes every descriptor it created even
// when an assertion fails.
public sealed class NetExportsLoopbackTests
{
    private const ulong MemoryBase = 0x0000_7FFF_3000_0000;
    private const int MemorySize = 0x1_0000;

    // Private error/errno constants mirrored from NetExports (they are not public).
    private const int NetErrorWouldBlock = unchecked((int)0x80410123);
    private const int NetErrorInProgress = unchecked((int)0x80410124);
    private const int NetErrnoWouldBlock = 35;

    // fcntl commands and O_NONBLOCK (FreeBSD numbering used by the PS5 libc).
    private const int FGetFl = 3;
    private const int FSetFl = 4;
    private const int ONonblock = 0x0004;

    // ORBIS_NET_SOL_SOCKET / ORBIS_NET_SO_NBIO / ORBIS_NET_SO_ERROR.
    private const int SolSocket = 0xFFFF;
    private const int SoNbio = 0x1200;
    private const int SoError = 0x1007;

    // poll(2) event bits (FreeBSD numbering).
    private const short PollIn = 0x0001;

    private static readonly FieldInfo SocketTableField =
        typeof(NetExports).GetField("_sockets", BindingFlags.Static | BindingFlags.NonPublic) ??
        throw new InvalidOperationException("NetExports._sockets is no longer a field; update the tests.");

    private readonly CpuContext _ctx = new(new FakeCpuMemory(MemoryBase, MemorySize), Generation.Gen5);

    private int CreateSocket(int family, int type, int protocol)
    {
        _ctx[CpuRegister.Rdi] = 0;
        _ctx[CpuRegister.Rsi] = unchecked((ulong)family);
        _ctx[CpuRegister.Rdx] = unchecked((ulong)type);
        _ctx[CpuRegister.Rcx] = unchecked((ulong)protocol);
        Assert.Equal(0, NetExports.NetSocket(_ctx));
        var id = checked((int)_ctx[CpuRegister.Rax]);
        Assert.True(id > 0);
        return id;
    }

    private void CloseSocket(int id)
    {
        _ctx[CpuRegister.Rdi] = unchecked((ulong)id);
        Assert.Equal(0, NetExports.NetSocketClose(_ctx));
    }

    // The host socket behind a libSceNet descriptor: the exports do not surface
    // getsockname yet, so loopback tests read the table to learn the ephemeral
    // port a bind(port 0) picked.
    private static Socket GetHostSocket(int id)
    {
        var table = (ConcurrentDictionary<int, Socket>)SocketTableField.GetValue(null)!;
        return table[id];
    }

    private static int GetLocalPort(int id) => ((IPEndPoint)GetHostSocket(id).LocalEndPoint!).Port;

    private ulong WriteGuestBytes(ulong address, ReadOnlySpan<byte> bytes)
    {
        Assert.True(_ctx.Memory.TryWrite(address, bytes));
        return address + (ulong)bytes.Length;
    }

    private void WriteGuestCString(ulong address, string text)
    {
        Span<byte> bytes = stackalloc byte[Encoding.ASCII.GetByteCount(text) + 1];
        Encoding.ASCII.GetBytes(text, bytes);
        bytes[^1] = 0;
        Assert.True(_ctx.Memory.TryWrite(address, bytes));
    }

    // sockaddr_in in the PS5 sa_len-first layout: len, family, port (big-endian),
    // address.
    private ulong WriteLoopbackSockaddr(ulong address, ushort port)
    {
        Span<byte> sockaddr = stackalloc byte[16];
        sockaddr.Clear();
        sockaddr[0] = 16;
        sockaddr[1] = 2; // AF_INET
        sockaddr[2] = (byte)(port >> 8);
        sockaddr[3] = (byte)port;
        sockaddr[4] = 127;
        sockaddr[5] = 0;
        sockaddr[6] = 0;
        sockaddr[7] = 1;
        return WriteGuestBytes(address, sockaddr);
    }

    private void BindLoopback(int id, out int port)
    {
        const ulong sockaddrAddress = MemoryBase + 0x100;
        WriteLoopbackSockaddr(sockaddrAddress, 0);

        _ctx[CpuRegister.Rdi] = unchecked((ulong)id);
        _ctx[CpuRegister.Rsi] = sockaddrAddress;
        _ctx[CpuRegister.Rdx] = 16;
        Assert.Equal(0, NetExports.NetBind(_ctx));

        port = GetLocalPort(id);
        Assert.True(port > 0);
    }

    private void BindListenLoopback(int serverId, out int port)
    {
        BindLoopback(serverId, out port);

        _ctx[CpuRegister.Rdi] = unchecked((ulong)serverId);
        _ctx[CpuRegister.Rsi] = 4;
        Assert.Equal(0, NetExports.NetListen(_ctx));
    }

    private int ConnectLoopback(int clientId, int port)
    {
        const ulong sockaddrAddress = MemoryBase + 0x140;
        WriteLoopbackSockaddr(sockaddrAddress, checked((ushort)port));

        _ctx[CpuRegister.Rdi] = unchecked((ulong)clientId);
        _ctx[CpuRegister.Rsi] = sockaddrAddress;
        _ctx[CpuRegister.Rdx] = 16;
        var result = NetExports.NetConnect(_ctx);

        // Sockets are born non-blocking, so a loopback connect reports EINPROGRESS
        // whenever the kernel did not finish the handshake inline; wait for
        // writability instead of treating that as a failure.
        if (result == NetErrorWouldBlock || result == NetErrorInProgress)
        {
            var socket = GetHostSocket(clientId);
            Assert.True(
                socket.Poll(2_000_000, SelectMode.SelectWrite),
                "loopback connect did not become writable within the timeout");
            Assert.True(socket.Connected);
        }
        else
        {
            Assert.Equal(0, result);
        }

        return clientId;
    }

    private int AcceptPending(int serverId)
    {
        _ctx[CpuRegister.Rdi] = unchecked((ulong)serverId);
        _ctx[CpuRegister.Rsi] = 0;
        _ctx[CpuRegister.Rdx] = 0;
        for (var attempt = 0; ; attempt++)
        {
            var result = NetExports.NetAccept(_ctx);
            if (result == 0)
            {
                var acceptedId = checked((int)_ctx[CpuRegister.Rax]);
                Assert.True(acceptedId > 0);
                return acceptedId;
            }

            Assert.Equal(NetErrorWouldBlock, result);
            Assert.True(attempt < 500, "accept never saw the loopback connection");
            Thread.Sleep(10);
        }
    }

    private int SendGuest(int id, ulong bufferAddress, byte[] payload)
    {
        WriteGuestBytes(bufferAddress, payload);
        _ctx[CpuRegister.Rdi] = unchecked((ulong)id);
        _ctx[CpuRegister.Rsi] = bufferAddress;
        _ctx[CpuRegister.Rdx] = unchecked((ulong)payload.Length);
        _ctx[CpuRegister.Rcx] = 0;
        return NetExports.NetSend(_ctx);
    }

    private (int Received, byte[] Data) RecvGuest(int id, ulong bufferAddress, int length)
    {
        _ctx[CpuRegister.Rdi] = unchecked((ulong)id);
        _ctx[CpuRegister.Rsi] = bufferAddress;
        _ctx[CpuRegister.Rdx] = unchecked((ulong)length);
        _ctx[CpuRegister.Rcx] = 0;
        var result = NetExports.NetRecv(_ctx);
        if (result < 0)
        {
            return (result, Array.Empty<byte>());
        }

        var data = new byte[result];
        if (result > 0)
        {
            Assert.True(_ctx.Memory.TryRead(bufferAddress, data));
        }

        return (result, data);
    }

    [Fact]
    public void LoopbackTcpPair_SendRecvRoundTripsThroughAccept()
    {
        var serverId = CreateSocket(2, 1, 6);
        var clientId = CreateSocket(2, 1, 6);
        var acceptedId = 0;
        try
        {
            BindListenLoopback(serverId, out var port);
            ConnectLoopback(clientId, port);
            acceptedId = AcceptPending(serverId);

            var payload = Encoding.ASCII.GetBytes("sharpmu loopback");
            const ulong sendBuffer = MemoryBase + 0x200;
            const ulong recvBuffer = MemoryBase + 0x300;
            Assert.Equal(payload.Length, SendGuest(clientId, sendBuffer, payload));

            var (received, data) = RecvGuest(acceptedId, recvBuffer, payload.Length);
            Assert.Equal(payload.Length, received);
            Assert.Equal(payload, data);
        }
        finally
        {
            if (acceptedId != 0)
            {
                CloseSocket(acceptedId);
            }

            CloseSocket(clientId);
            CloseSocket(serverId);
        }
    }

    [Fact]
    public void NonBlockingRecv_WithoutData_ReturnsWouldBlockAndSetsErrno()
    {
        var serverId = CreateSocket(2, 1, 6);
        var clientId = CreateSocket(2, 1, 6);
        var acceptedId = 0;
        try
        {
            BindListenLoopback(serverId, out var port);
            ConnectLoopback(clientId, port);
            acceptedId = AcceptPending(serverId);

            const ulong recvBuffer = MemoryBase + 0x300;
            var (received, _) = RecvGuest(acceptedId, recvBuffer, 16);

            // Sockets are created non-blocking, so an empty buffer is EAGAIN
            // (ORBIS_NET_ERROR_EAGAIN) with errno EWOULDBLOCK, never a wait.
            Assert.Equal(NetErrorWouldBlock, received);
            Assert.Equal(unchecked((int)0x80410123), unchecked((int)_ctx[CpuRegister.Rax]));

            _ctx[CpuRegister.Rdi] = 0;
            Assert.Equal(0, NetExports.NetErrnoLoc(_ctx));
            var errnoAddress = (nint)_ctx[CpuRegister.Rax];
            Assert.NotEqual(nint.Zero, errnoAddress);
            Assert.Equal(NetErrnoWouldBlock, System.Runtime.InteropServices.Marshal.ReadInt32(errnoAddress));
        }
        finally
        {
            if (acceptedId != 0)
            {
                CloseSocket(acceptedId);
            }

            CloseSocket(clientId);
            CloseSocket(serverId);
        }
    }

    [Fact]
    public void Poll_ReportsReadinessOnlyWhenDataArrives()
    {
        var serverId = CreateSocket(2, 1, 6);
        var clientId = CreateSocket(2, 1, 6);
        var acceptedId = 0;
        try
        {
            BindListenLoopback(serverId, out var port);
            ConnectLoopback(clientId, port);
            acceptedId = AcceptPending(serverId);

            // struct pollfd { int fd; short events; short revents; }
            const ulong pollFdAddress = MemoryBase + 0x200;
            Span<byte> pollFd = stackalloc byte[8];
            pollFd.Clear();
            System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(pollFd, acceptedId);
            System.Buffers.Binary.BinaryPrimitives.WriteInt16LittleEndian(pollFd[4..], PollIn);
            WriteGuestBytes(pollFdAddress, pollFd);

            _ctx[CpuRegister.Rdi] = pollFdAddress;
            _ctx[CpuRegister.Rsi] = 1;
            Assert.Equal(0, KernelMemoryCompatExports.PosixPoll(_ctx));
            Assert.Equal(0UL, _ctx[CpuRegister.Rax]);

            const ulong sendBuffer = MemoryBase + 0x280;
            const ulong recvBuffer = MemoryBase + 0x300;
            Assert.Equal(4, SendGuest(clientId, sendBuffer, [1, 2, 3, 4]));

            // Give the loopback stack a moment to queue the segment, then poll
            // again: the accepted socket must now report POLLIN.
            var ready = false;
            for (var attempt = 0; attempt < 200 && !ready; attempt++)
            {
                _ctx[CpuRegister.Rdi] = pollFdAddress;
                _ctx[CpuRegister.Rsi] = 1;
                Assert.Equal(0, KernelMemoryCompatExports.PosixPoll(_ctx));
                ready = _ctx[CpuRegister.Rax] == 1;
                if (!ready)
                {
                    Thread.Sleep(10);
                }
            }

            Assert.True(ready, "poll never reported the loopback data readable");
            Span<byte> updated = stackalloc byte[8];
            Assert.True(_ctx.Memory.TryRead(pollFdAddress, updated));
            Assert.Equal(PollIn, System.Buffers.Binary.BinaryPrimitives.ReadInt16LittleEndian(updated[6..]));

            // Draining the socket clears the readiness again.
            var (received, _) = RecvGuest(acceptedId, recvBuffer, 4);
            Assert.Equal(4, received);
            _ctx[CpuRegister.Rdi] = pollFdAddress;
            _ctx[CpuRegister.Rsi] = 1;
            Assert.Equal(0, KernelMemoryCompatExports.PosixPoll(_ctx));
            Assert.Equal(0UL, _ctx[CpuRegister.Rax]);
        }
        finally
        {
            if (acceptedId != 0)
            {
                CloseSocket(acceptedId);
            }

            CloseSocket(clientId);
            CloseSocket(serverId);
        }
    }

    [Fact]
    public void FcntlNonblockFlag_RoundTripsWithSocketBlockingMode()
    {
        var clientId = CreateSocket(2, 1, 6);
        try
        {
            // Sockets are born non-blocking, so F_GETFL reports O_NONBLOCK.
            _ctx[CpuRegister.Rdi] = unchecked((ulong)clientId);
            _ctx[CpuRegister.Rsi] = unchecked((ulong)FGetFl);
            _ctx[CpuRegister.Rdx] = 0;
            Assert.Equal(0, KernelMemoryCompatExports.PosixFcntl(_ctx));
            Assert.Equal(unchecked((ulong)ONonblock), _ctx[CpuRegister.Rax]);

            // Clearing O_NONBLOCK switches the socket to blocking mode, visible
            // through getsockopt(SO_NBIO) as well as a following F_GETFL.
            _ctx[CpuRegister.Rdi] = unchecked((ulong)clientId);
            _ctx[CpuRegister.Rsi] = unchecked((ulong)FSetFl);
            _ctx[CpuRegister.Rdx] = 0;
            Assert.Equal(0, KernelMemoryCompatExports.PosixFcntl(_ctx));

            _ctx[CpuRegister.Rdi] = unchecked((ulong)clientId);
            _ctx[CpuRegister.Rsi] = unchecked((ulong)FGetFl);
            _ctx[CpuRegister.Rdx] = 0;
            Assert.Equal(0, KernelMemoryCompatExports.PosixFcntl(_ctx));
            Assert.Equal(0UL, _ctx[CpuRegister.Rax]);

            const ulong valueAddress = MemoryBase + 0x200;
            const ulong lengthAddress = MemoryBase + 0x204;
            _ctx.Memory.TryWrite(lengthAddress, [4, 0, 0, 0]);
            _ctx[CpuRegister.Rdi] = unchecked((ulong)clientId);
            _ctx[CpuRegister.Rsi] = unchecked((ulong)SolSocket);
            _ctx[CpuRegister.Rdx] = unchecked((ulong)SoNbio);
            _ctx[CpuRegister.Rcx] = valueAddress;
            _ctx[CpuRegister.R8] = lengthAddress;
            Assert.Equal(0, NetExports.PosixGetsockopt(_ctx));
            Span<byte> value = stackalloc byte[4];
            Assert.True(_ctx.Memory.TryRead(valueAddress, value));
            Assert.Equal(0, System.Buffers.Binary.BinaryPrimitives.ReadInt32LittleEndian(value));

            // Re-arming O_NONBLOCK flips both views again.
            _ctx[CpuRegister.Rdi] = unchecked((ulong)clientId);
            _ctx[CpuRegister.Rsi] = unchecked((ulong)FSetFl);
            _ctx[CpuRegister.Rdx] = unchecked((ulong)ONonblock);
            Assert.Equal(0, KernelMemoryCompatExports.PosixFcntl(_ctx));

            _ctx[CpuRegister.Rdi] = unchecked((ulong)clientId);
            _ctx[CpuRegister.Rsi] = unchecked((ulong)FGetFl);
            _ctx[CpuRegister.Rdx] = 0;
            Assert.Equal(0, KernelMemoryCompatExports.PosixFcntl(_ctx));
            Assert.Equal(unchecked((ulong)ONonblock), _ctx[CpuRegister.Rax]);
        }
        finally
        {
            CloseSocket(clientId);
        }
    }

    [Fact]
    public void UdpLoopback_SendtoRecvfromReportsSourceAddress()
    {
        var udpId = CreateSocket(2, 2, 17);
        try
        {
            // UDP has no listen(); the bound ephemeral port is the destination the
            // datagram loops back to.
            BindLoopback(udpId, out var port);

            var payload = Encoding.ASCII.GetBytes("udp!");
            const ulong sendBuffer = MemoryBase + 0x200;
            const ulong sockaddrAddress = MemoryBase + 0x240;
            const ulong recvBuffer = MemoryBase + 0x280;
            const ulong fromAddress = MemoryBase + 0x2C0;
            const ulong fromLengthAddress = MemoryBase + 0x300;

            WriteGuestBytes(sendBuffer, payload);
            WriteLoopbackSockaddr(sockaddrAddress, checked((ushort)port));
            _ctx[CpuRegister.Rdi] = unchecked((ulong)udpId);
            _ctx[CpuRegister.Rsi] = sendBuffer;
            _ctx[CpuRegister.Rdx] = unchecked((ulong)payload.Length);
            _ctx[CpuRegister.Rcx] = 0;
            _ctx[CpuRegister.R8] = sockaddrAddress;
            _ctx[CpuRegister.R9] = 16;
            Assert.Equal(payload.Length, NetExports.NetSendto(_ctx));
            Assert.Equal(unchecked((ulong)payload.Length), _ctx[CpuRegister.Rax]);

            _ctx.Memory.TryWrite(fromLengthAddress, [16, 0, 0, 0]);
            _ctx[CpuRegister.Rdi] = unchecked((ulong)udpId);
            _ctx[CpuRegister.Rsi] = recvBuffer;
            _ctx[CpuRegister.Rdx] = unchecked((ulong)payload.Length);
            _ctx[CpuRegister.Rcx] = 0;
            _ctx[CpuRegister.R8] = fromAddress;
            _ctx[CpuRegister.R9] = fromLengthAddress;
            Assert.Equal(payload.Length, NetExports.NetRecvfrom(_ctx));

            var data = new byte[payload.Length];
            Assert.True(_ctx.Memory.TryRead(recvBuffer, data));
            Assert.Equal(payload, data);

            // recvfrom writes the sender sockaddr: 127.0.0.1 at the bound port.
            Span<byte> source = stackalloc byte[16];
            Assert.True(_ctx.Memory.TryRead(fromAddress, source));
            Assert.Equal(16, source[0]);
            Assert.Equal(2, source[1]);
            Assert.Equal(port, ((source[2] << 8) | source[3]));
            Assert.Equal(127, source[4]);
            Assert.Equal(0, source[5]);
            Assert.Equal(0, source[6]);
            Assert.Equal(1, source[7]);

            Span<byte> fromLength = stackalloc byte[4];
            Assert.True(_ctx.Memory.TryRead(fromLengthAddress, fromLength));
            Assert.Equal(16, System.Buffers.Binary.BinaryPrimitives.ReadInt32LittleEndian(fromLength));
        }
        finally
        {
            CloseSocket(udpId);
        }
    }

    [Fact]
    public void ResolverStartNtoa_ResolvesLocalhostToLoopback()
    {
        const ulong poolNameAddress = MemoryBase + 0x100;
        const ulong hostnameAddress = MemoryBase + 0x120;
        const ulong addressOut = MemoryBase + 0x160;

        WriteGuestCString(poolNameAddress, "loopback-test");
        _ctx[CpuRegister.Rdi] = poolNameAddress;
        _ctx[CpuRegister.Rsi] = 4096;
        _ctx[CpuRegister.Rdx] = 0;
        Assert.Equal(0, NetExports.NetPoolCreate(_ctx));
        var poolId = checked((int)_ctx[CpuRegister.Rax]);
        Assert.True(poolId > 0);

        var resolverId = 0;
        try
        {
            _ctx[CpuRegister.Rdi] = poolNameAddress;
            _ctx[CpuRegister.Rsi] = unchecked((ulong)poolId);
            _ctx[CpuRegister.Rdx] = 0;
            Assert.Equal(0, NetExports.NetResolverCreate(_ctx));
            resolverId = checked((int)_ctx[CpuRegister.Rax]);
            Assert.True(resolverId > 0);

            WriteGuestCString(hostnameAddress, "localhost");
            _ctx[CpuRegister.Rdi] = unchecked((ulong)resolverId);
            _ctx[CpuRegister.Rsi] = hostnameAddress;
            _ctx[CpuRegister.Rdx] = addressOut;
            _ctx[CpuRegister.Rcx] = 0;
            _ctx[CpuRegister.R8] = 0;
            _ctx[CpuRegister.R9] = 0;
            Assert.Equal(0, NetExports.NetResolverStartNtoa(_ctx));

            Span<byte> address = stackalloc byte[4];
            Assert.True(_ctx.Memory.TryRead(addressOut, address));
            Assert.Equal(127, address[0]);
            Assert.Equal(0, address[1]);
            Assert.Equal(0, address[2]);
            Assert.Equal(1, address[3]);
        }
        finally
        {
            if (resolverId != 0)
            {
                _ctx[CpuRegister.Rdi] = unchecked((ulong)resolverId);
                Assert.Equal(0, NetExports.NetResolverDestroy(_ctx));
            }

            _ctx[CpuRegister.Rdi] = unchecked((ulong)poolId);
            Assert.Equal(0, NetExports.NetPoolDestroy(_ctx));
        }
    }

    [Fact]
    public void ShutdownWriteSide_MakesPeerRecvReturnZero()
    {
        var serverId = CreateSocket(2, 1, 6);
        var clientId = CreateSocket(2, 1, 6);
        var acceptedId = 0;
        try
        {
            BindListenLoopback(serverId, out var port);
            ConnectLoopback(clientId, port);
            acceptedId = AcceptPending(serverId);

            // SHUT_WR on the client side is the POSIX half-close; the peer's recv
            // then observes EOF (0) instead of blocking or EAGAIN.
            _ctx[CpuRegister.Rdi] = unchecked((ulong)clientId);
            _ctx[CpuRegister.Rsi] = 1;
            Assert.Equal(0, NetExports.NetShutdown(_ctx));

            const ulong recvBuffer = MemoryBase + 0x300;
            var (received, _) = RecvGuest(acceptedId, recvBuffer, 16);
            Assert.Equal(0, received);
            Assert.Equal(0UL, _ctx[CpuRegister.Rax]);
        }
        finally
        {
            if (acceptedId != 0)
            {
                CloseSocket(acceptedId);
            }

            CloseSocket(clientId);
            CloseSocket(serverId);
        }
    }
}
