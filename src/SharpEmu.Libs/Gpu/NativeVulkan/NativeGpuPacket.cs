// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Runtime.InteropServices;
using SharpEmu.Libs.Gpu.Vulkan;
using SharpEmu.ShaderCompiler.Vulkan;

namespace SharpEmu.Libs.Gpu.NativeVulkan;

internal static unsafe class NativeGpuPacket
{
    internal static NativeGpuResult SubmitDraw(
        nint backend,
        VulkanCompiledGuestShader pixelShader,
        IReadOnlyList<GuestDrawTexture> textures,
        IReadOnlyList<GuestMemoryBuffer> memoryBuffers,
        uint width,
        uint height,
        uint attributeCount,
        VulkanCompiledGuestShader? vertexShader,
        uint vertexCount,
        uint instanceCount,
        uint primitiveType,
        GuestIndexBuffer? indexBuffer,
        IReadOnlyList<GuestVertexBuffer>? vertexBuffers,
        GuestRenderState? renderState,
        IReadOnlyList<GuestRenderTarget>? targets,
        bool publishTargets)
    {
        using var storage = new Storage();
        var nativeTextures = storage.Allocate<NativeVulkanApi.Texture>(textures.Count);
        for (var index = 0; index < textures.Count; ++index)
            nativeTextures[index] = Texture(textures[index], storage);
        var nativeMemory = storage.Allocate<NativeVulkanApi.MemoryBuffer>(memoryBuffers.Count);
        for (var index = 0; index < memoryBuffers.Count; ++index)
            nativeMemory[index] = new() { Address = memoryBuffers[index].BaseAddress, Data = storage.Pin(memoryBuffers[index].Data) };
        var vertices = vertexBuffers ?? [];
        var nativeVertices = storage.Allocate<NativeVulkanApi.VertexBuffer>(vertices.Count);
        for (var index = 0; index < vertices.Count; ++index)
        {
            var source = vertices[index];
            nativeVertices[index] = new()
            {
                StructSize = (uint)sizeof(NativeVulkanApi.VertexBuffer), Location = source.Location,
                ComponentCount = source.ComponentCount, DataFormat = source.DataFormat,
                NumberFormat = source.NumberFormat, Address = source.BaseAddress, Stride = source.Stride,
                OffsetBytes = source.OffsetBytes, Data = storage.Pin(source.Data),
            };
        }
        var targetList = targets ?? [];
        var nativeTargets = storage.Allocate<NativeVulkanApi.RenderTarget>(targetList.Count);
        for (var index = 0; index < targetList.Count; ++index)
        {
            var source = targetList[index];
            nativeTargets[index] = new()
            {
                StructSize = (uint)sizeof(NativeVulkanApi.RenderTarget), Address = source.Address,
                Width = source.Width, Height = source.Height, Format = source.Format,
                NumberType = source.NumberType, MipLevels = source.MipLevels,
            };
        }
        var state = renderState ?? GuestRenderState.Default;
        var blends = storage.Allocate<NativeVulkanApi.Blend>(state.Blends.Count);
        for (var index = 0; index < state.Blends.Count; ++index)
        {
            var source = state.Blends[index];
            blends[index] = new()
            {
                Enable = source.Enable ? 1u : 0u, ColorSrc = source.ColorSrcFactor,
                ColorDst = source.ColorDstFactor, ColorFunc = source.ColorFunc,
                AlphaSrc = source.AlphaSrcFactor, AlphaDst = source.AlphaDstFactor,
                AlphaFunc = source.AlphaFunc, SeparateAlpha = source.SeparateAlphaBlend ? 1u : 0u,
                WriteMask = source.WriteMask,
            };
        }
        NativeVulkanApi.IndexBuffer nativeIndex = default;
        NativeVulkanApi.IndexBuffer* nativeIndexPointer = null;
        if (indexBuffer is not null)
        {
            nativeIndex = new() { Data = storage.Pin(indexBuffer.Data), Is32Bit = indexBuffer.Is32Bit ? 1u : 0u };
            nativeIndexPointer = &nativeIndex;
        }
        NativeVulkanApi.Rect nativeScissor = default; NativeVulkanApi.Rect* scissorPointer = null;
        if (state.Scissor is { } scissor)
        {
            nativeScissor = new() { X = scissor.X, Y = scissor.Y, Width = scissor.Width, Height = scissor.Height };
            scissorPointer = &nativeScissor;
        }
        NativeVulkanApi.Viewport nativeViewport = default; NativeVulkanApi.Viewport* viewportPointer = null;
        if (state.Viewport is { } viewport)
        {
            nativeViewport = new()
            {
                X = viewport.X, Y = viewport.Y, Width = viewport.Width, Height = viewport.Height,
                MinDepth = viewport.MinDepth, MaxDepth = viewport.MaxDepth,
            };
            viewportPointer = &nativeViewport;
        }
        var draw = new NativeVulkanApi.Draw
        {
            StructSize = (uint)sizeof(NativeVulkanApi.Draw),
            Width = width,
            Height = height,
            VertexSpirv = storage.Pin(vertexShader?.Spirv ?? SpirvFixedShaders.CreateFullscreenVertex(attributeCount)),
            PixelSpirv = storage.Pin(pixelShader.Spirv), Textures = nativeTextures,
            TextureCount = (uint)textures.Count, MemoryBuffers = nativeMemory,
            MemoryBufferCount = (uint)memoryBuffers.Count, VertexBuffers = nativeVertices,
            VertexBufferCount = (uint)vertices.Count, Targets = nativeTargets,
            TargetCount = (uint)targetList.Count, Blends = blends, BlendCount = (uint)state.Blends.Count,
            IndexBuffer = nativeIndexPointer, Scissor = scissorPointer, ViewportState = viewportPointer,
            AttributeCount = attributeCount, VertexCount = vertexCount, InstanceCount = instanceCount,
            PrimitiveType = primitiveType, PublishTargets = publishTargets ? 1u : 0u,
        };
        return NativeVulkanApi.SubmitDraw(backend, &draw);
    }

