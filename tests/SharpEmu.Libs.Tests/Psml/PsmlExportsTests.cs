// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.HLE;
using SharpEmu.Libs.Psml;
using Xunit;

namespace SharpEmu.Libs.Tests.Psml;

public sealed class PsmlExportsTests
{
    private const ulong MemoryBase = 0x1_0000_0000;

    [Fact]
    public void MfsrInit_ReturnsSuccess()
    {
        var context = new CpuContext(new FakeCpuMemory(0x1_0000_0000, 0x1000), Generation.Gen5);
        context[CpuRegister.Rax] = unchecked((ulong)(long)(int)OrbisGen2Result.ORBIS_GEN2_ERROR_NOT_FOUND);

        Assert.Equal(0, PsmlExports.MfsrInit(context));
        Assert.Equal(0UL, context[CpuRegister.Rax]);
    }

    [Fact]
    public void MfsrInit_RegistersForPs5UnderObservedNid()
    {
        var manager = new ModuleManager();
        manager.RegisterExports(SharpEmu.Generated.SysAbiExportRegistry.CreateExports(Generation.Gen5));

        Assert.True(manager.TryGetExport("3WVD91e12ZQ", out var export));
        Assert.Equal("scePsmlMfsrInit", export.Name);
        Assert.Equal("libScePsml", export.LibraryName);
    }

    [Fact]
    public void GetSharedResourcesInitRequirement_WritesUsableAllocationShape()
    {
        var memory = new FakeCpuMemory(MemoryBase, 0x1000);
        var context = new CpuContext(memory, Generation.Gen5);
        context[CpuRegister.Rdi] = MemoryBase;

        Assert.Equal(0, PsmlExports.MfsrGetSharedResourcesInitRequirement(context));
        Assert.Equal(0UL, context[CpuRegister.Rax]);

        Span<byte> requirement = stackalloc byte[3 * sizeof(ulong)];
        Assert.True(memory.TryRead(MemoryBase, requirement));
        Assert.Equal(0x1_0000UL, System.Buffers.Binary.BinaryPrimitives.ReadUInt64LittleEndian(requirement));
        Assert.Equal(0x1_0000UL, System.Buffers.Binary.BinaryPrimitives.ReadUInt64LittleEndian(requirement[8..]));
        Assert.Equal(1UL, System.Buffers.Binary.BinaryPrimitives.ReadUInt64LittleEndian(requirement[16..]));
    }

    [Fact]
    public void GetSharedResourcesInitRequirement_RejectsMissingOutput()
    {
        var context = new CpuContext(new FakeCpuMemory(MemoryBase, 0x1000), Generation.Gen5);

        Assert.Equal(
            (int)OrbisGen2Result.ORBIS_GEN2_ERROR_INVALID_ARGUMENT,
            PsmlExports.MfsrGetSharedResourcesInitRequirement(context));
    }

    [Fact]
    public void CreateSharedResources_ReturnsSuccessAndRegisters()
    {
        var context = new CpuContext(new FakeCpuMemory(MemoryBase, 0x1000), Generation.Gen5);
        Assert.Equal(0, PsmlExports.MfsrCreateSharedResources(context));

        var manager = new ModuleManager();
        manager.RegisterExports(SharpEmu.Generated.SysAbiExportRegistry.CreateExports(Generation.Gen5));
        Assert.True(manager.TryGetExport("eWoKNeB6V-k", out var export));
        Assert.Equal("scePsmlMfsrCreateSharedResources", export.Name);
        Assert.Equal("libScePsml", export.LibraryName);
    }

    [Fact]
    public void Context800M32CompatibilityExports_RegisterAndSucceed()
    {
        var memory = new FakeCpuMemory(MemoryBase, 0x1000);
        var context = new CpuContext(memory, Generation.Gen5);
        context[CpuRegister.Rdi] = MemoryBase;
        Assert.Equal(0, PsmlExports.MfsrGetContextBufferRequirement800M3_2(context));
        Assert.Equal(0, PsmlExports.MfsrCreateContext800M3_2(context));

        Span<byte> requirement = stackalloc byte[3 * sizeof(ulong)];
        Assert.True(memory.TryRead(MemoryBase, requirement));
        Assert.Equal(0x1_0000UL, System.Buffers.Binary.BinaryPrimitives.ReadUInt64LittleEndian(requirement));
        Assert.Equal(0x1_0000UL, System.Buffers.Binary.BinaryPrimitives.ReadUInt64LittleEndian(requirement[8..]));
        Assert.Equal(1UL, System.Buffers.Binary.BinaryPrimitives.ReadUInt64LittleEndian(requirement[16..]));

        var manager = new ModuleManager();
        manager.RegisterExports(SharpEmu.Generated.SysAbiExportRegistry.CreateExports(Generation.Gen5));
        Assert.True(manager.TryGetExport("ArakEpzsZo0", out _));
        Assert.True(manager.TryGetExport("gxv3i+MTEzU", out _));
    }

    [Fact]
    public void DispatchPacketCompatibilityExports_ReserveNoNativeGpuWordsAndSucceed()
    {
        var memory = new FakeCpuMemory(MemoryBase, 0x1000);
        var context = new CpuContext(memory, Generation.Gen5);
        context[CpuRegister.Rsi] = MemoryBase;
        Assert.True(context.TryWriteUInt32(MemoryBase, uint.MaxValue));

        Assert.Equal(0, PsmlExports.MfsrGetDispatchMfsrPacketSizeInDwords(context));
        Assert.True(context.TryReadUInt32(MemoryBase, out var packetSize));
        Assert.Equal(0U, packetSize);
        Assert.Equal(0, PsmlExports.MfsrGetDispatchMfsrPacket900(context));
        Assert.Equal(0, PsmlExports.MfsrGetDispatchMfsrPacket1000(context));
        Assert.Equal(0, PsmlExports.MfsrGetDispatchMfsrPacket1100(context));

        var manager = new ModuleManager();
        manager.RegisterExports(SharpEmu.Generated.SysAbiExportRegistry.CreateExports(Generation.Gen5));
        Assert.True(manager.TryGetExport("AHalTX9wFZY", out _));
        Assert.True(manager.TryGetExport("RUNLFro+qok", out _));
        Assert.True(manager.TryGetExport("s2psNHUIdjk", out _));
        Assert.True(manager.TryGetExport("94iBp3KvIuI", out _));
    }
}
