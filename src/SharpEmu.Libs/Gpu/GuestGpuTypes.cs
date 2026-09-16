// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.Libs.Agc;

namespace SharpEmu.Libs.Gpu;

// The types that cross the guest-GPU backend seam. Every field is either a neutral
// primitive (dimensions, counts, host pixel bytes) or a raw guest/AGC value (guest
// addresses, guest format and number-type codes, guest register bitfields). Host
// graphics-API values must never appear here: each backend owns the guest -> native
// translation for its API.

/// <summary>A guest texture referenced by a draw or dispatch. Format/NumberType/
/// TileMode/DstSelect/Type are raw guest descriptor codes. Depth is the
/// normalized volume depth (one for non-3D resources).</summary>
internal sealed record GuestDrawTexture(
    ulong Address,
    uint Width,
    uint Height,
    uint Format,
    uint NumberType,
    byte[] RgbaPixels,
    bool IsFallback,
    bool IsStorage,
    uint MipLevels = 1,
    uint MipLevel = 0,
    uint BaseMipLevel = 0,
    uint ResourceMipLevels = 1,
    uint Pitch = 0,
    uint TileMode = 0,
    uint DstSelect = 0xFAC,
    GuestSampler Sampler = default,
    // Guest CPU write-tracker generation of the memory RgbaPixels was read
    // from; -1 when the range is untracked or the pixels were not read here.
    long WriteGeneration = -1,
    bool ArrayedView = false,
    uint ArrayLayers = 1,
    uint Type = 9,
    uint Depth = 1,
    // GPU-detile opt-in (SHARPEMU_GPU_DETILE): when Detile is non-null the AGC
    // layer skipped the CPU deswizzle and shipped the raw TILED bytes here in
    // TiledSource; the Vulkan backend detiles them on the GPU. RgbaPixels is
    // empty in that case. Both are neutral (no host graphics-API values).
    byte[]? TiledSource = null,
    DetileParams? Detile = null);

/// <summary>Raw guest sampler descriptor dwords, copied verbatim from guest memory.</summary>
internal readonly record struct GuestSampler(
    uint Word0,
    uint Word1,
    uint Word2,
    uint Word3);

/// <summary>Identity of a texture's content in a backend texture cache, keyed
/// entirely on raw guest descriptor values; the AGC layer uses it to skip texel
/// copies for content the backend already holds. The sampler field is carried
/// for callers that need it but is deliberately EXCLUDED from equality: every
/// backend resolves samplers separately from the per-draw descriptor (Vulkan
/// creates them at descriptor-write time from the texture's SamplerState;
/// Metal builds MTLSamplerState objects per draw), so the same texture sampled
/// with point AND linear filters is one cached image plus one upload, not two
/// full images and two device-memory allocations.</summary>
internal readonly record struct TextureContentIdentity(
    ulong Address,
    uint Width,
    uint Height,
    uint Format,
    uint NumberType,
    uint DstSelect,
    uint TileMode,
    uint Pitch,
    GuestSampler Sampler,
    bool Arrayed = false,
    uint ArrayLayers = 1,
    uint Type = 9,
    uint Depth = 1)
{
    // Content identity covers exactly the fields that affect the decoded
    // texels; the sampler changes how texels are READ, never what they are.
    public bool Equals(TextureContentIdentity other) =>
        Address == other.Address &&
        Width == other.Width &&
        Height == other.Height &&
        Format == other.Format &&
        NumberType == other.NumberType &&
        DstSelect == other.DstSelect &&
        TileMode == other.TileMode &&
        Pitch == other.Pitch &&
        Arrayed == other.Arrayed &&
        ArrayLayers == other.ArrayLayers &&
        Type == other.Type &&
        Depth == other.Depth;

    public override int GetHashCode() =>
        HashCode.Combine(
            HashCode.Combine(Address, Width, Height, Format),
            HashCode.Combine(NumberType, DstSelect, TileMode, Pitch),
            HashCode.Combine(Arrayed, ArrayLayers, Type, Depth));
}

internal sealed record GuestMemoryBuffer(
    ulong BaseAddress,
    byte[] Data,
    int Length,
    bool Pooled,
    bool Writable = false,
    bool WriteBackToGuest = true);

/// <summary>DataFormat/NumberFormat are raw guest vertex-attribute codes.</summary>
internal sealed record GuestVertexBuffer(
    uint Location,
    uint ComponentCount,
    uint DataFormat,
    uint NumberFormat,
    ulong BaseAddress,
    uint Stride,
    uint OffsetBytes,
    byte[] Data,
    int Length,
    bool Pooled,
    bool PerInstance = false,
    uint BaseRecord = 0)
{
    public ulong BindingOffsetBytes => (ulong)BaseRecord * Stride;
}

internal sealed record GuestIndexBuffer(
    byte[] Data,
    int Length,
    bool Is32Bit,
    bool Pooled);

internal readonly record struct GuestRect(
    int X,
    int Y,
    uint Width,
    uint Height);

internal readonly record struct GuestViewport(
    float X,
    float Y,
    float Width,
    float Height,
    float MinDepth,
    float MaxDepth);

internal readonly record struct GuestRasterState(
    bool CullFront,
    bool CullBack,
    bool FrontFaceClockwise,
    bool Wireframe)
{
    public static GuestRasterState Default { get; } = new(false, false, false, false);
}

// CompareOp uses the GCN DB_DEPTH_CONTROL ZFUNC encoding, which matches the
// Vulkan CompareOp ordering (0=Never through 7=Always).
internal readonly record struct GuestDepthState(
    bool TestEnable,
    bool WriteEnable,
    uint CompareOp,
    bool ClearEnable = false)
{
    public static GuestDepthState Default { get; } = new(false, false, 7, false);
}

