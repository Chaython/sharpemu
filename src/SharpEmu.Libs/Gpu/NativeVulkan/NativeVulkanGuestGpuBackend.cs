// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using SharpEmu.Libs.Gpu.Vulkan;
using SharpEmu.ShaderCompiler;
using SharpEmu.ShaderCompiler.Vulkan;

namespace SharpEmu.Libs.Gpu.NativeVulkan;

/// <summary>Native C++ Vulkan implementation of the guest-domain GPU seam.</summary>
internal sealed unsafe class NativeVulkanGuestGpuBackend : IGuestGpuBackend
{
    // GNM texture-type code for 3D volumes; every other type is a 2D/array/1D
    // view whose cache identity uses a normalized depth of one.
    private const uint Gen5TextureType3D = 10;

    // Bound on the texture-content identity set: static scenes reference a few
    // thousand distinct descriptors; unbounded growth would only ever hold
    // evicted identities whose skip answer would then be wrong anyway.
    private const int MaxCachedTextureIdentities = 65_536;

    private static readonly IGuestCompiledShader DepthOnlyFragmentShader =
        new VulkanCompiledGuestShader(SpirvFixedShaders.CreateDepthOnlyFragment());

    private readonly object _startGate = new();
    private readonly BlockingCollection<Action<nint>> _commands = new(new ConcurrentQueue<Action<nint>>(), 256);
    private readonly ManualResetEventSlim _ready = new(false);
    private Thread? _thread;
    private Exception? _startError;

    // Guest work ordering. The native backend consumes every command through a
    // single FIFO queue, so a monotonic sequence plus one completion source per
    // sequence is enough to reproduce the seam's ordered-guest-queue contract
    // (the sequences AGC hands around are this backend's work tickets).
    private readonly object _guestWorkGate = new();
    private readonly ConcurrentDictionary<long, TaskCompletionSource<bool>> _guestWorkWaiters = new();
    private long _guestWorkSequence;
    private long _executingGuestWorkSequence;
    private bool _guestWorkClosed;
    private static readonly IDisposable SharedGuestQueueScope = new GuestQueueScope();
    private bool _closeRequested;

    // Guest image lifecycle. The native ABI has no upload/fill/write guest-image
    // entry points (images are created and retained from draw packets and
    // flips), so the seam's content calls are managed-side bookkeeping: they
    // keep the AGC layer from re-reading initial data every frame and let
    // extent queries answer for presented flip sources.
    private readonly ConcurrentDictionary<ulong, GuestImageRecord> _guestImages = new();
    private readonly ConcurrentDictionary<TextureContentIdentity, byte> _cachedTextureIdentities = new();
    private int _imageLifecycleLogged;

    // Guest memory handle for backend self-healing (cache misses re-reading
    // texels directly); retained for the future native read-back path.
    private SharpEmu.HLE.ICpuMemory? _guestMemory;

    private long _perfDrawCount;
    private long _perfShaderCompilations;

    public string BackendName => "Native Vulkan";

    public void EnsureStarted(uint width, uint height)
    {
        if (width == 0 || height == 0) return;
        lock (_startGate)
        {
            if (_thread is null)
            {
                _thread = new Thread(() => Run(width, height))
                {
                    IsBackground = true,
                    Name = "SharpEmu native Vulkan",
                };
                _thread.Start();
            }
        }
        _ready.Wait();
        if (_startError is not null)
        {
            if (_startError is DllNotFoundException)
            {
                _ = NativeVulkanApi.IsAvailable(out var error);
                throw new InvalidOperationException(error, _startError);
            }

            throw new InvalidOperationException("Native Vulkan startup failed", _startError);
        }
    }

