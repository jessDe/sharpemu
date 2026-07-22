// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Buffers.Binary;
using SharpEmu.HLE;
using SharpEmu.Libs.Agc;
using Xunit;

namespace SharpEmu.Libs.Tests.Agc;

public sealed class AgcFusedShaderTests
{
    private const ulong BaseAddress = 0x1000;
    private const ulong FrontShader = 0x1200;
    private const ulong BackShader = 0x1300;
    private const ulong FusedShader = 0x1400;
    private const ulong ScratchRegisters = 0x1500;
    private const ulong FrontRegisters = 0x1600;
    private const ulong BackRegisters = 0x1700;
    private const ulong FrontCode = 0x1234_5678_9A00;
    private const ulong BackCode = 0x2233_4455_6600;

    [Fact]
    public void GetFusedShaderSize_ReportsBackRegisterStorage()
    {
        var memory = new FakeCpuMemory(BaseAddress, 0x1000);
        WriteByte(memory, FrontShader + 0x5A, 4);
        WriteByte(memory, BackShader + 0x5A, 6);
        WriteByte(memory, BackShader + 0x5C, 5);
        var ctx = Context(memory, 0x1100, FrontShader, BackShader);

        var result = AgcExports.GetFusedShaderSize(ctx);

        Assert.Equal(0, result);
        Assert.Equal(40ul, ReadUInt64(memory, 0x1100));
        Assert.Equal(4ul, ReadUInt64(memory, 0x1108));
    }

    [Fact]
    public void FuseShaderHalves_CopiesBackAndPatchesFrontGeometryState()
    {
        var memory = new FakeCpuMemory(BaseAddress, 0x1000);
        WriteByte(memory, FrontShader + 0x5A, 4);
        WriteByte(memory, BackShader + 0x5A, 6);
        WriteByte(memory, FrontShader + 0x5C, 4);
        WriteByte(memory, BackShader + 0x5C, 4);
        WriteUInt64(memory, FrontShader + 0x10, FrontCode);
        WriteUInt64(memory, BackShader + 0x10, BackCode);
        WriteUInt64(memory, FrontShader + 0x20, FrontRegisters);
        WriteUInt64(memory, BackShader + 0x20, BackRegisters);
        WriteUInt64(memory, BackShader + 0x08, 0xDEAD_BEEF);

        WriteRegister(memory, FrontRegisters + 0x00, 0x80, 0x1111);
        WriteRegister(memory, FrontRegisters + 0x08, 0x80, 0x2222);
        WriteRegister(memory, FrontRegisters + 0x10, 0xC8, 0);
        WriteRegister(memory, FrontRegisters + 0x18, 0xC9, 0xAABB_CC00);
        WriteRegister(memory, BackRegisters + 0x00, 0x80, 0xAAAA);
        WriteRegister(memory, BackRegisters + 0x08, 0x80, 0xBBBB);
        WriteRegister(memory, BackRegisters + 0x10, 0xC8, 0);
        WriteRegister(memory, BackRegisters + 0x18, 0xC9, 0x5566_7700);

        var ctx = Context(
            memory,
            FusedShader,
            FrontShader,
            BackShader,
            ScratchRegisters);

        var result = AgcExports.FuseShaderHalves(ctx);

        Assert.Equal(0, result);
        Assert.Equal((byte)2, ReadByte(memory, FusedShader + 0x5A));
        Assert.Equal(0ul, ReadUInt64(memory, FusedShader + 0x08));
        Assert.Equal(ScratchRegisters, ReadUInt64(memory, FusedShader + 0x20));
        Assert.Equal(0x1111u, ReadUInt32(memory, ScratchRegisters + 0x04));
        Assert.Equal(0x2222u, ReadUInt32(memory, ScratchRegisters + 0x0C));
        Assert.Equal(
            unchecked((uint)(FrontCode >> 8)),
            ReadUInt32(memory, ScratchRegisters + 0x14));
        Assert.Equal(
            0x5566_7700u | (uint)((FrontCode >> 40) & 0xFF),
            ReadUInt32(memory, ScratchRegisters + 0x1C));

        var resolvedShader = AgcExports.ResolveExportShaderForTranslation(
            FrontCode,
            out var userDataScalarRegisterBase);
        Assert.Equal(BackCode, resolvedShader);
        Assert.Equal(0u, userDataScalarRegisterBase);
    }

    [Fact]
    public void ResolveExportShaderForTranslation_KeepsMergedNggAbiForUnfusedShader()
    {
        const ulong unfusedShader = 0x3456_789A_BC00;

        var resolvedShader = AgcExports.ResolveExportShaderForTranslation(
            unfusedShader,
            out var userDataScalarRegisterBase);

        Assert.Equal(unfusedShader, resolvedShader);
        Assert.Equal(8u, userDataScalarRegisterBase);
    }

    private static CpuContext Context(
        FakeCpuMemory memory,
        ulong rdi,
        ulong rsi,
        ulong rdx,
        ulong rcx = 0)
    {
        var ctx = new CpuContext(memory, Generation.Gen5);
        ctx[CpuRegister.Rdi] = rdi;
        ctx[CpuRegister.Rsi] = rsi;
        ctx[CpuRegister.Rdx] = rdx;
        ctx[CpuRegister.Rcx] = rcx;
        return ctx;
    }

    private static void WriteRegister(
        FakeCpuMemory memory,
        ulong address,
        uint offset,
        uint value)
    {
        WriteUInt32(memory, address, offset);
        WriteUInt32(memory, address + sizeof(uint), value);
    }

    private static void WriteByte(FakeCpuMemory memory, ulong address, byte value) =>
        Assert.True(memory.TryWrite(address, new byte[] { value }));

    private static void WriteUInt32(FakeCpuMemory memory, ulong address, uint value)
    {
        Span<byte> bytes = stackalloc byte[sizeof(uint)];
        BinaryPrimitives.WriteUInt32LittleEndian(bytes, value);
        Assert.True(memory.TryWrite(address, bytes));
    }

    private static void WriteUInt64(FakeCpuMemory memory, ulong address, ulong value)
    {
        Span<byte> bytes = stackalloc byte[sizeof(ulong)];
        BinaryPrimitives.WriteUInt64LittleEndian(bytes, value);
        Assert.True(memory.TryWrite(address, bytes));
    }

    private static byte ReadByte(FakeCpuMemory memory, ulong address)
    {
        Span<byte> bytes = stackalloc byte[1];
        Assert.True(memory.TryRead(address, bytes));
        return bytes[0];
    }

    private static uint ReadUInt32(FakeCpuMemory memory, ulong address)
    {
        Span<byte> bytes = stackalloc byte[sizeof(uint)];
        Assert.True(memory.TryRead(address, bytes));
        return BinaryPrimitives.ReadUInt32LittleEndian(bytes);
    }

    private static ulong ReadUInt64(FakeCpuMemory memory, ulong address)
    {
        Span<byte> bytes = stackalloc byte[sizeof(ulong)];
        Assert.True(memory.TryRead(address, bytes));
        return BinaryPrimitives.ReadUInt64LittleEndian(bytes);
    }
}