    internal static NativeGpuResult SubmitCompute(
        nint backend,
        ulong shaderAddress,
        VulkanCompiledGuestShader shader,
        IReadOnlyList<GuestDrawTexture> textures,
        IReadOnlyList<GuestMemoryBuffer> buffers,
        uint x, uint y, uint z)
    {
        using var storage = new Storage();
        var nativeTextures = storage.Allocate<NativeVulkanApi.Texture>(textures.Count);
        for (var index = 0; index < textures.Count; ++index) nativeTextures[index] = Texture(textures[index], storage);
        var nativeBuffers = storage.Allocate<NativeVulkanApi.MemoryBuffer>(buffers.Count);
        for (var index = 0; index < buffers.Count; ++index)
            nativeBuffers[index] = new() { Address = buffers[index].BaseAddress, Data = storage.Pin(buffers[index].Data) };
        var compute = new NativeVulkanApi.Compute
        {
            StructSize = (uint)sizeof(NativeVulkanApi.Compute), ShaderAddress = shaderAddress,
            Spirv = storage.Pin(shader.Spirv), Textures = nativeTextures, TextureCount = (uint)textures.Count,
            MemoryBuffers = nativeBuffers, MemoryBufferCount = (uint)buffers.Count,
            GroupsX = x, GroupsY = y, GroupsZ = z,
        };
        return NativeVulkanApi.SubmitCompute(backend, &compute);
    }

    private static NativeVulkanApi.Texture Texture(GuestDrawTexture source, Storage storage)
    {
        var result = new NativeVulkanApi.Texture
        {
            StructSize = (uint)sizeof(NativeVulkanApi.Texture), Address = source.Address,
            Width = source.Width, Height = source.Height, Format = source.Format,
            NumberType = source.NumberType, RgbaPixels = storage.Pin(source.RgbaPixels),
            IsFallback = source.IsFallback ? 1u : 0u, IsStorage = source.IsStorage ? 1u : 0u,
            MipLevels = source.MipLevels, MipLevel = source.MipLevel, Pitch = source.Pitch,
            TileMode = source.TileMode, DstSelect = source.DstSelect,
        };
        result.SamplerState.Words[0] = source.Sampler.Word0;
        result.SamplerState.Words[1] = source.Sampler.Word1;
        result.SamplerState.Words[2] = source.Sampler.Word2;
        result.SamplerState.Words[3] = source.Sampler.Word3;
        return result;
    }

    private sealed class Storage : IDisposable
    {
        private readonly List<GCHandle> _pins = [];
        private readonly List<nint> _allocations = [];
        internal NativeVulkanApi.Bytes Pin(byte[] data)
        {
            if (data.Length == 0) return default;
            var pin = GCHandle.Alloc(data, GCHandleType.Pinned); _pins.Add(pin);
            return new() { Data = (void*)pin.AddrOfPinnedObject(), Size = (nuint)data.Length };
        }
        internal T* Allocate<T>(int count) where T : unmanaged
        {
            if (count == 0) return null;
            var pointer = NativeMemory.AllocZeroed((nuint)count, (nuint)sizeof(T));
            if (pointer is null) throw new OutOfMemoryException();
            _allocations.Add((nint)pointer); return (T*)pointer;
        }
        public void Dispose()
        {
            foreach (var pin in _pins) pin.Free();
            foreach (var allocation in _allocations) NativeMemory.Free((void*)allocation);
        }
    }
}