    public bool TryCompileVertexShader(Gen5ShaderState state, Gen5ShaderEvaluation evaluation,
        out IGuestCompiledShader? shader, out string error, int globalBufferBase = 0,
        int totalGlobalBufferCount = -1, int imageBindingBase = 0, int scalarRegisterBufferIndex = -1,
        int requiredVertexOutputCount = 0, ulong storageBufferOffsetAlignment = 1)
    {
        shader = null;
        if (!Gen5SpirvTranslator.TryCompileVertexShader(state, evaluation, out var compiled, out error,
                globalBufferBase, totalGlobalBufferCount, imageBindingBase, scalarRegisterBufferIndex,
                requiredVertexOutputCount, storageBufferOffsetAlignment)) return false;
        shader = new VulkanCompiledGuestShader(compiled.Spirv); return true;
    }

    public bool TryCompilePixelShader(Gen5ShaderState state, Gen5ShaderEvaluation evaluation,
        IReadOnlyList<Gen5PixelOutputBinding> outputs, out IGuestCompiledShader? shader, out string error,
        int globalBufferBase = 0, int totalGlobalBufferCount = -1, int imageBindingBase = 0,
        int scalarRegisterBufferIndex = -1, uint pixelInputEnable = 0, uint pixelInputAddress = 0,
        IReadOnlyList<uint>? pixelInputCntl = null, ulong storageBufferOffsetAlignment = 1)
    {
        shader = null;
        if (!Gen5SpirvTranslator.TryCompilePixelShader(state, evaluation, outputs, out var compiled, out error,
                globalBufferBase, totalGlobalBufferCount, imageBindingBase, scalarRegisterBufferIndex,
                pixelInputEnable, pixelInputAddress, pixelInputCntl, storageBufferOffsetAlignment)) return false;
        shader = new VulkanCompiledGuestShader(compiled.Spirv); return true;
    }

    public bool TryCompileComputeShader(Gen5ShaderState state, Gen5ShaderEvaluation evaluation,
        uint localSizeX, uint localSizeY, uint localSizeZ, out IGuestCompiledShader? shader, out string error,
        int totalGlobalBufferCount = -1, int initialScalarBufferIndex = -1, uint waveLaneCount = 32,
        ulong storageBufferOffsetAlignment = 1)
    {
        shader = null;
        if (!Gen5SpirvTranslator.TryCompileComputeShader(state, evaluation, localSizeX, localSizeY, localSizeZ,
                out var compiled, out error, totalGlobalBufferCount, initialScalarBufferIndex, waveLaneCount,
                storageBufferOffsetAlignment)) return false;
        shader = new VulkanCompiledGuestShader(compiled.Spirv); return true;
    }

    public IGuestCompiledShader GetDepthOnlyFragmentShader() => DepthOnlyFragmentShader;

    public IGuestCompiledShader GetFallbackColorFragmentShader(
        IReadOnlyList<Gen5PixelOutputKind> outputKinds) =>
        new VulkanCompiledGuestShader(
            SpirvFixedShaders.CreateSolidFragment(1f, 0f, 1f, 1f, outputKinds));

    public void HideSplashScreen() { }

    public void Submit(byte[] bgraFrame, uint width, uint height)
    {
        if (bgraFrame.Length != checked((int)(width * height * 4))) return;
        EnsureStarted(width, height);
        Enqueue(handle =>
        {
            fixed (byte* pixels = bgraFrame)
                Check(handle, NativeVulkanApi.PresentBgra(handle, pixels, (nuint)bgraFrame.Length, width, height, width * 4));
        });
    }

    public void SubmitGuestDraw(GuestDrawKind drawKind, uint width, uint height)
    {
        if (drawKind != GuestDrawKind.FullscreenBarycentric || width == 0 || height == 0) return;
        EnsureStarted(width, height);
        Interlocked.Increment(ref _perfDrawCount);
        var pixel = new VulkanCompiledGuestShader(SpirvFixedShaders.CreateBarycentricFragment());
        Enqueue(handle => Check(handle, NativeGpuPacket.SubmitDraw(handle, pixel, [], [],
            width, height, 1, null, 3, 1, 4, null, null, null, null, false)));
    }