/// <summary>Stencil state for one face. CompareFunc and the three ops use the
/// GCN DB_DEPTH_CONTROL encodings, which match the host orderings: func
/// 0=Never..7=Always (same as CompareOp), op 0=Keep, 1=Zero, 2=Replace,
/// 3=IncrementClamp, 4=DecrementClamp, 5=Invert, 6=IncrementWrap,
/// 7=DecrementWrap (same as Vulkan StencilOp / MTLStencilOperation).
/// Reference/CompareMask/WriteMask are the DB_STENCILREFMASK(_BF) bytes;
/// the back face carries its own copy.</summary>
internal readonly record struct GuestStencilFace(
    uint CompareFunc,
    uint FailOp,
    uint DepthFailOp,
    uint PassOp,
    uint Reference,
    uint CompareMask,
    uint WriteMask)
{
    public static GuestStencilFace Default { get; } = new(7, 0, 0, 0, 0, 0xFF, 0xFF);
}

/// <summary>Guest stencil state decoded from DB_DEPTH_CONTROL (enables, funcs,
/// ops) and DB_STENCILREFMASK / DB_STENCILREFMASK_BF (per-face reference and
/// compare/write masks). Back is only honored by backends when BackfaceEnable
/// is set; otherwise the front face state applies to both faces.</summary>
internal readonly record struct GuestStencilState(
    bool TestEnable,
    bool BackfaceEnable,
    GuestStencilFace Front,
    GuestStencilFace Back)
{
    public static GuestStencilState Default { get; } =
        new(false, false, GuestStencilFace.Default, GuestStencilFace.Default);

    /// <summary>Pipeline-identity projection: exactly the fields a backend
    /// bakes into a graphics pipeline (enables, compare funcs, ops). Reference
    /// and compare/write masks are deliberately excluded — backends bind them
    /// as dynamic per-draw state so updating them never rebuilds a pipeline.</summary>
    public GuestStencilPipelineState PipelineIdentity => new(
        TestEnable,
        BackfaceEnable,
        Front.CompareFunc,
        Front.FailOp,
        Front.DepthFailOp,
        Front.PassOp,
        Back.CompareFunc,
        Back.FailOp,
        Back.DepthFailOp,
        Back.PassOp);
}

/// <summary>The pipeline-baked slice of <see cref="GuestStencilState"/>;
/// see <see cref="GuestStencilState.PipelineIdentity"/>.</summary>
internal readonly record struct GuestStencilPipelineState(
    bool TestEnable,
    bool BackfaceEnable,
    uint FrontFunc,
    uint FrontFailOp,
    uint FrontDepthFailOp,
    uint FrontPassOp,
    uint BackFunc,
    uint BackFailOp,
    uint BackDepthFailOp,
    uint BackPassOp)
{
    public static GuestStencilPipelineState Default { get; } =
        new(false, false, 7, 0, 0, 0, 7, 0, 0, 0);
}

/// <summary>Factors/funcs are raw guest CB_BLEND*_CONTROL register bitfields; the
/// defaults (1/0) are the guest ONE/ZERO codes.</summary>
internal readonly record struct GuestBlendState(
    bool Enable,
    uint ColorSrcFactor,
    uint ColorDstFactor,
    uint ColorFunc,
    uint AlphaSrcFactor,
    uint AlphaDstFactor,
    uint AlphaFunc,
    bool SeparateAlphaBlend,
    uint WriteMask)
{
    public static GuestBlendState Default { get; } = new(
        Enable: false,
        ColorSrcFactor: 1,
        ColorDstFactor: 0,
        ColorFunc: 0,
        AlphaSrcFactor: 1,
        AlphaDstFactor: 0,
        AlphaFunc: 0,
        SeparateAlphaBlend: false,
        WriteMask: 0xFu);
}

/// <summary>CB_BLEND_RED..ALPHA: the constant color referenced by the
/// CONSTANT_COLOR / CONSTANT_ALPHA blend factors. One constant serves every
/// render target of a draw; the hardware reset value is transparent black.</summary>
internal readonly record struct GuestBlendConstant(
    float Red,
    float Green,
    float Blue,
    float Alpha);

internal sealed record GuestRenderState(
    IReadOnlyList<GuestBlendState> Blends,
    GuestRect? Scissor,
    GuestViewport? Viewport,
    GuestRasterState Raster,
    GuestDepthState Depth,
    GuestStencilState Stencil,
    GuestBlendConstant BlendConstant = default)
{
    public static GuestRenderState Default { get; } = new(
        [GuestBlendState.Default],
        Scissor: null,
        Viewport: null,
        GuestRasterState.Default,
        GuestDepthState.Default,
        GuestStencilState.Default);

    public GuestBlendState Blend =>
        Blends.Count == 0 ? GuestBlendState.Default : Blends[0];
}

/// <summary>Format/NumberType are raw guest render-target register codes.
/// SampleCount is the sample count decoded from CB_COLORn_ATTRIB.NUM_SAMPLES
/// (1/2/4/8); backends clamp it to what their backing actually allocates
/// (see the MSAA notes in the presenters).</summary>
internal sealed record GuestRenderTarget(
    ulong Address,
    uint Width,
    uint Height,
    uint Format,
    uint NumberType,
    uint MipLevels = 1,
    uint SampleCount = 1);

/// <summary>Guest DB surface bound alongside a color render target.</summary>
internal sealed record GuestDepthTarget(
    ulong ReadAddress,
    ulong WriteAddress,
    uint Width,
    uint Height,
    uint GuestFormat,
    uint SwizzleMode,
    float ClearDepth,
    bool ReadOnly)
{
    public ulong Address => WriteAddress != 0 ? WriteAddress : ReadAddress;
}
