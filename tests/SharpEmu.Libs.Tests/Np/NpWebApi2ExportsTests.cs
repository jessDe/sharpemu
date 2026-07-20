// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.HLE;
using SharpEmu.Libs.Np;
using Xunit;

namespace SharpEmu.Libs.Tests.Np;

public sealed class NpWebApi2ExportsTests
{
    private readonly CpuContext _ctx = new(new FakeCpuMemory(0x1_0000_0000, 0x1000), Generation.Gen5);

    [Fact]
    public void CreateUserContext_ReturnsStablePositiveHandle()
    {
        _ctx[CpuRegister.Rdi] = 0;
        _ctx[CpuRegister.Rsi] = 0x10000000;

        var first = NpWebApi2Exports.NpWebApi2CreateUserContext(_ctx);
        var second = NpWebApi2Exports.NpWebApi2CreateUserContext(_ctx);

        Assert.True(first > 0);
        Assert.Equal(first, second);
    }

    [Fact]
    public void CreateUserContext_SeparatesUsers()
    {
        _ctx[CpuRegister.Rdi] = 7;
        _ctx[CpuRegister.Rsi] = 0x10000000;
        var first = NpWebApi2Exports.NpWebApi2CreateUserContext(_ctx);
        _ctx[CpuRegister.Rsi] = 0x10000001;

        var second = NpWebApi2Exports.NpWebApi2CreateUserContext(_ctx);

        Assert.NotEqual(first, second);
    }
}