    public void SubmitTranslatedDraw(IGuestCompiledShader pixelShader, IReadOnlyList<GuestDrawTexture> textures,
        IReadOnlyList<GuestMemoryBuffer> globalMemoryBuffers, uint width, uint height, uint attributeCount,
        IGuestCompiledShader? vertexShader = null, uint vertexCount = 3, uint instanceCount = 1,
        uint primitiveType = 4, GuestIndexBuffer? indexBuffer = null,
        IReadOnlyList<GuestVertexBuffer>? vertexBuffers = null, GuestRenderState? renderState = null)
    {
        EnsureStarted(width, height);
        Interlocked.Increment(ref _perfDrawCount);
        var ps = Spirv(pixelShader); var vs = vertexShader is null ? null : Spirv(vertexShader);
        var textureCopy = textures.ToArray(); var memoryCopy = globalMemoryBuffers.ToArray();
        var vertexCopy = vertexBuffers?.ToArray();
        NoteTextureIdentities(textureCopy);
        Enqueue(handle => Check(handle, NativeGpuPacket.SubmitDraw(handle, ps, textureCopy, memoryCopy,
            width, height, attributeCount, vs, vertexCount, instanceCount, primitiveType, indexBuffer,
            vertexCopy, renderState, null, false)));
    }

    public void SubmitOffscreenTranslatedDraw(IGuestCompiledShader pixelShader,
        IReadOnlyList<GuestDrawTexture> textures, IReadOnlyList<GuestMemoryBuffer> globalMemoryBuffers,
        uint attributeCount, IReadOnlyList<GuestRenderTarget> targets, IGuestCompiledShader? vertexShader = null,
        uint vertexCount = 3, uint instanceCount = 1, uint primitiveType = 4,
        GuestIndexBuffer? indexBuffer = null, IReadOnlyList<GuestVertexBuffer>? vertexBuffers = null,
        GuestRenderState? renderState = null, GuestDepthTarget? depthTarget = null, ulong shaderAddress = 0,
        int baseVertex = 0)
    {
        // The native draw packet has no base-vertex field yet; AGC currently
        // defaults it to 0, so index-buffer draws that translate a non-zero base
        // vertex render with unshifted indices until the native ABI grows one.
        _ = baseVertex;
        if (targets.Count == 0) return;
        EnsureStarted(targets[0].Width, targets[0].Height);
        Interlocked.Increment(ref _perfDrawCount);
        var ps = Spirv(pixelShader); var vs = vertexShader is null ? null : Spirv(vertexShader);
        var textureCopy = textures.ToArray(); var memoryCopy = globalMemoryBuffers.ToArray();
        var targetCopy = targets.ToArray(); var vertexCopy = vertexBuffers?.ToArray();
        NoteTextureIdentities(textureCopy);
        Enqueue(handle => Check(handle, NativeGpuPacket.SubmitDraw(handle, ps, textureCopy, memoryCopy,
            targetCopy[0].Width, targetCopy[0].Height, attributeCount, vs, vertexCount, instanceCount,
            primitiveType, indexBuffer, vertexCopy, renderState, targetCopy, true)));
    }

