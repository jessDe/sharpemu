// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Buffers.Binary;
using SharpEmu.HLE;
using SharpEmu.Libs.Font;
using Xunit;

namespace SharpEmu.Libs.Tests.Font;

public sealed class FontExportsTests
{
    private const ulong Base = 0x1_0000_0000;
    private const ulong LayoutAddress = Base + 0x100;

    private readonly FakeCpuMemory _memory = new(Base, 0x4000);
    private readonly CpuContext _ctx;

    public FontExportsTests()
    {
        _ctx = new CpuContext(_memory, Generation.Gen5);
    }

    // SceFontHorizontalLayout is three floats; the sentinel directly after
    // them must survive the call.
    [Fact]
    public void GetHorizontalLayout_WritesExactlyThreeFloats()
    {
        const uint Sentinel = 0xDEADBEEF;
        Span<byte> sentinelBytes = stackalloc byte[sizeof(uint)];
        BinaryPrimitives.WriteUInt32LittleEndian(sentinelBytes, Sentinel);
        Assert.True(_ctx.Memory.TryWrite(LayoutAddress + 12, sentinelBytes));

        _ctx[CpuRegister.Rsi] = LayoutAddress;
        Assert.Equal(0, FontExports.GetHorizontalLayout(_ctx));

        Span<byte> layout = stackalloc byte[16];
        Assert.True(_ctx.Memory.TryRead(LayoutAddress, layout));
        Assert.Equal(12.0f, BinaryPrimitives.ReadSingleLittleEndian(layout));
        Assert.Equal(16.0f, BinaryPrimitives.ReadSingleLittleEndian(layout[4..]));
        Assert.Equal(0.0f, BinaryPrimitives.ReadSingleLittleEndian(layout[8..]));
        Assert.Equal(Sentinel, BinaryPrimitives.ReadUInt32LittleEndian(layout[12..]));
    }

    [Fact]
    public void GetVerticalLayout_WritesExactlyThreeFloats()
    {
        const uint Sentinel = 0xDEADBEEF;
        Span<byte> sentinelBytes = stackalloc byte[sizeof(uint)];
        BinaryPrimitives.WriteUInt32LittleEndian(sentinelBytes, Sentinel);
        Assert.True(_ctx.Memory.TryWrite(LayoutAddress + 12, sentinelBytes));

        _ctx[CpuRegister.Rsi] = LayoutAddress;
        Assert.Equal(0, FontExports.GetVerticalLayout(_ctx));

        Span<byte> layout = stackalloc byte[16];
        Assert.True(_ctx.Memory.TryRead(LayoutAddress, layout));
        Assert.Equal(12.0f, BinaryPrimitives.ReadSingleLittleEndian(layout));
        Assert.Equal(16.0f, BinaryPrimitives.ReadSingleLittleEndian(layout[4..]));
        Assert.Equal(0.0f, BinaryPrimitives.ReadSingleLittleEndian(layout[8..]));
        Assert.Equal(Sentinel, BinaryPrimitives.ReadUInt32LittleEndian(layout[12..]));
    }

    [Fact]
    public void GetVerticalLayout_RejectsNullOutput()
    {
        _ctx[CpuRegister.Rsi] = 0;

        Assert.Equal(
            (int)OrbisGen2Result.ORBIS_GEN2_ERROR_INVALID_ARGUMENT,
            FontExports.GetVerticalLayout(_ctx));
    }

    [Fact]
    public void GenerateCharGlyph_WritesOnlyOpaqueHandle()
    {
        const ulong glyphStorage = Base + 0x300;
        const ulong glyphOut = Base + 0x400;
        const ulong sentinel = 0xD15EA5E5CAFEBABE;
        Span<byte> bytes = stackalloc byte[16];
        BinaryPrimitives.WriteUInt64LittleEndian(bytes[8..], sentinel);
        Assert.True(_memory.TryWrite(glyphOut, bytes));
        _ctx[CpuRegister.Rdx] = glyphStorage;
        _ctx[CpuRegister.Rcx] = glyphOut;

        Assert.Equal(0, FontExports.GenerateCharGlyph(_ctx));

        Assert.True(_memory.TryRead(glyphOut, bytes));
        Assert.Equal(glyphStorage, BinaryPrimitives.ReadUInt64LittleEndian(bytes));
        Assert.Equal(sentinel, BinaryPrimitives.ReadUInt64LittleEndian(bytes[8..]));
    }

    [Fact]
    public void RenderCharGlyphImageHorizontal_WritesMetricsAndSurfacePixels()
    {
        const ulong surface = Base + 0x500;
        const ulong pixels = Base + 0x1000;
        const ulong metrics = Base + 0x600;
        Span<byte> descriptor = stackalloc byte[0x28];
        BinaryPrimitives.WriteUInt64LittleEndian(descriptor, pixels);
        BinaryPrimitives.WriteUInt32LittleEndian(descriptor[8..], 64); // stride
        BinaryPrimitives.WriteUInt32LittleEndian(descriptor[12..], 1); // pixel bytes
        BinaryPrimitives.WriteUInt32LittleEndian(descriptor[16..], 64);
        BinaryPrimitives.WriteUInt32LittleEndian(descriptor[20..], 32);
        Assert.True(_memory.TryWrite(surface, descriptor));
        _ctx[CpuRegister.Rsi] = 'A';
        _ctx[CpuRegister.Rdx] = surface;
        _ctx[CpuRegister.Rcx] = metrics;
        _ctx.SetXmmRegister(0, BitConverter.SingleToUInt32Bits(2.0f), 0);
        _ctx.SetXmmRegister(1, BitConverter.SingleToUInt32Bits(14.0f), 0);

        Assert.Equal(0, FontExports.RenderCharGlyphImageHorizontal(_ctx));

        Span<byte> metricBytes = stackalloc byte[32];
        Assert.True(_memory.TryRead(metrics, metricBytes));
        Assert.Equal(8.0f, BinaryPrimitives.ReadSingleLittleEndian(metricBytes));
        Span<byte> surfacePixels = stackalloc byte[64 * 32];
        Assert.True(_memory.TryRead(pixels, surfacePixels));
        Assert.Contains((byte)0xFF, surfacePixels.ToArray());
    }
}
