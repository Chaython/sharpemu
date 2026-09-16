// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

namespace SharpEmu.ShaderCompiler;

/// <summary>
/// Flags decomposed from a GCN5 MIMG sampling opcode name by
/// <see cref="Gen5ShaderTranslator.ParseImageOpcodeFlags"/>. The ISA builds
/// IMAGE_SAMPLE / IMAGE_GATHER4 names from a fixed modifier grammar —
/// <c>[C][S][modifier][O]</c> — where each letter is a token:
/// C = shadow/dref compare, S = signed compare, D = explicit derivatives,
/// B = LOD bias, L = explicit LOD, Cl = LOD clamp, Lz = LOD zero, O = per-lane
/// texel offset. GATHER4 carries an implicit G (gather) flag and has no
/// derivative variants. Case matters: the tokenizer must read
/// <c>ImageSampleCLz</c> as compare + lod-zero while <c>ImageSampleCl</c> is
/// the plain LOD-clamp form with no compare at all.
/// </summary>
public struct Gen5ImageOpcodeFlags : IEquatable<Gen5ImageOpcodeFlags>
{
    public bool Gather { get; set; }
    public bool Compare { get; set; }
    public bool SignedCompare { get; set; }
    public bool Offset { get; set; }
    public bool Bias { get; set; }
    public bool Lod { get; set; }
    public bool Derivatives { get; set; }
    public bool LodClamp { get; set; }
    public bool LodZero { get; set; }

    /// <summary>True when the opcode samples with a depth-reference operand
    /// (the shadow-compare "C" family).</summary>
    public readonly bool UsesDepthReference => Compare;

    public readonly bool Equals(Gen5ImageOpcodeFlags other) =>
        Gather == other.Gather &&
        Compare == other.Compare &&
        SignedCompare == other.SignedCompare &&
        Offset == other.Offset &&
        Bias == other.Bias &&
        Lod == other.Lod &&
        Derivatives == other.Derivatives &&
        LodClamp == other.LodClamp &&
        LodZero == other.LodZero;

    public override readonly bool Equals(object? obj) =>
        obj is Gen5ImageOpcodeFlags other && Equals(other);

    public override readonly int GetHashCode()
    {
        var hash = 0;
        if (Gather) hash |= 1;
        if (Compare) hash |= 2;
        if (SignedCompare) hash |= 4;
        if (Offset) hash |= 8;
        if (Bias) hash |= 16;
        if (Lod) hash |= 32;
        if (Derivatives) hash |= 64;
        if (LodClamp) hash |= 128;
        if (LodZero) hash |= 256;
        return hash;
    }
}
