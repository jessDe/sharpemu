// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.HLE;
using SharpEmu.Libs.Pad;
using Xunit;

namespace SharpEmu.Libs.Tests.Pad;

public sealed class PadExportsTests
{
    private const ulong Base = 0x1_0000_0000;
    private const int InvalidHandle = unchecked((int)0x80920003);

    private readonly FakeCpuMemory _memory = new(Base, 0x1000);
    private readonly CpuContext _ctx;

    public PadExportsTests()
    {
        _ctx = new CpuContext(_memory, Generation.Gen5);
    }

    [Theory]
    [InlineData(0, 0)]
    [InlineData(1, 0)]
    [InlineData(2, InvalidHandle)]
    [InlineData(-1, InvalidHandle)]
    public void SetTiltCorrectionState_ValidatesHandle(int handle, int expected)
    {
        _ctx[CpuRegister.Rdi] = unchecked((ulong)handle);
        Assert.Equal(expected, PadExports.PadSetTiltCorrectionState(_ctx));
    }

    [Fact]
    public void GetTriggerEffectState_WritesTwoNeutralModes()
    {
        const ulong address = Base + 0x100;
        Assert.True(_memory.TryWrite(address, new byte[] { 0xAA, 0xBB, 0xCC }));
        _ctx[CpuRegister.Rdi] = 1;
        _ctx[CpuRegister.Rsi] = address;

        Assert.Equal(0, PadExports.PadGetTriggerEffectState(_ctx));

        Span<byte> actual = stackalloc byte[3];
        Assert.True(_memory.TryRead(address, actual));
        Assert.Equal(new byte[] { 0, 0, 0xCC }, actual.ToArray());
    }

    [Fact]
    public void GetTriggerEffectState_ValidatesHandle()
    {
        _ctx[CpuRegister.Rdi] = 2;
        _ctx[CpuRegister.Rsi] = Base + 0x100;

        Assert.Equal(InvalidHandle, PadExports.PadGetTriggerEffectState(_ctx));
    }
}
