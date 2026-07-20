// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Buffers.Binary;
using SharpEmu.HLE;
using SharpEmu.Libs.AvPlayer;
using Xunit;

namespace SharpEmu.Libs.Tests.AvPlayer;

public sealed class AvPlayerStreamInfoTests
{
    private const string StreamInfoExNid = "ctTAcF5DiKQ";
    private const ulong BaseAddress = 0x1_0000_0000;
    private const int MemorySize = 0x2000;
    private const ulong InfoAddress = BaseAddress + 0x100;
    private const ulong Handle = 0xA0_0000_0001;
    private const ulong DurationMilliseconds = 0x0102_0304_0506_0708;
    private const byte Sentinel = 0xAB;

    [Theory]
    [InlineData(0u)]
    [InlineData(1u)]
    public void GetStreamInfoExDoesNotWritePastThe32ByteStructure(uint streamIndex)
    {
        var memory = new FakeCpuMemory(BaseAddress, MemorySize);
        var context = new CpuContext(memory, Generation.Gen5);

        AvPlayerExports.RegisterPlayerForTest(Handle, 1280, 720, DurationMilliseconds);
        try
        {
            Span<byte> window = stackalloc byte[40];
            window.Fill(Sentinel);
            Assert.True(memory.TryWrite(InfoAddress, window));

            context[CpuRegister.Rdi] = Handle;
            context[CpuRegister.Rsi] = streamIndex;
            context[CpuRegister.Rdx] = InfoAddress;

            var resultCode = AvPlayerExports.AvPlayerGetStreamInfoEx(context);
            Assert.Equal(0, resultCode);

            Span<byte> result = stackalloc byte[40];
            Assert.True(memory.TryRead(InfoAddress, result));
            Assert.Equal(streamIndex, BinaryPrimitives.ReadUInt32LittleEndian(result));
            if (streamIndex == 0)
            {
                Assert.Equal(1280u, BinaryPrimitives.ReadUInt32LittleEndian(result[8..]));
                Assert.Equal(720u, BinaryPrimitives.ReadUInt32LittleEndian(result[12..]));
            }
            else
            {
                Assert.Equal(2, BinaryPrimitives.ReadUInt16LittleEndian(result[8..]));
                Assert.Equal(48_000u, BinaryPrimitives.ReadUInt32LittleEndian(result[12..]));
            }
            Assert.Equal(DurationMilliseconds, BinaryPrimitives.ReadUInt64LittleEndian(result[24..]));

            for (var index = 32; index < result.Length; index++)
            {
                Assert.Equal(Sentinel, result[index]);
            }
        }
        finally
        {
            AvPlayerExports.RemovePlayerForTest(Handle);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void GetStreamInfoFunctionsRejectInvalidArguments(bool useExtendedFunction)
    {
        var memory = new FakeCpuMemory(BaseAddress, MemorySize);
        var context = new CpuContext(memory, Generation.Gen5);
        AvPlayerExports.RegisterPlayerForTest(Handle, 1280, 720, DurationMilliseconds);

        try
        {
            context[CpuRegister.Rdi] = Handle;
            context[CpuRegister.Rsi] = 2;
            context[CpuRegister.Rdx] = InfoAddress;
            Assert.NotEqual(0, InvokeGetStreamInfo(context, useExtendedFunction));

            context[CpuRegister.Rsi] = 0;
            context[CpuRegister.Rdx] = 0;
            Assert.NotEqual(0, InvokeGetStreamInfo(context, useExtendedFunction));

            context[CpuRegister.Rdi] = Handle + 1;
            context[CpuRegister.Rdx] = InfoAddress;
            Assert.NotEqual(0, InvokeGetStreamInfo(context, useExtendedFunction));
        }
        finally
        {
            AvPlayerExports.RemovePlayerForTest(Handle);
        }
    }

    [Fact]
    public void StreamInfoExExportIsRegisteredForGen5Only()
    {
        var gen4Manager = new ModuleManager();
        gen4Manager.RegisterExports(
            SharpEmu.Generated.SysAbiExportRegistry.CreateExports(Generation.Gen4));
        Assert.False(gen4Manager.TryGetExport(StreamInfoExNid, out _));

        var gen5Manager = new ModuleManager();
        gen5Manager.RegisterExports(
            SharpEmu.Generated.SysAbiExportRegistry.CreateExports(Generation.Gen5));
        Assert.True(gen5Manager.TryGetExport(StreamInfoExNid, out var export));
        Assert.Equal("sceAvPlayerGetStreamInfoEx", export.Name);
        Assert.Equal("libSceAvPlayer", export.LibraryName);
        Assert.Equal(Generation.Gen5, export.Target);
    }

    private static int InvokeGetStreamInfo(CpuContext context, bool useExtendedFunction) =>
        useExtendedFunction
            ? AvPlayerExports.AvPlayerGetStreamInfoEx(context)
            : AvPlayerExports.AvPlayerGetStreamInfo(context);

    [Theory]
    [InlineData(0, 1, 3840, 2160)]
    [InlineData(1, 2, 2, 48000)]
    public void WriteStreamInfo_UsesSixteenByteAbiWithoutClobberingCallerStack(
        uint streamIndex,
        uint expectedType,
        uint expectedFirstValue,
        uint expectedSecondValue)
    {
        var memory = new FakeCpuMemory(BaseAddress, 0x1000);
        var context = new CpuContext(memory, Generation.Gen5);
        const ulong sentinel = 0xD15EA5E5CAFEBABE;
        Span<byte> sentinelBytes = stackalloc byte[sizeof(ulong)];
        BinaryPrimitives.WriteUInt64LittleEndian(sentinelBytes, sentinel);
        Assert.True(memory.TryWrite(InfoAddress + 16, sentinelBytes));

        Assert.True(AvPlayerExports.TryWriteStreamInfo(
            context,
            InfoAddress,
            streamIndex,
            3840,
            2160));

        Span<byte> result = stackalloc byte[24];
        Assert.True(memory.TryRead(InfoAddress, result));
        Assert.Equal(expectedType, BinaryPrimitives.ReadUInt32LittleEndian(result));
        Assert.Equal(expectedFirstValue, BinaryPrimitives.ReadUInt32LittleEndian(result[8..]));
        Assert.Equal(expectedSecondValue, BinaryPrimitives.ReadUInt32LittleEndian(result[12..]));
        Assert.Equal(sentinel, BinaryPrimitives.ReadUInt64LittleEndian(result[16..]));
    }

    [Theory]
    [InlineData(3840, 2160, 1280, 1280, 720)]
    [InlineData(1920, 1080, 1280, 1280, 720)]
    [InlineData(640, 481, 1280, 640, 480)]
    public void ComputeHostPreviewSize_PreservesAspectAndProducesEvenHeight(
        int sourceWidth,
        int sourceHeight,
        int maximumWidth,
        int expectedWidth,
        int expectedHeight)
    {
        Assert.Equal(
            (expectedWidth, expectedHeight),
            AvPlayerExports.ComputeHostPreviewSize(sourceWidth, sourceHeight, maximumWidth));
    }
}
