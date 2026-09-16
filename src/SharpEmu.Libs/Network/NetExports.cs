// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Text;
using SharpEmu.HLE;
using SharpEmu.Libs.Kernel;

namespace SharpEmu.Libs.Network;

public static class NetExports
{
    private const int NetErrorBadFileDescriptor = unchecked((int)0x80410109);
    private const int NetErrorInvalidArgument = unchecked((int)0x80410116);
    private const int NetErrorWouldBlock = unchecked((int)0x80410123);
    private const int NetErrorInProgress = unchecked((int)0x80410124);
    private const int NetErrorNotConnected = unchecked((int)0x80410139);
    private const int NetErrorConnectionRefused = unchecked((int)0x8041013D);
    private const int NetErrorAddressInUse = unchecked((int)0x80410130);
    private const int NetErrorNotInitialized = unchecked((int)0x804101C8);
    private const int NetErrnoBadFileDescriptor = 9;
    private const int NetErrnoInvalidArgument = 22;
    private const int NetErrnoWouldBlock = 35;
    private const int NetErrnoInProgress = 36;
    private const int NetErrnoNotConnected = 57;
    private const int NetErrnoConnectionRefused = 61;
    private const int NetErrnoAddressInUse = 48;
    private const int NetErrnoNotInitialized = 200;
    private const int MaxNameLength = 256;

    // ORBIS_NET_MSG_* message flags (FreeBSD numbering used by the console libc).
    private const int MessageFlagPeek = 0x2;
    private const int MessageFlagDontRoute = 0x4;
    private const int MessageFlagDontWait = 0x80;

    // sizeof(sockaddr_in) / sizeof(sockaddr_in6) in the PS5 sa_len-first layout
    // TryReadSocketAddress parses and WriteSocketAddressResult produces.
    private const int SocketAddressSizeIPv4 = 16;
    private const int SocketAddressSizeIPv6 = 28;

    private static readonly ConcurrentDictionary<int, NetPool> _pools = new();
    private static readonly ConcurrentDictionary<int, ResolverContext> _resolvers = new();
    private static readonly ConcurrentDictionary<int, Socket> _sockets = new();
    private static int _nextPoolId;
    private static int _nextResolverId = 0x2000;
    private static int _nextSocketId = 0x4000;
    // The platform networking module is usable immediately after it is loaded.
    // Games and middleware (notably FMOD) can create internal sockets before an
    // explicit sceNetInit call reaches application code.
    private static bool _initialized = true;

    // Upper bound for one resolver call: the console timeout parameter's unit is not
    // modeled 1:1, but a bounded wait keeps a dead host resolver from pinning the
    // guest thread forever while the common path completes immediately.
    private static readonly TimeSpan ResolveTimeout = TimeSpan.FromSeconds(30);

    [ThreadStatic]
    private static nint _errnoAddress;

    private sealed record NetPool(string Name, int Size, int Flags);

    private sealed class ResolverContext
    {
        public ResolverContext(string name, int poolId, int flags)
        {
            Name = name;
            PoolId = poolId;
            Flags = flags;
        }

        public string Name { get; }

        public int PoolId { get; }

        public int Flags { get; }

        // Set by the resolver start exports so sceNetResolverGetError reports the
        // outcome of the most recent lookup.
        public int LastError { get; set; }
    }