    public void SubmitDepthOnlyTranslatedDraw(IGuestCompiledShader pixelShader,
        IReadOnlyList<GuestDrawTexture> textures, IReadOnlyList<GuestMemoryBuffer> globalMemoryBuffers,
        uint attributeCount, GuestDepthTarget depthTarget, IGuestCompiledShader? vertexShader = null,
        uint vertexCount = 3, uint instanceCount = 1, uint primitiveType = 4,
        GuestIndexBuffer? indexBuffer = null, IReadOnlyList<GuestVertexBuffer>? vertexBuffers = null,
        GuestRenderState? renderState = null, ulong shaderAddress = 0, int baseVertex = 0)
    {
        // The native draw packet has no base-vertex field yet; AGC currently
        // defaults it to 0 (see SubmitOffscreenTranslatedDraw).
        _ = baseVertex;
        EnsureStarted(depthTarget.Width, depthTarget.Height);
        Interlocked.Increment(ref _perfDrawCount);
        var ps = Spirv(pixelShader); var vs = vertexShader is null ? null : Spirv(vertexShader);
        var textureCopy = textures.ToArray(); var memoryCopy = globalMemoryBuffers.ToArray();
        var vertexCopy = vertexBuffers?.ToArray();
        NoteTextureIdentities(textureCopy);
        Enqueue(handle => Check(handle, NativeGpuPacket.SubmitDraw(handle, ps, textureCopy, memoryCopy,
            depthTarget.Width, depthTarget.Height, attributeCount, vs, vertexCount, instanceCount,
            primitiveType, indexBuffer, vertexCopy, renderState, null, false)));
    }

    public void SubmitStorageTranslatedDraw(IGuestCompiledShader pixelShader,
        IReadOnlyList<GuestDrawTexture> textures, IReadOnlyList<GuestMemoryBuffer> globalMemoryBuffers,
        uint attributeCount, uint width, uint height, ulong shaderAddress = 0)
    {
        EnsureStarted(width, height); var ps = Spirv(pixelShader);
        Interlocked.Increment(ref _perfDrawCount);
        var textureCopy = textures.ToArray(); var memoryCopy = globalMemoryBuffers.ToArray();
        NoteTextureIdentities(textureCopy);
        GuestRenderTarget[] targets = [new(0, width, height, 12, 7)];
        Enqueue(handle => Check(handle, NativeGpuPacket.SubmitDraw(handle, ps, textureCopy, memoryCopy,
            width, height, attributeCount, null, 3, 1, 4, null, null, null, targets, false)));
    }

    public long SubmitComputeDispatch(ulong shaderAddress, IGuestCompiledShader computeShader,
        IReadOnlyList<GuestDrawTexture> textures, IReadOnlyList<GuestMemoryBuffer> globalMemoryBuffers,
        uint groupCountX, uint groupCountY, uint groupCountZ, uint baseGroupX, uint baseGroupY,
        uint baseGroupZ, uint localSizeX, uint localSizeY, uint localSizeZ, bool isIndirect,
        bool writesGlobalMemory, uint threadCountX = uint.MaxValue, uint threadCountY = uint.MaxValue,
        uint threadCountZ = uint.MaxValue)
    {
        EnsureStarted(1280, 720); var shader = Spirv(computeShader);
        var textureCopy = textures.ToArray(); var memoryCopy = globalMemoryBuffers.ToArray();
        Enqueue(handle => Check(handle, NativeGpuPacket.SubmitCompute(handle, shaderAddress, shader,
            textureCopy, memoryCopy, groupCountX, groupCountY, groupCountZ)));
        return 0;
    }

    public bool TrySubmitGuestImage(ulong address, uint width, uint height, uint pitchInPixel)
    {
        EnsureStarted(width, height);
        var presented = Invoke(handle => NativeVulkanApi.PresentGuestImage(handle, address, width, height, pitchInPixel)) ==
               NativeGpuResult.Success;
        if (presented)
        {
            // A successful present proves the native side holds (and retains)
            // this address-keyed image; record its byte-exact guest extent for
            // the DMA mirror path (pitch in pixels, 4 bytes per display pixel).
            var pitchInPixels = pitchInPixel == 0 ? width : pitchInPixel;
            _guestImages[address] = new GuestImageRecord(0, 0, width, height,
                checked((ulong)pitchInPixels * height * 4), UploadKnown: true);
        }

        return presented;
    }

    public bool TrySubmitOrderedGuestImageFlip(int videoOutHandle, int displayBufferIndex, ulong address,
        uint width, uint height, uint pitchInPixel) =>
        TrySubmitGuestImage(address, width, height, pitchInPixel);

