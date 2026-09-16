// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace SharpEmu.Libs.Gpu.NativeVulkan;

internal enum NativeGpuResult
{
    Success = 0,
    NotFound = 1,
    NotReady = 2,
    InvalidArgument = -1,
    IncompatibleAbi = -2,
    PlatformError = -3,
    VulkanError = -4,
    OutOfMemory = -5,
    InternalError = -6,
}

internal static unsafe partial class NativeVulkanApi
{
    internal const uint AbiVersion = 1;
    private const string Library = "sharpemu_gpu_vulkan";

    internal static string LibraryFileName =>
        OperatingSystem.IsWindows() ? "sharpemu_gpu_vulkan.dll" :
        OperatingSystem.IsMacOS() ? "libsharpemu_gpu_vulkan.dylib" :
        "libsharpemu_gpu_vulkan.so";

    internal static bool IsAvailable(out string error)
    {
        if (NativeLibrary.TryLoad(
                Library,
                typeof(NativeVulkanApi).Assembly,
                DllImportSearchPath.SafeDirectories | DllImportSearchPath.AssemblyDirectory,
                out var handle))
        {
            NativeLibrary.Free(handle);
            error = string.Empty;
            return true;
        }

        error =
            $"The optional native Vulkan backend was selected, but {LibraryFileName} could not be loaded. " +
            "Place the library from https://github.com/sharpemu/sharpemu.vulkan next to the SharpEmu executable " +
            "and ensure its SDL3 and Vulkan runtime dependencies are installed.";
        return false;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct CreateInfo
    {
        internal uint StructSize;
        internal uint AbiVersion;
        internal uint Width;
        internal uint Height;
        internal uint EnableValidation;
        internal byte* TitleUtf8;
        internal delegate* unmanaged[Cdecl]<int, byte*, void*, void> Log;
        internal void* LogUser;
    }

    [StructLayout(LayoutKind.Sequential)] internal struct Bytes { internal void* Data; internal nuint Size; }
    [StructLayout(LayoutKind.Sequential)] internal struct Sampler { internal fixed uint Words[4]; }
    [StructLayout(LayoutKind.Sequential)]
    internal struct Texture
    {
        internal uint StructSize; internal ulong Address; internal uint Width, Height, Format, NumberType;
        internal Bytes RgbaPixels; internal uint IsFallback, IsStorage, MipLevels, MipLevel;
        internal uint Pitch, TileMode, DstSelect; internal Sampler SamplerState;
    }
    [StructLayout(LayoutKind.Sequential)] internal struct MemoryBuffer { internal ulong Address; internal Bytes Data; }
    [StructLayout(LayoutKind.Sequential)]
    internal struct VertexBuffer
    {
        internal uint StructSize, Location, ComponentCount, DataFormat, NumberFormat;
        internal ulong Address; internal uint Stride, OffsetBytes; internal Bytes Data;
    }
    [StructLayout(LayoutKind.Sequential)] internal struct IndexBuffer { internal Bytes Data; internal uint Is32Bit; }
    [StructLayout(LayoutKind.Sequential)] internal struct Rect { internal int X, Y; internal uint Width, Height; }
    [StructLayout(LayoutKind.Sequential)]
    internal struct Viewport { internal float X, Y, Width, Height, MinDepth, MaxDepth; }
    [StructLayout(LayoutKind.Sequential)]
    internal struct Blend
    {
        internal uint Enable, ColorSrc, ColorDst, ColorFunc, AlphaSrc, AlphaDst, AlphaFunc;
        internal uint SeparateAlpha, WriteMask;
    }
    [StructLayout(LayoutKind.Sequential)]
    internal struct RenderTarget
    {
        internal uint StructSize; internal ulong Address;
        internal uint Width, Height, Format, NumberType, MipLevels;
    }
    [StructLayout(LayoutKind.Sequential)]
    internal struct Draw
    {
        internal uint StructSize, Width, Height; internal Bytes VertexSpirv, PixelSpirv;
        internal Texture* Textures; internal uint TextureCount;
        internal MemoryBuffer* MemoryBuffers; internal uint MemoryBufferCount;
        internal VertexBuffer* VertexBuffers; internal uint VertexBufferCount;
        internal RenderTarget* Targets; internal uint TargetCount;
        internal Blend* Blends; internal uint BlendCount;
        internal IndexBuffer* IndexBuffer; internal Rect* Scissor; internal Viewport* ViewportState;
        internal uint AttributeCount, VertexCount, InstanceCount, PrimitiveType, PublishTargets;
    }
    [StructLayout(LayoutKind.Sequential)]
    internal struct Compute
    {
        internal uint StructSize; internal ulong ShaderAddress; internal Bytes Spirv;
        internal Texture* Textures; internal uint TextureCount;
        internal MemoryBuffer* MemoryBuffers; internal uint MemoryBufferCount;
        internal uint GroupsX, GroupsY, GroupsZ;
    }
    [StructLayout(LayoutKind.Sequential)]
    internal struct Input
    {
        internal uint StructSize, KeyboardFocused;
        internal fixed uint VirtualKeys[8];
        internal uint GamepadConnected, GamepadButtons;
        internal byte LeftX, LeftY, RightX, RightY, LeftTrigger, RightTrigger;
        internal fixed byte Reserved[2]; internal fixed byte GamepadNameUtf8[128];
    }

    [LibraryImport(Library, EntryPoint = "se_gpu_abi_version")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial uint GetAbiVersion();

    [LibraryImport(Library, EntryPoint = "se_gpu_last_error")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    private static partial byte* LastError(nint backend);

    [LibraryImport(Library, EntryPoint = "se_gpu_create")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial NativeGpuResult Create(CreateInfo* info, out nint backend);

    [LibraryImport(Library, EntryPoint = "se_gpu_destroy")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial void Destroy(nint backend);

    [LibraryImport(Library, EntryPoint = "se_gpu_poll")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial NativeGpuResult Poll(nint backend, out uint shouldClose);

    [LibraryImport(Library, EntryPoint = "se_gpu_input_snapshot")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial NativeGpuResult InputSnapshot(nint backend, Input* input);

    [LibraryImport(Library, EntryPoint = "se_gpu_present_bgra")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial NativeGpuResult PresentBgra(
        nint backend,
        void* pixels,
        nuint size,
        uint width,
        uint height,
        uint pitch);

    [LibraryImport(Library, EntryPoint = "se_gpu_submit_draw")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial NativeGpuResult SubmitDraw(nint backend, Draw* draw);

    [LibraryImport(Library, EntryPoint = "se_gpu_submit_compute")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial NativeGpuResult SubmitCompute(nint backend, Compute* compute);

    [LibraryImport(Library, EntryPoint = "se_gpu_register_display_buffer")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial NativeGpuResult RegisterDisplayBuffer(nint backend, ulong address, uint format);

    [LibraryImport(Library, EntryPoint = "se_gpu_present_guest_image")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial NativeGpuResult PresentGuestImage(
        nint backend, ulong address, uint width, uint height, uint pitch);

    [LibraryImport(Library, EntryPoint = "se_gpu_has_guest_image")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial NativeGpuResult HasGuestImage(nint backend, ulong address, uint format, uint numberType);

    [LibraryImport(Library, EntryPoint = "se_gpu_blit_guest_image")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial NativeGpuResult BlitGuestImage(
        nint backend, ulong sourceAddress, uint sourceWidth, uint sourceHeight, uint sourceFormat,
        ulong destinationAddress, uint destinationWidth, uint destinationHeight, uint destinationFormat);

    [LibraryImport(Library, EntryPoint = "se_gpu_render_target_output_kind")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial NativeGpuResult RenderTargetOutputKind(uint format, uint numberType, out uint outputKind);

    internal static string GetError(nint backend)
    {
        var pointer = LastError(backend);
        return pointer is null ? "Unknown native GPU error" : Marshal.PtrToStringUTF8((nint)pointer)!;
    }
}