    [SysAbiExport(
        Nid = "Nlev7Lg8k3A",
        ExportName = "sceNetInit",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceNet")]
    public static int NetInit(CpuContext ctx)
    {
        _initialized = true;
        TraceNet("init", 0, 0, 0, 0);
        return ctx.SetReturn(0);
    }

    [SysAbiExport(
        Nid = "cTGkc6-TBlI",
        ExportName = "sceNetTerm",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceNet")]
    public static int NetTerm(CpuContext ctx)
    {
        _initialized = false;
        _pools.Clear();
        _resolvers.Clear();
        foreach (var socket in _sockets.Values)
        {
            socket.Dispose();
        }
        _sockets.Clear();
        TraceNet("term", 0, 0, 0, 0);
        return ctx.SetReturn(0);
    }

    [SysAbiExport(
        Nid = "Q4qBuN-c0ZM",
        ExportName = "sceNetSocket",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceNet")]
    public static int NetSocket(CpuContext ctx)
    {
        if (!_initialized)
        {
            return SetNetError(ctx, NetErrorNotInitialized, NetErrnoNotInitialized);
        }

        var nameAddress = ctx[CpuRegister.Rdi];
        var family = unchecked((int)ctx[CpuRegister.Rsi]);
        var type = unchecked((int)ctx[CpuRegister.Rdx]);
        var protocol = unchecked((int)ctx[CpuRegister.Rcx]);
        var name = TryReadUtf8Z(ctx, nameAddress, MaxNameLength, out var value)
            ? value
            : string.Empty;

        if (!TryTranslateSocketParameters(family, type, protocol, out var addressFamily, out var socketType, out var protocolType))
        {
            return SetNetError(ctx, NetErrorInvalidArgument, NetErrnoInvalidArgument);
        }

        try
        {
            var socket = new Socket(addressFamily, socketType, protocolType);
            // Console sockets start blocking, but a blocking host socket pins the
            // guest thread inside a host kernel call the emulator cannot interrupt.
            // Create every socket non-blocking instead: poll/select-driven games
            // observe WouldBlock exactly as they would for an O_NONBLOCK descriptor,
            // and a game can still opt back into blocking waits with
            // setsockopt(SO_NBIO, 0) or fcntl(F_SETFL) without O_NONBLOCK.
            socket.Blocking = false;
            var id = Interlocked.Increment(ref _nextSocketId);
            _sockets[id] = socket;
            TraceNet("socket.create", id, unchecked((ulong)family), unchecked((ulong)type), unchecked((ulong)protocol));
            ctx[CpuRegister.Rax] = unchecked((ulong)id);
            return (int)OrbisGen2Result.ORBIS_GEN2_OK;
        }
        catch (SocketException)
        {
            return SetNetError(ctx, NetErrorInvalidArgument, NetErrnoInvalidArgument);
        }
    }

    [SysAbiExport(
        Nid = "45ggEzakPJQ",
        ExportName = "sceNetSocketClose",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceNet")]
    public static int NetSocketClose(CpuContext ctx)
    {
        var id = unchecked((int)ctx[CpuRegister.Rdi]);
        if (!_sockets.TryRemove(id, out var socket))
        {
            return SetNetError(ctx, NetErrorBadFileDescriptor, NetErrnoBadFileDescriptor);
        }

        socket.Dispose();
        TraceNet("socket.close", id, 0, 0, 0);
        return ctx.SetReturn(0);
    }

    [SysAbiExport(
        Nid = "2mKX2Spso7I",
        ExportName = "sceNetSetsockopt",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceNet")]
    public static int NetSetsockopt(CpuContext ctx)
    {
        var id = unchecked((int)ctx[CpuRegister.Rdi]);
        var level = unchecked((int)ctx[CpuRegister.Rsi]);
        var option = unchecked((int)ctx[CpuRegister.Rdx]);
        var valueAddress = ctx[CpuRegister.Rcx];
        var valueLength = unchecked((int)ctx[CpuRegister.R8]);
        if (!_sockets.TryGetValue(id, out var socket))
        {
            return SetNetError(ctx, NetErrorBadFileDescriptor, NetErrnoBadFileDescriptor);
        }

        // ORBIS_NET_SOL_SOCKET / ORBIS_NET_SO_NBIO. This is the first option
        // used by FMOD's discovery socket and maps directly to host blocking.
        if (level == 0xFFFF && option == 0x1200)
        {
            Span<byte> value = stackalloc byte[sizeof(int)];
            if (valueLength < value.Length || valueAddress == 0 || !ctx.Memory.TryRead(valueAddress, value))
            {
                return SetNetError(ctx, NetErrorInvalidArgument, NetErrnoInvalidArgument);
            }

            socket.Blocking = BinaryPrimitives.ReadInt32LittleEndian(value) == 0;
            TraceNet("socket.nonblocking", id, socket.Blocking ? 0UL : 1UL, 0, 0);
            return ctx.SetReturn(0);
        }

        // ORBIS_NET_SO_REUSEADDR uses the BSD value 0x0004.
        if (level == 0xFFFF && option == 0x0004)
        {
            Span<byte> value = stackalloc byte[sizeof(int)];
            if (valueLength < value.Length || valueAddress == 0 || !ctx.Memory.TryRead(valueAddress, value))
            {
                return SetNetError(ctx, NetErrorInvalidArgument, NetErrnoInvalidArgument);
            }

            socket.SetSocketOption(
                SocketOptionLevel.Socket,
                SocketOptionName.ReuseAddress,
                BinaryPrimitives.ReadInt32LittleEndian(value) != 0);
            TraceNet("socket.reuseaddr", id, BinaryPrimitives.ReadUInt32LittleEndian(value), 0, 0);
            return ctx.SetReturn(0);
        }

        return SetNetError(ctx, NetErrorInvalidArgument, NetErrnoInvalidArgument);
    }

    /// <summary>
    /// POSIX alias of <see cref="NetSetsockopt"/>; identical
    /// (fd, level, option, value, length) argument order.
    /// </summary>
    [SysAbiExport(
        Nid = "fFxGkxF2bVo",
        ExportName = "setsockopt",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libKernel")]
    public static int PosixSetsockopt(CpuContext ctx) => NetSetsockopt(ctx);

    /// <summary>
    /// Reads back the socket options this backend actually tracks: SO_NBIO,
    /// SO_REUSEADDR and SO_ERROR.
    /// </summary>
    /// <remarks>
    /// Anything else returns EINVAL rather than a zero-filled buffer. A caller
    /// that receives success for an option nobody stored would treat whatever
    /// happens to be in its output buffer as the real setting, which is a harder
    /// failure to trace than an explicit rejection.
    /// </remarks>
    [SysAbiExport(
        Nid = "6O8EwYOgH9Y",
        ExportName = "getsockopt",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libKernel")]
    public static int PosixGetsockopt(CpuContext ctx)
    {
        var id = unchecked((int)ctx[CpuRegister.Rdi]);
        var level = unchecked((int)ctx[CpuRegister.Rsi]);
        var option = unchecked((int)ctx[CpuRegister.Rdx]);
        var valueAddress = ctx[CpuRegister.Rcx];
        var lengthAddress = ctx[CpuRegister.R8];
        if (!_sockets.TryGetValue(id, out var socket))
        {
            return SetNetError(ctx, NetErrorBadFileDescriptor, NetErrnoBadFileDescriptor);
        }

        if (valueAddress == 0 || lengthAddress == 0 || level != 0xFFFF)
        {
            return SetNetError(ctx, NetErrorInvalidArgument, NetErrnoInvalidArgument);
        }

        Span<byte> lengthBytes = stackalloc byte[sizeof(int)];
        if (!ctx.Memory.TryRead(lengthAddress, lengthBytes))
        {
            return SetNetError(ctx, NetErrorInvalidArgument, NetErrnoInvalidArgument);
        }

        if (BinaryPrimitives.ReadInt32LittleEndian(lengthBytes) < sizeof(int))
        {
            return SetNetError(ctx, NetErrorInvalidArgument, NetErrnoInvalidArgument);
        }

        int value;
        switch (option)
        {
            // ORBIS_NET_SO_NBIO: mirrors what sceNetSetsockopt stored.
            case 0x1200:
                value = socket.Blocking ? 0 : 1;
                break;
            case 0x0004:
                value = (int)socket.GetSocketOption(
                    SocketOptionLevel.Socket,
                    SocketOptionName.ReuseAddress)! != 0 ? 1 : 0;
                break;
            // ORBIS_NET_SO_ERROR: surface the live per-socket error (the query
            // clears it, matching getsockopt(SO_ERROR)) so callers driving a
            // non-blocking connect can observe EINPROGRESS completing.
            case 0x1007:
                try
                {
                    value = socket.GetSocketOption(SocketOptionLevel.Socket, SocketOptionName.Error) is int pendingError
                        ? pendingError
                        : 0;
                }
                catch (SocketException)
                {
                    value = 0;
                }

                break;
            default:
                return SetNetError(ctx, NetErrorInvalidArgument, NetErrnoInvalidArgument);
        }

        Span<byte> valueBytes = stackalloc byte[sizeof(int)];
        BinaryPrimitives.WriteInt32LittleEndian(valueBytes, value);
        BinaryPrimitives.WriteInt32LittleEndian(lengthBytes, sizeof(int));
        if (!ctx.Memory.TryWrite(valueAddress, valueBytes) ||
            !ctx.Memory.TryWrite(lengthAddress, lengthBytes))
        {
            return SetNetError(ctx, NetErrorInvalidArgument, NetErrnoInvalidArgument);
        }

        TraceNet("socket.getsockopt", id, unchecked((uint)option), unchecked((uint)value), 0);
        return ctx.SetReturn(0);
    }

    [SysAbiExport(
        Nid = "fZOeZIOEmLw",
        ExportName = "send",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libKernel")]
    public static int PosixSend(CpuContext ctx)
    {
        var id = unchecked((int)ctx[CpuRegister.Rdi]);
        var bufferAddress = ctx[CpuRegister.Rsi];
        var length = unchecked((int)ctx[CpuRegister.Rdx]);
        if (!_sockets.TryGetValue(id, out var socket))
        {
            return SetNetError(ctx, NetErrorBadFileDescriptor, NetErrnoBadFileDescriptor);
        }

        if (length < 0 || (length != 0 && bufferAddress == 0))
        {
            return SetNetError(ctx, NetErrorInvalidArgument, NetErrnoInvalidArgument);
        }

        if (length == 0)
        {
            return ctx.SetReturn(0);
        }

        var payload = new byte[length];
        if (!ctx.Memory.TryRead(bufferAddress, payload))
        {
            return SetNetError(ctx, NetErrorInvalidArgument, NetErrnoInvalidArgument);
        }

        try
        {
            var sent = socket.Send(payload, SocketFlags.None);
            TraceNet("socket.send", id, unchecked((uint)length), unchecked((uint)sent), 0);
            return ctx.SetReturn(sent);
        }
        catch (SocketException exception)
            when (exception.SocketErrorCode == SocketError.WouldBlock)
        {
            return SetNetError(ctx, NetErrorWouldBlock, NetErrnoWouldBlock);
        }
        catch (SocketException)
        {
            return SetNetError(ctx, NetErrorInvalidArgument, NetErrnoInvalidArgument);
        }
        catch (ObjectDisposedException)
        {
            return SetNetError(ctx, NetErrorBadFileDescriptor, NetErrnoBadFileDescriptor);
        }
    }

    /// <summary>
    /// libSceNet alias of <see cref="PosixSend"/>; identical (fd, buffer, length, flags)
    /// argument order.
    /// </summary>
    [SysAbiExport(
        Nid = "beRjXBn-z+o",
        ExportName = "sceNetSend",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceNet")]
    public static int NetSend(CpuContext ctx) => PosixSend(ctx);

    [SysAbiExport(
        Nid = "Ez8xjo9UF4E",
        ExportName = "recv",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libKernel")]
    public static int PosixRecv(CpuContext ctx)
    {
        var id = unchecked((int)ctx[CpuRegister.Rdi]);
        var bufferAddress = ctx[CpuRegister.Rsi];
        var length = unchecked((int)ctx[CpuRegister.Rdx]);
        var flags = unchecked((int)ctx[CpuRegister.Rcx]);
        if (!_sockets.TryGetValue(id, out var socket))
        {
            return SetNetError(ctx, NetErrorBadFileDescriptor, NetErrnoBadFileDescriptor);
        }

        if (length < 0 || (length != 0 && bufferAddress == 0))
        {
            return SetNetError(ctx, NetErrorInvalidArgument, NetErrnoInvalidArgument);
        }

        if (length == 0)
        {
            return ctx.SetReturn(0);
        }

        var payload = new byte[length];
        try
        {
            var socketFlags = TranslateSocketFlags(flags);
            // Sockets are created non-blocking (see NetSocket); only a socket the
            // game explicitly switched back to blocking waits for data, and
            // MSG_DONTWAIT forces the non-blocking path for a single call.
            if (socket.Blocking && (flags & MessageFlagDontWait) == 0)
            {
                socket.Poll(-1, SelectMode.SelectRead);
            }

            var received = socket.Receive(payload, socketFlags);
            if (received > 0 && !ctx.Memory.TryWrite(bufferAddress, payload.AsSpan(0, received)))
            {
                return SetNetError(ctx, NetErrorInvalidArgument, NetErrnoInvalidArgument);
            }

            TraceNet("socket.recv", id, unchecked((uint)length), unchecked((uint)received), 0);
            return ctx.SetReturn(received);
        }
        catch (SocketException exception)
            when (exception.SocketErrorCode is SocketError.WouldBlock or SocketError.IOPending)
        {
            return SetNetError(ctx, NetErrorWouldBlock, NetErrnoWouldBlock);
        }
        catch (SocketException exception) when (exception.SocketErrorCode == SocketError.NotConnected)
        {
            return SetNetError(ctx, NetErrorNotConnected, NetErrnoNotConnected);
        }
        catch (SocketException)
        {
            return SetNetError(ctx, NetErrorInvalidArgument, NetErrnoInvalidArgument);
        }
        catch (ObjectDisposedException)
        {
            return SetNetError(ctx, NetErrorBadFileDescriptor, NetErrnoBadFileDescriptor);
        }
    }

    /// <summary>
    /// libSceNet alias of <see cref="PosixRecv"/>; identical (fd, buffer, length, flags)
    /// argument order.
    /// </summary>
    [SysAbiExport(
        Nid = "9wO9XrMsNhc",
        ExportName = "sceNetRecv",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceNet")]
    public static int NetRecv(CpuContext ctx) => PosixRecv(ctx);

    [SysAbiExport(
        Nid = "lUk6wrGXyMw",
        ExportName = "recvfrom",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libKernel")]
    public static int PosixRecvfrom(CpuContext ctx)
    {
        var id = unchecked((int)ctx[CpuRegister.Rdi]);
        var bufferAddress = ctx[CpuRegister.Rsi];
        var length = unchecked((int)ctx[CpuRegister.Rdx]);
        var flags = unchecked((int)ctx[CpuRegister.Rcx]);
        var fromAddress = ctx[CpuRegister.R8];
        var fromLengthAddress = ctx[CpuRegister.R9];
        if (!_sockets.TryGetValue(id, out var socket))
        {
            return SetNetError(ctx, NetErrorBadFileDescriptor, NetErrnoBadFileDescriptor);
        }

        if (length < 0 || (length != 0 && bufferAddress == 0))
        {
            return SetNetError(ctx, NetErrorInvalidArgument, NetErrnoInvalidArgument);
        }

        if (length == 0)
        {
            return ctx.SetReturn(0);
        }

        var fromCapacity = 0;
        if (fromAddress != 0)
        {
            // The source-address length is an in/out value; read the capacity the
            // caller provided before receiving so the sockaddr is never truncated.
            if (fromLengthAddress == 0 ||
                !ctx.TryReadUInt32(fromLengthAddress, out var fromLength) ||
                fromLength == 0 ||
                fromLength > int.MaxValue)
            {
                return SetNetError(ctx, NetErrorInvalidArgument, NetErrnoInvalidArgument);
            }

            fromCapacity = unchecked((int)fromLength);
        }

        var payload = new byte[length];
        EndPoint source = socket.AddressFamily == AddressFamily.InterNetworkV6
            ? new IPEndPoint(IPAddress.IPv6Any, 0)
            : new IPEndPoint(IPAddress.Any, 0);
        try
        {
            var socketFlags = TranslateSocketFlags(flags);
            if (socket.Blocking && (flags & MessageFlagDontWait) == 0)
            {
                socket.Poll(-1, SelectMode.SelectRead);
            }

            var received = socket.ReceiveFrom(payload, socketFlags, ref source);
            if (received > 0 && !ctx.Memory.TryWrite(bufferAddress, payload.AsSpan(0, received)))
            {
                return SetNetError(ctx, NetErrorInvalidArgument, NetErrnoInvalidArgument);
            }

            if (fromAddress != 0 && source is IPEndPoint sourceEndpoint)
            {
                WriteSocketAddressResult(ctx, fromAddress, fromLengthAddress, fromCapacity, sourceEndpoint);
            }

            TraceNet(
                "socket.recvfrom",
                id,
                unchecked((uint)length),
                unchecked((uint)received),
                (ulong)((source as IPEndPoint)?.Port ?? 0));
            return ctx.SetReturn(received);
        }
        catch (SocketException exception)
            when (exception.SocketErrorCode is SocketError.WouldBlock or SocketError.IOPending)
        {
            return SetNetError(ctx, NetErrorWouldBlock, NetErrnoWouldBlock);
        }
        catch (SocketException exception) when (exception.SocketErrorCode == SocketError.NotConnected)
        {
            return SetNetError(ctx, NetErrorNotConnected, NetErrnoNotConnected);
        }
        catch (SocketException)
        {
            return SetNetError(ctx, NetErrorInvalidArgument, NetErrnoInvalidArgument);
        }
        catch (ObjectDisposedException)
        {
            return SetNetError(ctx, NetErrorBadFileDescriptor, NetErrnoBadFileDescriptor);
        }
    }

    /// <summary>
    /// libSceNet alias of <see cref="PosixRecvfrom"/>; identical
    /// (fd, buffer, length, flags, from, fromlen) argument order.
    /// </summary>
    [SysAbiExport(
        Nid = "304ooNZxWDY",
        ExportName = "sceNetRecvfrom",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceNet")]
    public static int NetRecvfrom(CpuContext ctx) => PosixRecvfrom(ctx);

    [SysAbiExport(
        Nid = "oBr313PppNE",
        ExportName = "sendto",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libKernel")]
    public static int PosixSendto(CpuContext ctx)
    {
        var id = unchecked((int)ctx[CpuRegister.Rdi]);
        var bufferAddress = ctx[CpuRegister.Rsi];
        var length = unchecked((int)ctx[CpuRegister.Rdx]);
        var flags = unchecked((int)ctx[CpuRegister.Rcx]);
        var toAddress = ctx[CpuRegister.R8];
        var toLength = unchecked((int)ctx[CpuRegister.R9]);
        if (!_sockets.TryGetValue(id, out var socket))
        {
            return SetNetError(ctx, NetErrorBadFileDescriptor, NetErrnoBadFileDescriptor);
        }

        if (length < 0 || (length != 0 && bufferAddress == 0))
        {
            return SetNetError(ctx, NetErrorInvalidArgument, NetErrnoInvalidArgument);
        }

        if (length == 0)
        {
            return ctx.SetReturn(0);
        }

        var payload = new byte[length];
        if (!ctx.Memory.TryRead(bufferAddress, payload))
        {
            return SetNetError(ctx, NetErrorInvalidArgument, NetErrnoInvalidArgument);
        }

        IPEndPoint? destination = null;
        if (toAddress != 0 && !TryReadSocketAddress(ctx, toAddress, toLength, out destination))
        {
            return SetNetError(ctx, NetErrorInvalidArgument, NetErrnoInvalidArgument);
        }

        try
        {
            var socketFlags = TranslateSocketFlags(flags);
            // A null destination falls back to the connected-socket path, matching
            // sendto(2) on an already-connected descriptor.
            var sent = destination is null
                ? socket.Send(payload, socketFlags)
                : socket.SendTo(payload, socketFlags, destination);
            TraceNet(
                "socket.sendto",
                id,
                unchecked((uint)length),
                unchecked((uint)sent),
                destination is null ? 0UL : (ulong)destination.Port);
            return ctx.SetReturn(sent);
        }
        catch (SocketException exception)
            when (exception.SocketErrorCode is SocketError.WouldBlock or SocketError.IOPending)
        {
            return SetNetError(ctx, NetErrorWouldBlock, NetErrnoWouldBlock);
        }
        catch (SocketException exception) when (exception.SocketErrorCode == SocketError.NotConnected)
        {
            return SetNetError(ctx, NetErrorNotConnected, NetErrnoNotConnected);
        }
        catch (SocketException)
        {
            return SetNetError(ctx, NetErrorInvalidArgument, NetErrnoInvalidArgument);
        }
        catch (ObjectDisposedException)
        {
            return SetNetError(ctx, NetErrorBadFileDescriptor, NetErrnoBadFileDescriptor);
        }
    }

    /// <summary>
    /// libSceNet alias of <see cref="PosixSendto"/>; identical
    /// (fd, buffer, length, flags, to, tolen) argument order.
    /// </summary>
    [SysAbiExport(
        Nid = "gvD1greCu0A",
        ExportName = "sceNetSendto",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceNet")]
    public static int NetSendto(CpuContext ctx) => PosixSendto(ctx);

    [SysAbiExport(
        Nid = "XVL8So3QJUk",
        ExportName = "connect",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libKernel")]
    public static int PosixConnect(CpuContext ctx)
    {
        var id = unchecked((int)ctx[CpuRegister.Rdi]);
        if (!_sockets.TryGetValue(id, out var socket))
        {
            // Descriptors from the libKernel "socket" export live in a separate fd
            // space served by the TCP compat backend (which also applies
            // SHARPEMU_NET_REDIRECT and the outbound policy); hand those over instead
            // of failing with EBADF.
            return KernelSocketCompatExports.Connect(ctx);
        }

        if (!TryReadSocketAddress(ctx, ctx[CpuRegister.Rsi], unchecked((int)ctx[CpuRegister.Rdx]), out var endpoint))
        {
            return SetNetError(ctx, NetErrorInvalidArgument, NetErrnoInvalidArgument);
        }

        try
        {
            socket.Connect(endpoint);
            TraceNet("socket.connect", id, unchecked((ulong)endpoint.Port), socket.Blocking ? 1UL : 0UL, 0);
            return ctx.SetReturn(0);
        }
        catch (SocketException exception)
            when (exception.SocketErrorCode is SocketError.WouldBlock or SocketError.InProgress or SocketError.IOPending)
        {
            // A connect that cannot complete immediately on a non-blocking descriptor
            // reports EINPROGRESS on BSD, not EWOULDBLOCK; callers finish the
            // handshake by polling for writability and checking SO_ERROR.
            TraceNet("socket.connect.pending", id, unchecked((ulong)endpoint.Port), 0, 0);
            return SetNetError(ctx, NetErrorInProgress, NetErrnoInProgress);
        }
        catch (SocketException exception) when (exception.SocketErrorCode == SocketError.ConnectionRefused)
        {
            return SetNetError(ctx, NetErrorConnectionRefused, NetErrnoConnectionRefused);
        }
        catch (SocketException)
        {
            return SetNetError(ctx, NetErrorInvalidArgument, NetErrnoInvalidArgument);
        }
        catch (ObjectDisposedException)
        {
            return SetNetError(ctx, NetErrorBadFileDescriptor, NetErrnoBadFileDescriptor);
        }
    }

    /// <summary>
    /// libSceNet alias of <see cref="PosixConnect"/>; identical (fd, sockaddr, addrlen)
    /// argument order.
    /// </summary>
    [SysAbiExport(
        Nid = "OXXX4mUk3uk",
        ExportName = "sceNetConnect",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceNet")]
    public static int NetConnect(CpuContext ctx) => PosixConnect(ctx);

    [SysAbiExport(
        Nid = "TUuiYS2kE8s",
        ExportName = "shutdown",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libKernel")]
    public static int PosixShutdown(CpuContext ctx)
    {
        var id = unchecked((int)ctx[CpuRegister.Rdi]);
        var how = unchecked((int)ctx[CpuRegister.Rsi]);
        if (!_sockets.TryGetValue(id, out var socket))
        {
            return SetNetError(ctx, NetErrorBadFileDescriptor, NetErrnoBadFileDescriptor);
        }

        SocketShutdown? shutdown = how switch
        {
            0 => SocketShutdown.Receive, // SHUT_RD
            1 => SocketShutdown.Send,    // SHUT_WR
            2 => SocketShutdown.Both,    // SHUT_RDWR
            _ => null,
        };
        if (shutdown is null)
        {
            return SetNetError(ctx, NetErrorInvalidArgument, NetErrnoInvalidArgument);
        }

        try
        {
            socket.Shutdown(shutdown.Value);
            TraceNet("socket.shutdown", id, unchecked((ulong)how), 0, 0);
            return ctx.SetReturn(0);
        }
        catch (SocketException exception) when (exception.SocketErrorCode == SocketError.NotConnected)
        {
            return SetNetError(ctx, NetErrorNotConnected, NetErrnoNotConnected);
        }
        catch (SocketException)
        {
            return SetNetError(ctx, NetErrorInvalidArgument, NetErrnoInvalidArgument);
        }
        catch (ObjectDisposedException)
        {
            return SetNetError(ctx, NetErrorBadFileDescriptor, NetErrnoBadFileDescriptor);
        }
    }

    /// <summary>
    /// libSceNet alias of <see cref="PosixShutdown"/>; identical (fd, how) argument order.
    /// </summary>
    [SysAbiExport(
        Nid = "TSM6whtekok",
        ExportName = "sceNetShutdown",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceNet")]
    public static int NetShutdown(CpuContext ctx) => PosixShutdown(ctx);

    /// <summary>
    /// Formats a binary address as text. Pure conversion with no socket state,
    /// so it behaves identically to the console version for AF_INET/AF_INET6.
    /// </summary>
    [SysAbiExport(
        Nid = "5jRCs2axtr4",
        ExportName = "inet_ntop",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libKernel")]
    public static int PosixInetNtop(CpuContext ctx)
    {
        var family = unchecked((int)ctx[CpuRegister.Rdi]);
        var sourceAddress = ctx[CpuRegister.Rsi];
        var destinationAddress = ctx[CpuRegister.Rdx];
        var destinationSize = unchecked((int)ctx[CpuRegister.Rcx]);
        if (sourceAddress == 0 || destinationAddress == 0 || destinationSize <= 0)
        {
            ctx[CpuRegister.Rax] = 0;
            return (int)OrbisGen2Result.ORBIS_GEN2_ERROR_INVALID_ARGUMENT;
        }

        // ORBIS_NET_AF_INET / ORBIS_NET_AF_INET6, matching TryMapAddressFamily.
        var addressLength = family switch
        {
            2 => 4,
            28 => 16,
            _ => 0,
        };

        if (addressLength == 0)
        {
            ctx[CpuRegister.Rax] = 0;
            return (int)OrbisGen2Result.ORBIS_GEN2_ERROR_INVALID_ARGUMENT;
        }

        var rawAddress = new byte[addressLength];
        if (!ctx.Memory.TryRead(sourceAddress, rawAddress))
        {
            ctx[CpuRegister.Rax] = 0;
            return (int)OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT;
        }

        var text = new IPAddress(rawAddress).ToString();
        var encoded = Encoding.ASCII.GetBytes(text);

        // POSIX requires the terminator to fit as well; a truncated address string
        // is worse than a reported failure because the caller cannot detect it.
        if (encoded.Length + 1 > destinationSize)
        {
            ctx[CpuRegister.Rax] = 0;
            return (int)OrbisGen2Result.ORBIS_GEN2_ERROR_INVALID_ARGUMENT;
        }

        var buffer = new byte[encoded.Length + 1];
        encoded.CopyTo(buffer, 0);
        if (!ctx.Memory.TryWrite(destinationAddress, buffer))
        {
            ctx[CpuRegister.Rax] = 0;
            return (int)OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT;
        }

        // inet_ntop returns the destination pointer on success.
        ctx[CpuRegister.Rax] = destinationAddress;
        return (int)OrbisGen2Result.ORBIS_GEN2_OK;
    }

    [SysAbiExport(
        Nid = "bErx49PgxyY",
        ExportName = "sceNetBind",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceNet")]
    public static int NetBind(CpuContext ctx)
    {
        var id = unchecked((int)ctx[CpuRegister.Rdi]);
        if (!_sockets.TryGetValue(id, out var socket))
        {
            return SetNetError(ctx, NetErrorBadFileDescriptor, NetErrnoBadFileDescriptor);
        }
        if (!TryReadSocketAddress(ctx, ctx[CpuRegister.Rsi], unchecked((int)ctx[CpuRegister.Rdx]), out var endpoint))
        {
            return SetNetError(ctx, NetErrorInvalidArgument, NetErrnoInvalidArgument);
        }

        try
        {
            socket.Bind(endpoint);
            TraceNet("socket.bind", id, unchecked((ulong)endpoint.Port), 0, 0);
            return ctx.SetReturn(0);
        }
        catch (SocketException ex) when (ex.SocketErrorCode == SocketError.AddressAlreadyInUse)
        {
            return SetNetError(ctx, NetErrorAddressInUse, NetErrnoAddressInUse);
        }
        catch (SocketException)
        {
            return SetNetError(ctx, NetErrorInvalidArgument, NetErrnoInvalidArgument);
        }
    }

    [SysAbiExport(
        Nid = "kOj1HiAGE54",
        ExportName = "sceNetListen",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceNet")]
    public static int NetListen(CpuContext ctx)
    {
        var id = unchecked((int)ctx[CpuRegister.Rdi]);
        if (!_sockets.TryGetValue(id, out var socket))
        {
            return SetNetError(ctx, NetErrorBadFileDescriptor, NetErrnoBadFileDescriptor);
        }

        try
        {
            socket.Listen(Math.Max(0, unchecked((int)ctx[CpuRegister.Rsi])));
            TraceNet("socket.listen", id, ctx[CpuRegister.Rsi], 0, 0);
            return ctx.SetReturn(0);
        }
        catch (SocketException)
        {
            return SetNetError(ctx, NetErrorInvalidArgument, NetErrnoInvalidArgument);
        }
    }

    /// <summary>
    /// POSIX alias of <see cref="NetBind"/>; identical (fd, sockaddr*, addrlen)
    /// argument order.
    /// </summary>
    [SysAbiExport(
        Nid = "KuOmgKoqCdY",
        ExportName = "bind",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libKernel")]
    public static int PosixBind(CpuContext ctx)
    {
        // Descriptors from the libKernel "socket" export live in a separate fd space;
        // hand those to the TCP compat backend instead of failing with EBADF.
        if (!_sockets.ContainsKey(unchecked((int)ctx[CpuRegister.Rdi])))
        {
            return KernelSocketCompatExports.Bind(ctx);
        }

        return NetBind(ctx);
    }

    /// <summary>
    /// POSIX alias of <see cref="NetListen"/>; identical (fd, backlog) argument order.
    /// </summary>
    [SysAbiExport(
        Nid = "pxnCmagrtao",
        ExportName = "listen",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libKernel")]
    public static int PosixListen(CpuContext ctx) => NetListen(ctx);

    [SysAbiExport(
        Nid = "PIWqhn9oSxc",
        ExportName = "sceNetAccept",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceNet")]
    public static int NetAccept(CpuContext ctx)
    {
        // sceNetAccept takes the same (fd, sockaddr*, socklen_t*) shape as POSIX
        // accept, but its address outputs are best-effort: keep accepting even when
        // the caller passed unusable buffers.
        return AcceptGuestSocket(
            ctx,
            unchecked((int)ctx[CpuRegister.Rdi]),
            ctx[CpuRegister.Rsi],
            ctx[CpuRegister.Rdx],
            strictAddressArguments: false);
    }

    /// <summary>
    /// POSIX accept with strict address validation: a non-null address paired with an
    /// unusable length pointer fails with EINVAL instead of being ignored.
    /// </summary>
    [SysAbiExport(
        Nid = "3e+4Iv7IJ8U",
        ExportName = "accept",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libKernel")]
    public static int PosixAccept(CpuContext ctx)
    {
        return AcceptGuestSocket(
            ctx,
            unchecked((int)ctx[CpuRegister.Rdi]),
            ctx[CpuRegister.Rsi],
            ctx[CpuRegister.Rdx],
            strictAddressArguments: true);
    }

    private static int AcceptGuestSocket(CpuContext ctx, int id, ulong address, ulong lengthAddress, bool strictAddressArguments)
    {
        if (!_sockets.TryGetValue(id, out var socket))
        {
            return SetNetError(ctx, NetErrorBadFileDescriptor, NetErrnoBadFileDescriptor);
        }

        var addressCapacity = 0;
        if (address != 0)
        {
            if (lengthAddress != 0 &&
                ctx.TryReadUInt32(lengthAddress, out var capacity) &&
                capacity != 0 &&
                capacity <= int.MaxValue)
            {
                addressCapacity = unchecked((int)capacity);
            }
            else if (strictAddressArguments)
            {
                return SetNetError(ctx, NetErrorInvalidArgument, NetErrnoInvalidArgument);
            }
        }

        try
        {
            var accepted = socket.Accept();
            // Accepted sockets follow the listening socket's blocking mode so a game
            // driving blocking listeners keeps blocking connection handlers.
            accepted.Blocking = socket.Blocking;
            var acceptedId = Interlocked.Increment(ref _nextSocketId);
            _sockets[acceptedId] = accepted;
            if (address != 0 && addressCapacity > 0 && accepted.RemoteEndPoint is IPEndPoint remote)
            {
                WriteSocketAddressResult(ctx, address, lengthAddress, addressCapacity, remote);
            }

            TraceNet("socket.accept", acceptedId, unchecked((ulong)id), 0, 0);
            ctx[CpuRegister.Rax] = unchecked((ulong)acceptedId);
            return (int)OrbisGen2Result.ORBIS_GEN2_OK;
        }
        catch (SocketException ex) when (ex.SocketErrorCode is SocketError.WouldBlock or SocketError.IOPending)
        {
            return SetNetError(ctx, NetErrorWouldBlock, NetErrnoWouldBlock);
        }
        catch (SocketException)
        {
            return SetNetError(ctx, NetErrorInvalidArgument, NetErrnoInvalidArgument);
        }
        catch (ObjectDisposedException)
        {
            return SetNetError(ctx, NetErrorBadFileDescriptor, NetErrnoBadFileDescriptor);
        }
    }

    [SysAbiExport(
        Nid = "HQOwnfMGipQ",
        ExportName = "sceNetErrnoLoc",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceNet")]
    public static int NetErrnoLoc(CpuContext ctx)
    {
        if (_errnoAddress == 0)
        {
            _errnoAddress = Marshal.AllocHGlobal(sizeof(int));
            Marshal.WriteInt32(_errnoAddress, 0);
        }

        ctx[CpuRegister.Rax] = unchecked((ulong)_errnoAddress);
        return (int)OrbisGen2Result.ORBIS_GEN2_OK;
    }

    [SysAbiExport(
        Nid = "dgJBaeJnGpo",
        ExportName = "sceNetPoolCreate",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceNet")]
    public static int NetPoolCreate(CpuContext ctx)
    {
        var nameAddress = ctx[CpuRegister.Rdi];
        var size = unchecked((int)ctx[CpuRegister.Rsi]);
        var flags = unchecked((int)ctx[CpuRegister.Rdx]);

        if (size <= 0)
        {
            return ctx.SetReturn(NetErrorInvalidArgument);
        }

        var name = TryReadUtf8Z(ctx, nameAddress, MaxNameLength, out var value)
            ? value
            : string.Empty;

        var id = Interlocked.Increment(ref _nextPoolId);
        _pools[id] = new NetPool(name, size, flags);

        TraceNet("pool.create", id, unchecked((ulong)size), unchecked((ulong)flags), _initialized ? 1UL : 0UL);
        ctx[CpuRegister.Rax] = unchecked((ulong)id);
        return (int)OrbisGen2Result.ORBIS_GEN2_OK;
    }

    [SysAbiExport(
        Nid = "K7RlrTkI-mw",
        ExportName = "sceNetPoolDestroy",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceNet")]
    public static int NetPoolDestroy(CpuContext ctx)
    {
        var id = unchecked((int)ctx[CpuRegister.Rdi]);
        if (!_pools.TryRemove(id, out _))
        {
            return ctx.SetReturn(NetErrorBadFileDescriptor);
        }

        TraceNet("pool.destroy", id, 0, 0, _initialized ? 1UL : 0UL);
        return ctx.SetReturn(0);
    }

    [SysAbiExport(
        Nid = "9T2pDF2Ryqg",
        ExportName = "sceNetHtonl",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceNet")]
    public static int NetHtonl(CpuContext ctx)
    {
        var value = unchecked((uint)ctx[CpuRegister.Rdi]);
        // The byte-swapped result is the return value and already lives in Rax; return OK as the
        // dispatch status without going through SetReturn, which would overwrite Rax with 0.
        ctx[CpuRegister.Rax] = BinaryPrimitives.ReverseEndianness(value);
        return (int)OrbisGen2Result.ORBIS_GEN2_OK;
    }

    [SysAbiExport(
        Nid = "iWQWrwiSt8A",
        ExportName = "sceNetHtons",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceNet")]
    public static int NetHtons(CpuContext ctx)
    {
        var value = unchecked((ushort)ctx[CpuRegister.Rdi]);
        // The byte-swapped result is the return value and already lives in Rax; return OK as the
        // dispatch status without going through SetReturn, which would overwrite Rax with 0.
        ctx[CpuRegister.Rax] = BinaryPrimitives.ReverseEndianness(value);
        return (int)OrbisGen2Result.ORBIS_GEN2_OK;
    }

    [SysAbiExport(
        Nid = "pQGpHYopAIY",
        ExportName = "sceNetNtohl",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceNet")]
    public static int NetNtohl(CpuContext ctx)
    {
        var value = unchecked((uint)ctx[CpuRegister.Rdi]);
        // The byte-swapped result is the return value and already lives in Rax; return OK as the
        // dispatch status without going through SetReturn, which would overwrite Rax with 0.
        ctx[CpuRegister.Rax] = BinaryPrimitives.ReverseEndianness(value);
        return (int)OrbisGen2Result.ORBIS_GEN2_OK;
    }

    [SysAbiExport(
        Nid = "Rbvt+5Y2iEw",
        ExportName = "sceNetNtohs",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceNet")]
    public static int NetNtohs(CpuContext ctx)
    {
        var value = unchecked((ushort)ctx[CpuRegister.Rdi]);
        // The byte-swapped result is the return value and already lives in Rax; return OK as the
        // dispatch status without going through SetReturn, which would overwrite Rax with 0.
        ctx[CpuRegister.Rax] = BinaryPrimitives.ReverseEndianness(value);
        return (int)OrbisGen2Result.ORBIS_GEN2_OK;
    }

    [SysAbiExport(
        Nid = "C4UgDHHPvdw",
        ExportName = "sceNetResolverCreate",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceNet")]
    public static int NetResolverCreate(CpuContext ctx)
    {
        var nameAddress = ctx[CpuRegister.Rdi];
        var poolId = unchecked((int)ctx[CpuRegister.Rsi]);
        var flags = unchecked((int)ctx[CpuRegister.Rdx]);
        if (flags != 0)
        {
            return ctx.SetReturn(NetErrorInvalidArgument);
        }

        var name = TryReadUtf8Z(ctx, nameAddress, MaxNameLength, out var value)
            ? value
            : string.Empty;
        var id = Interlocked.Increment(ref _nextResolverId);
        _resolvers[id] = new ResolverContext(name, poolId, flags);
        TraceNet("resolver.create", id, unchecked((ulong)poolId), unchecked((ulong)flags), _initialized ? 1UL : 0UL);
        ctx[CpuRegister.Rax] = unchecked((ulong)id);
        return (int)OrbisGen2Result.ORBIS_GEN2_OK;
    }

    [SysAbiExport(
        Nid = "kJlYH5uMAWI",
        ExportName = "sceNetResolverDestroy",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceNet")]
    public static int NetResolverDestroy(CpuContext ctx)
    {
        var id = unchecked((int)ctx[CpuRegister.Rdi]);
        return _resolvers.TryRemove(id, out _)
            ? ctx.SetReturn(0)
            : ctx.SetReturn(NetErrorBadFileDescriptor);
    }

    [SysAbiExport(
        Nid = "J5i3hiLJMPk",
        ExportName = "sceNetResolverGetError",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceNet")]
    public static int NetResolverGetError(CpuContext ctx)
    {
        var id = unchecked((int)ctx[CpuRegister.Rdi]);
        var statusAddress = ctx[CpuRegister.Rsi];
        if (statusAddress == 0)
        {
            return ctx.SetReturn(NetErrorInvalidArgument);
        }

        if (!_resolvers.TryGetValue(id, out var resolver))
        {
            return ctx.SetReturn(NetErrorBadFileDescriptor);
        }

        Span<byte> status = stackalloc byte[sizeof(int)];
        BinaryPrimitives.WriteInt32LittleEndian(status, resolver.LastError);
        return ctx.Memory.TryWrite(statusAddress, status)
            ? ctx.SetReturn(0)
            : ctx.SetReturn((int)OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT);
    }

    [SysAbiExport(
        Nid = "Nd91WaWmG2w",
        ExportName = "sceNetResolverStartNtoa",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceNet")]
    public static int NetResolverStartNtoa(CpuContext ctx)
    {
        var id = unchecked((int)ctx[CpuRegister.Rdi]);
        var hostnameAddress = ctx[CpuRegister.Rsi];
        var addressOut = ctx[CpuRegister.Rdx];
        if (!_resolvers.TryGetValue(id, out var resolver))
        {
            return SetNetError(ctx, NetErrorBadFileDescriptor, NetErrnoBadFileDescriptor);
        }

        if (!TryReadUtf8Z(ctx, hostnameAddress, MaxNameLength, out var hostname) ||
            hostname.Length == 0 ||
            addressOut == 0)
        {
            return SetNetError(ctx, NetErrorInvalidArgument, NetErrnoInvalidArgument);
        }

        try
        {
            // The console resolver is synchronous (the timeout/retry/flags parameters
            // in Rcx/R8/R9 shape the lookup but need no host counterpart), so block
            // the calling guest thread on the host resolver, bounded by
            // ResolveTimeout.
            var resolved = Dns.GetHostAddressesAsync(hostname)
                .WaitAsync(ResolveTimeout)
                .GetAwaiter()
                .GetResult();
            var address = Array.Find(resolved, candidate => candidate.AddressFamily == AddressFamily.InterNetwork);
            if (address is null)
            {
                resolver.LastError = NetErrnoInvalidArgument;
                return SetNetError(ctx, NetErrorInvalidArgument, NetErrnoInvalidArgument);
            }

            var bytes = address.GetAddressBytes();
            if (!ctx.Memory.TryWrite(addressOut, bytes))
            {
                return SetNetError(ctx, NetErrorInvalidArgument, NetErrnoInvalidArgument);
            }

            resolver.LastError = 0;
            TraceNet("resolver.ntoa", id, BinaryPrimitives.ReadUInt32LittleEndian(bytes), unchecked((uint)resolved.Length), 0);
            return ctx.SetReturn(0);
        }
        catch (Exception exception) when (exception is SocketException or TimeoutException or ArgumentException or AggregateException)
        {
            resolver.LastError = NetErrnoInvalidArgument;
            TraceNet("resolver.ntoa.failed", id, 0, 0, 0);
            return SetNetError(ctx, NetErrorInvalidArgument, NetErrnoInvalidArgument);
        }
    }

    [SysAbiExport(
        Nid = "Apb4YDxKsRI",
        ExportName = "sceNetResolverStartAton",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceNet")]
    public static int NetResolverStartAton(CpuContext ctx)
    {
        var id = unchecked((int)ctx[CpuRegister.Rdi]);
        var addressIn = ctx[CpuRegister.Rsi];
        var hostnameOut = ctx[CpuRegister.Rdx];
        var hostnameLength = unchecked((int)ctx[CpuRegister.Rcx]);
        if (!_resolvers.TryGetValue(id, out var resolver))
        {
            return SetNetError(ctx, NetErrorBadFileDescriptor, NetErrnoBadFileDescriptor);
        }

        if (addressIn == 0 || hostnameOut == 0 || hostnameLength <= 0)
        {
            return SetNetError(ctx, NetErrorInvalidArgument, NetErrnoInvalidArgument);
        }

        Span<byte> addressBytes = stackalloc byte[4];
        if (!ctx.Memory.TryRead(addressIn, addressBytes))
        {
            return SetNetError(ctx, NetErrorInvalidArgument, NetErrnoInvalidArgument);
        }

        try
        {
            // Reverse lookup: the address arrives in network byte order, which is the
            // layout IPAddress's constructor consumes directly. The timeout/retry
            // parameters (R8/R9 and the 7th stack argument) shape the console lookup
            // but have no host counterpart.
            var entry = Dns.GetHostEntryAsync(new IPAddress(addressBytes))
                .WaitAsync(ResolveTimeout)
                .GetAwaiter()
                .GetResult();
            var encoded = Encoding.UTF8.GetBytes(entry.HostName);
            if (encoded.Length + 1 > hostnameLength)
            {
                resolver.LastError = NetErrnoInvalidArgument;
                return SetNetError(ctx, NetErrorInvalidArgument, NetErrnoInvalidArgument);
            }

            var buffer = new byte[encoded.Length + 1];
            encoded.CopyTo(buffer, 0);
            if (!ctx.Memory.TryWrite(hostnameOut, buffer))
            {
                return SetNetError(ctx, NetErrorInvalidArgument, NetErrnoInvalidArgument);
            }

            resolver.LastError = 0;
            TraceNet("resolver.aton", id, BinaryPrimitives.ReadUInt32LittleEndian(addressBytes), (ulong)encoded.Length, 0);
            return ctx.SetReturn(0);
        }
        catch (Exception exception) when (exception is SocketException or TimeoutException or ArgumentException or AggregateException)
        {
            resolver.LastError = NetErrnoInvalidArgument;
            TraceNet("resolver.aton.failed", id, 0, 0, 0);
            return SetNetError(ctx, NetErrorInvalidArgument, NetErrnoInvalidArgument);
        }
    }

    private static int SetNetError(CpuContext ctx, int result, int errno)
    {
        if (_errnoAddress == 0)
        {
            _errnoAddress = Marshal.AllocHGlobal(sizeof(int));
        }
        Marshal.WriteInt32(_errnoAddress, errno);
        return ctx.SetReturn(result);
    }

    private static bool TryTranslateSocketParameters(
        int family,
        int type,
        int protocol,
        out AddressFamily addressFamily,
        out SocketType socketType,
        out ProtocolType protocolType)
    {
        addressFamily = family switch
        {
            2 => AddressFamily.InterNetwork,
            28 => AddressFamily.InterNetworkV6,
            _ => AddressFamily.Unspecified,
        };
        socketType = type switch
        {
            1 => SocketType.Stream,
            2 => SocketType.Dgram,
            _ => SocketType.Unknown,
        };
        protocolType = protocol switch
        {
            0 when socketType == SocketType.Stream => ProtocolType.Tcp,
            0 when socketType == SocketType.Dgram => ProtocolType.Udp,
            6 => ProtocolType.Tcp,
            17 => ProtocolType.Udp,
            _ => ProtocolType.Unknown,
        };

        return addressFamily != AddressFamily.Unspecified &&
            socketType != SocketType.Unknown &&
            protocolType != ProtocolType.Unknown;
    }

    private static bool TryReadSocketAddress(CpuContext ctx, ulong address, int length, out IPEndPoint endpoint)
    {
        endpoint = new IPEndPoint(IPAddress.Any, 0);
        if (address == 0 || length < 16)
        {
            return false;
        }

        Span<byte> bytes = stackalloc byte[16];
        if (!ctx.Memory.TryRead(address, bytes) || bytes[1] != 2)
        {
            return false;
        }

        var port = BinaryPrimitives.ReadUInt16BigEndian(bytes[2..4]);
        endpoint = new IPEndPoint(new IPAddress(bytes[4..8]), port);
        return true;
    }

    private static bool TryReadUtf8Z(CpuContext ctx, ulong address, int maxLength, out string value)
    {
        value = string.Empty;
        if (address == 0)
        {
            return true;
        }

        Span<byte> one = stackalloc byte[1];
        var bytes = new byte[maxLength];
        var count = 0;
        for (; count < maxLength; count++)
        {
            if (!ctx.Memory.TryRead(address + (ulong)count, one))
            {
                return false;
            }

            if (one[0] == 0)
            {
                break;
            }

            bytes[count] = one[0];
        }

        value = Encoding.UTF8.GetString(bytes, 0, count);
        return true;
    }

    [SysAbiExport(
        Nid = "8Kcp5d-q1Uo",
        ExportName = "sceNetInetPton",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceNet")]
    public static int NetInetPton(CpuContext ctx)
    {
        var addressFamily = unchecked((int)ctx[CpuRegister.Rdi]);
        var sourceAddress = ctx[CpuRegister.Rsi];
        var destinationAddress = ctx[CpuRegister.Rdx];
        if (sourceAddress == 0 || destinationAddress == 0)
        {
            return SetNetError(ctx, NetErrorInvalidArgument, NetErrnoInvalidArgument);
        }

        if (!TryReadUtf8Z(ctx, sourceAddress, MaxNameLength, out var source))
        {
            return SetNetError(ctx, NetErrorInvalidArgument, NetErrnoInvalidArgument);
        }

        var family = addressFamily switch
        {
            2 => AddressFamily.InterNetwork,      // AF_INET
            28 => AddressFamily.InterNetworkV6,   // AF_INET6
            _ => AddressFamily.Unknown,
        };
        if (family == AddressFamily.Unknown ||
            !IPAddress.TryParse(source, out var parsed) ||
            parsed.AddressFamily != family)
        {
            // Match BSD inet_pton: return 0 for a parseable-family miss.
            ctx[CpuRegister.Rax] = 0;
            return 0;
        }

        var bytes = parsed.GetAddressBytes();
        if (!ctx.Memory.TryWrite(destinationAddress, bytes))
        {
            return SetNetError(ctx, NetErrorInvalidArgument, NetErrnoInvalidArgument);
        }

        TraceNet("inet_pton", addressFamily, sourceAddress, destinationAddress, (ulong)bytes.Length);
        ctx[CpuRegister.Rax] = 1;
        return 1;
    }

    /// <summary>
    /// Translates ORBIS_NET_MSG_* flags onto their managed equivalents. Flags without
    /// a managed counterpart (MSG_WAITALL, MSG_DONTWAIT, ...) are ignored: DONTWAIT is
    /// applied by the recv paths themselves and the rest only relax guarantees the
    /// managed API already provides for small buffers.
    /// </summary>
    private static SocketFlags TranslateSocketFlags(int flags)
    {
        var socketFlags = SocketFlags.None;
        if ((flags & MessageFlagPeek) != 0)
        {
            socketFlags |= SocketFlags.Peek;
        }

        if ((flags & MessageFlagDontRoute) != 0)
        {
            socketFlags |= SocketFlags.DontRoute;
        }

        return socketFlags;
    }

    /// <summary>
    /// Writes an endpoint back into a guest sockaddr buffer (sa_len, sa_family, port,
    /// address — the layout <see cref="TryReadSocketAddress"/> parses) and stores the
    /// final length through the in/out socklen_t pointer.
    /// </summary>
    /// <remarks>
    /// A capacity too small for the address skips the sockaddr write but still reports
    /// the required length, so callers can detect truncation the way they would on the
    /// console.
    /// </remarks>
    private static void WriteSocketAddressResult(
        CpuContext ctx,
        ulong address,
        ulong lengthAddress,
        int capacity,
        IPEndPoint endpoint)
    {
        var isIPv6 = endpoint.Address.AddressFamily != AddressFamily.InterNetwork;
        var size = isIPv6 ? SocketAddressSizeIPv6 : SocketAddressSizeIPv4;
        Span<byte> sockaddr = stackalloc byte[SocketAddressSizeIPv6];
        sockaddr.Clear();
        sockaddr[0] = unchecked((byte)size);
        sockaddr[1] = isIPv6 ? (byte)28 : (byte)2;
        BinaryPrimitives.WriteUInt16BigEndian(sockaddr[2..4], (ushort)endpoint.Port);
        var addressBytes = endpoint.Address.GetAddressBytes();
        if (isIPv6)
        {
            // sockaddr_in6 keeps the port at offset 2 and the address at offset 8.
            addressBytes.AsSpan().CopyTo(sockaddr[8..]);
        }
        else
        {
            addressBytes.AsSpan().CopyTo(sockaddr[4..]);
        }

        if (capacity >= size)
        {
            _ = ctx.Memory.TryWrite(address, sockaddr[..size]);
        }

        if (lengthAddress != 0)
        {
            Span<byte> lengthBytes = stackalloc byte[sizeof(uint)];
            BinaryPrimitives.WriteUInt32LittleEndian(lengthBytes, unchecked((uint)size));
            _ = ctx.Memory.TryWrite(lengthAddress, lengthBytes);
        }
    }

    // ---- Readiness and blocking queries shared with the kernel poll/select/fcntl
    // exports ----
    // Keeping the socket state behind these queries lets those POSIX handlers report
    // live socket readiness and honor fcntl O_NONBLOCK without reaching into the
    // socket table themselves.

    public static bool IsSocketDescriptor(int id) => _sockets.ContainsKey(id);

    public static bool TryGetSocketBlocking(int id, out bool blocking)
    {
        if (!_sockets.TryGetValue(id, out var socket))
        {
            blocking = false;
            return false;
        }

        blocking = socket.Blocking;
        return true;
    }

    public static bool TrySetSocketBlocking(int id, bool blocking)
    {
        if (!_sockets.TryGetValue(id, out var socket))
        {
            return false;
        }

        socket.Blocking = blocking;
        return true;
    }

    // poll(2) event bits (FreeBSD numbering, shared with ORBIS_NET_POLL_*).
    public const short PollInput = 0x0001;
    public const short PollPriority = 0x0002;
    public const short PollOutput = 0x0004;
    public const short PollError = 0x0008;
    public const short PollHangUp = 0x0010;
    public const short PollInvalid = 0x0020;

    /// <summary>
    /// Evaluates revents for a socket descriptor: the requested readability or
    /// writability only when the socket is actually ready, plus unconditional error
    /// and invalid-descriptor bits.
    /// </summary>
    /// <remarks>
    /// A peer-side EOF reads as readiness (a following recv returns 0) rather than
    /// POLLHUP: distinguishing a hung-up stream from a listening socket with a pending
    /// connection is not possible through the managed socket API, and a spurious
    /// POLLHUP on a listener would break accept loops.
    /// </remarks>
    public static short ComputeSocketPollEvents(int id, short requestedEvents)
    {
        if (!_sockets.TryGetValue(id, out var socket))
        {
            return PollInvalid;
        }

        try
        {
            short revents = 0;
            if ((requestedEvents & (PollInput | PollPriority)) != 0 && socket.Poll(0, SelectMode.SelectRead))
            {
                revents = (short)(revents | (requestedEvents & (PollInput | PollPriority)));
            }

            if ((requestedEvents & PollOutput) != 0 && socket.Poll(0, SelectMode.SelectWrite))
            {
                revents = (short)(revents | PollOutput);
            }

            if (socket.Poll(0, SelectMode.SelectError))
            {
                revents = (short)(revents | PollError);
            }

            return revents;
        }
        catch (SocketException)
        {
            return PollError;
        }
        catch (ObjectDisposedException)
        {
            // A socket closed since the pollfd array was built is no longer a valid
            // descriptor for poll(2).
            return PollInvalid;
        }
    }

    /// <summary>
    /// Per-descriptor readiness for select(2): read, write and exception state for a
    /// socket this backend tracks. Descriptors that are not sockets report false so
    /// the caller keeps its own file behavior.
    /// </summary>
    public static bool TryGetSocketReadiness(int id, out bool readable, out bool writable, out bool error)
    {
        readable = writable = error = false;
        if (!_sockets.TryGetValue(id, out var socket))
        {
            return false;
        }

        try
        {
            readable = socket.Poll(0, SelectMode.SelectRead);
            writable = socket.Poll(0, SelectMode.SelectWrite);
            error = socket.Poll(0, SelectMode.SelectError);
            return true;
        }
        catch (SocketException)
        {
            error = true;
            return true;
        }
        catch (ObjectDisposedException)
        {
            error = true;
            return true;
        }
    }

    private static void TraceNet(string operation, int id, ulong arg0, ulong arg1, ulong arg2)
    {
        if (!string.Equals(Environment.GetEnvironmentVariable("SHARPEMU_LOG_NET"), "1", StringComparison.Ordinal))
        {
            return;
        }

        Console.Error.WriteLine(
            $"[LOADER][TRACE] net.{operation} id={id} arg0=0x{arg0:X16} arg1=0x{arg1:X16} arg2=0x{arg2:X16}");
    }
}