    public void RegisterKnownDisplayBuffer(ulong address, uint guestFormat)
    {
        EnsureStarted(1280, 720);
        Enqueue(handle => Check(handle, NativeVulkanApi.RegisterDisplayBuffer(handle, address, guestFormat)));
    }

    public bool IsGpuGuestImageAvailable(ulong address, uint format, uint numberType)
    {
        EnsureStarted(1280, 720);
        return Invoke(handle => NativeVulkanApi.HasGuestImage(handle, address, format, numberType)) ==
               NativeGpuResult.Success;
    }

    public bool TrySubmitGuestImageBlit(ulong sourceAddress, uint sourceWidth, uint sourceHeight,
        uint sourceFormat, uint sourceNumberType, ulong destinationAddress, uint destinationWidth,
        uint destinationHeight, uint destinationFormat, uint destinationNumberType)
    {
        EnsureStarted(destinationWidth, destinationHeight);
        return Invoke(handle => NativeVulkanApi.BlitGuestImage(handle, sourceAddress, sourceWidth, sourceHeight,
            sourceFormat, destinationAddress, destinationWidth, destinationHeight, destinationFormat)) ==
               NativeGpuResult.Success;
    }

    public bool TryGetRenderTargetOutputKind(uint dataFormat, uint numberType,
        out Gen5PixelOutputKind outputKind)
    {
        var result = NativeVulkanApi.RenderTargetOutputKind(dataFormat, numberType, out var nativeKind);
        outputKind = (Gen5PixelOutputKind)nativeKind; return result == NativeGpuResult.Success;
    }

    // Guest work ordering. The single native FIFO already executes every
    // submission in order, so queue identity is irrelevant: the shared scope
    // merely marks the region for the AGC layer.

    public IDisposable EnterGuestQueue(string queueName, ulong submissionId) => SharedGuestQueueScope;

    public long SubmitOrderedGuestAction(Action action, string debugName)
    {
        ArgumentNullException.ThrowIfNull(action);
        var sequence = Interlocked.Increment(ref _guestWorkSequence);
        var completion = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        lock (_guestWorkGate)
        {
            // A closed or never-started presenter cannot drain the queue, so
            // report 0 and let the caller execute the action inline.
            if (_guestWorkClosed || Volatile.Read(ref _thread) is null) return 0;
            _guestWorkWaiters[sequence] = completion;
            // Enqueue at this action's exact position in the FIFO guest queue;
            // a full bounded queue reports 0 so the caller runs inline too.
            if (!_commands.TryAdd(handle => ExecuteGuestWork(sequence, completion, action, debugName), 0))
            {
                _guestWorkWaiters.TryRemove(sequence, out _);
                return 0;
            }
        }

        return sequence;
    }

    public long SubmitOrderedGuestFlipWait(int videoOutHandle, int displayBufferIndex) =>
        // The native backend presents on Poll from the same FIFO that drains
        // draws, so an in-order no-op marker is enough to preserve the queue
        // position of sceAgcDcbWaitUntilSafeForRendering.
        SubmitOrderedGuestAction(
            () => { },
            $"agc flip wait {videoOutHandle}:{displayBufferIndex}");

    public bool WaitForGuestWork(long workSequence, int timeoutMilliseconds = Timeout.Infinite)
    {
        if (workSequence <= 0) return false;
        // A missing completion source means the sequence already ran (or was
        // never enqueued) and was retired from the map: already complete.
        if (!_guestWorkWaiters.TryGetValue(workSequence, out var completion)) return true;
        var milliseconds = timeoutMilliseconds == Timeout.Infinite ? -1 : Math.Max(timeoutMilliseconds, 0);
        // Wait returns whether the task finished; the result is false when the
        // presenter closed and completed the waiter without running the work.
        return completion.Task.Wait(milliseconds) && completion.Task.Result;
    }

    public long CurrentGuestWorkSequenceForDiagnostics =>
        Volatile.Read(ref _executingGuestWorkSequence);

    // Guest image lifecycle: the native ABI exposes no upload/fill/write entry
    // points, so these calls are managed-side bookkeeping (plus a one-time
    // log). SupportsPartialImageWrite deliberately keeps the interface default
    // of false: there is no native upload path at all, let alone a partial one.

    public bool IsGuestImageUploadKnown(ulong address, uint format, uint numberType)
    {
        return _guestImages.TryGetValue(address, out var image) &&
               image.UploadKnown &&
               // Records that carry a format tag must match the queried
               // descriptor; untagged records (the present path) match any.
               (image.Format == 0 || (image.Format == format && image.NumberType == numberType));
    }

    public bool GuestImageWantsInitialData(ulong address) =>
        address != 0 && !_guestImages.ContainsKey(address);

    public void ProvideGuestImageInitialData(ulong address, byte[] rgbaPixels)
    {
        // No native entry point: the pending initial data cannot be applied to
        // the native image, but recording the upload keeps AGC from re-reading
        // the guest bytes on every subsequent draw.
        _ = rgbaPixels;
        MarkGuestImageBookkeeping(address);
    }

    public void SubmitGuestImageFill(ulong address, uint fillValue)
    {
        _ = fillValue;
        MarkGuestImageBookkeeping(address);
    }

    public void SubmitGuestImageWrite(ulong address, byte[] pixels, uint rowOffset = 0)
    {
        _ = pixels;
        _ = rowOffset;
        MarkGuestImageBookkeeping(address);
    }

    public void RequestCpuWrittenGuestImageSync(ulong scopeAddress = 0, ulong scopeByteCount = ulong.MaxValue)
    {
        // The native backend has no CPU-write tracking drain to wake.
        _ = scopeAddress;
        _ = scopeByteCount;
    }

    public bool TryGetGuestImageExtent(ulong address, out uint width, out uint height, out ulong byteCount)
    {
        // Only records with a byte-exact guest extent answer: content-call
        // bookkeeping knows no extent, and an RGBA-expanded count would make
        // DMA paths size their buffers from the wrong layout.
        if (_guestImages.TryGetValue(address, out var image) && image.ByteCount != 0)
        {
            width = image.Width;
            height = image.Height;
            byteCount = image.ByteCount;
            return true;
        }

        width = 0;
        height = 0;
        byteCount = 0;
        return false;
    }

    public IReadOnlyList<(ulong Address, uint Width, uint Height, ulong ByteCount)> GetGuestImageExtents() =>
        _guestImages
            .Select(entry => (entry.Key, entry.Value.Width, entry.Value.Height, entry.Value.ByteCount))
            .ToArray();

    public bool IsTextureContentCached(in TextureContentIdentity identity) =>
        _cachedTextureIdentities.ContainsKey(identity);

    public void AttachGuestMemory(SharpEmu.HLE.ICpuMemory memory) => _guestMemory = memory;

    /// <summary>Guest memory handle for future self-healing re-reads; kept
    /// readable so cache-miss paths can adopt it without another seam change.</summary>
    private SharpEmu.HLE.ICpuMemory? GuestMemory => Volatile.Read(ref _guestMemory);

    // Matches VulkanVideoPresenter.GuestStorageBufferOffsetAlignment: the AGC
    // layer aligns storage-buffer offsets before they cross the seam.
    public ulong GuestStorageBufferOffsetAlignment => 256;

    public void CountShaderCompilation() => Interlocked.Increment(ref _perfShaderCompilations);

    public (long Draws, double DrawMs, long Pipelines, long ShaderCompilations) ReadAndResetPerfCounters()
    {
        var draws = Interlocked.Exchange(ref _perfDrawCount, 0);
        var compilations = Interlocked.Exchange(ref _perfShaderCompilations, 0);
        // The native backend reports no draw timing, and every translated
        // shader drives exactly one native pipeline.
        return (draws, 0.0, compilations, compilations);
    }

    public void RequestClose() => Volatile.Write(ref _closeRequested, true);

    private void Run(uint width, uint height)
    {
        nint backend = 0;
        try
        {
            if (NativeVulkanApi.GetAbiVersion() != NativeVulkanApi.AbiVersion)
                throw new InvalidOperationException("Native Vulkan ABI version mismatch");
            var title = Marshal.StringToCoTaskMemUTF8("SharpEmu");
            try
            {
                var info = new NativeVulkanApi.CreateInfo
                {
                    StructSize = (uint)sizeof(NativeVulkanApi.CreateInfo), AbiVersion = NativeVulkanApi.AbiVersion,
                    Width = width, Height = height, TitleUtf8 = (byte*)title,
                    EnableValidation = Environment.GetEnvironmentVariable("SHARPEMU_VK_VALIDATION") == "1" ? 1u : 0u,
                };
                var result = NativeVulkanApi.Create(&info, out backend);
                if (result != NativeGpuResult.Success)
                    throw new InvalidOperationException($"se_gpu_create failed with {result}: {NativeVulkanApi.GetError(0)}");
            }
            finally { Marshal.FreeCoTaskMem(title); }
            _ready.Set();
            NativeGpuInputSource.Instance.Attach();
            while (true)
            {
                if (_commands.TryTake(out var command, 8))
                {
                    command(backend);
                    for (var drained = 1; drained < 128 && _commands.TryTake(out command); ++drained)
                        command(backend);
                }
                var result = NativeVulkanApi.Poll(backend, out var shouldClose);
                if (result != NativeGpuResult.Success || shouldClose != 0 ||
                    Volatile.Read(ref _closeRequested)) break;
                var input = new NativeVulkanApi.Input { StructSize = (uint)sizeof(NativeVulkanApi.Input) };
                if (NativeVulkanApi.InputSnapshot(backend, &input) == NativeGpuResult.Success)
                    NativeGpuInputSource.Instance.Update(&input);
            }
        }
        catch (Exception exception)
        {
            _startError ??= exception;
            Console.Error.WriteLine($"[LOADER][ERROR] Native Vulkan backend failed: {exception}");
        }
        finally
        {
            _ready.Set();
            if (backend != 0) NativeVulkanApi.Destroy(backend);
            CloseGuestWork();
        }
    }

    private void ExecuteGuestWork(long sequence, TaskCompletionSource<bool> completion, Action action,
        string debugName)
    {
        Volatile.Write(ref _executingGuestWorkSequence, sequence);
        try
        {
            action();
            _guestWorkWaiters.TryRemove(sequence, out _);
            completion.TrySetResult(true);
        }
        catch (Exception exception)
        {
            // A throwing guest action must neither kill the native Run loop
            // (every other queued command would be lost) nor wedge its waiter.
            Console.Error.WriteLine(
                $"[LOADER][ERROR] Native Vulkan guest work '{debugName}' failed: {exception}");
            _guestWorkWaiters.TryRemove(sequence, out _);
            completion.TrySetResult(false);
        }
        finally
        {
            Volatile.Write(ref _executingGuestWorkSequence, 0);
        }
    }

    private void CloseGuestWork()
    {
        List<TaskCompletionSource<bool>> pending;
        lock (_guestWorkGate)
        {
            _guestWorkClosed = true;
            pending = [.. _guestWorkWaiters.Values];
            _guestWorkWaiters.Clear();
        }

        // Complete every waiter so close/exit can never wedge a caller blocked
        // in WaitForGuestWork; false tells it the work never ran.
        foreach (var completion in pending) completion.TrySetResult(false);
    }

    private void MarkGuestImageBookkeeping(ulong address)
    {
        if (address == 0) return;
        LogMissingNativeImageLifecycleOnce();
        // Bookkeeping only: preserve any known extent, mark the address as a
        // known upload so AGC stops re-reading its initial data.
        _guestImages.AddOrUpdate(
            address,
            _ => new GuestImageRecord(0, 0, 0, 0, 0, UploadKnown: true),
            (_, existing) => existing with { UploadKnown = true });
    }

    private void LogMissingNativeImageLifecycleOnce()
    {
        if (Interlocked.Exchange(ref _imageLifecycleLogged, 1) == 0)
            Console.Error.WriteLine(
                "[LOADER][WARN] Native Vulkan backend has no guest-image upload/fill/write entry points; " +
                "image lifecycle calls are managed-side bookkeeping only");
    }

    private void NoteTextureIdentities(IReadOnlyList<GuestDrawTexture> textures)
    {
        // The native side retains address-keyed images from draw packets, so a
        // draw that ships texel content publishes its descriptor identity for
        // AGC's cache-skip queries. Fallback and empty-pixel textures ship no
        // content and must stay queryable.
        foreach (var texture in textures)
        {
            if (texture.Address == 0 ||
                texture.IsFallback ||
                (texture.RgbaPixels.Length == 0 && texture.TiledSource is not { Length: > 0 })) continue;
            if (_cachedTextureIdentities.Count >= MaxCachedTextureIdentities) return;
            _cachedTextureIdentities.TryAdd(TextureContentIdentityOf(texture), 0);
        }
    }

    private static TextureContentIdentity TextureContentIdentityOf(GuestDrawTexture texture) => new(
        texture.Address,
        texture.Width,
        texture.Height,
        texture.Format,
        texture.NumberType,
        texture.DstSelect,
        texture.TileMode,
        texture.Pitch,
        texture.Sampler,
        texture.ArrayedView,
        Math.Max(texture.ArrayLayers, 1),
        Type: texture.Type,
        // Normalized exactly like AgcExports.GetTextureVolumeDepth so these
        // identities match the AGC layer's cache-skip queries.
        Depth: GetTextureVolumeDepth(texture.Type, texture.Depth));

    private static uint GetTextureVolumeDepth(uint type, uint depth) =>
        type == Gen5TextureType3D ? Math.Max(depth, 1u) : 1u;

    private void Enqueue(Action<nint> command)
    {
        if (!_commands.TryAdd(command)) Console.Error.WriteLine("[LOADER][WARN] Native GPU queue is full; dropping work");
    }

    private NativeGpuResult Invoke(Func<nint, NativeGpuResult> operation)
    {
        var completion = new TaskCompletionSource<NativeGpuResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        Enqueue(handle =>
        {
            try { completion.SetResult(operation(handle)); }
            catch (Exception exception) { completion.SetException(exception); }
        });
        return completion.Task.GetAwaiter().GetResult();
    }

    private static void Check(nint backend, NativeGpuResult result)
    {
        if (result is NativeGpuResult.Success or NativeGpuResult.NotReady) return;
        Console.Error.WriteLine($"[LOADER][ERROR] Native GPU operation failed: {result}: {NativeVulkanApi.GetError(backend)}");
    }

    private static VulkanCompiledGuestShader Spirv(IGuestCompiledShader shader) =>
        shader as VulkanCompiledGuestShader ?? throw new InvalidOperationException(
            $"Shader type {shader.GetType().Name} was not compiled by the native Vulkan backend");

    /// <summary>Tracked state of one address-keyed guest image. Format and
    /// NumberType are raw guest descriptor codes (0 when the record's source
    /// did not know them); ByteCount is the byte-exact guest-layout extent, or
    /// 0 when only RGBA-expanded content was ever seen.</summary>
    private readonly record struct GuestImageRecord(
        uint Format,
        uint NumberType,
        uint Width,
        uint Height,
        ulong ByteCount,
        bool UploadKnown);

    private sealed class GuestQueueScope : IDisposable
    {
        public void Dispose() { }
    }
}
