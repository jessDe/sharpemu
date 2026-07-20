// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.HLE;
using SharpEmu.ShaderCompiler;
using Xunit;

namespace SharpEmu.Libs.Tests.Compatibility;

public sealed class WorldMapImportTests
{
    public static TheoryData<string, string> ResolvedImports => new()
    {
        { "00oCq0RwSAY", "_ZN3sce4Json11Initializer27setGlobalNullAccessCallBackEPFRKNS0_5ValueENS0_9ValueTypeEPS3_PvES7_" },
        { "amuBfI-AQc4", "sceRudpInit" },
        { "n1-v6FgU7MQ", "sceKernelConfiguredFlexibleMemorySize" },
        { "Sygnk9dr5WQ", "sceShareRegisterContentEventCallback" },
        { "tU5e3f9gSiU", "sceKernelIsTrinityMode" },
        { "WAzWTZm1H+I", "sceSaveDataTransferringMount" },
        { "ZIXln2K3XMk", "sceAcmContextCreate" },
    };

    [Theory]
    [MemberData(nameof(ResolvedImports))]
    public void CsvResolvedWorldMapImportsAreRegistered(string nid, string expectedName)
    {
        var manager = new ModuleManager();
        manager.RegisterExports(
            SharpEmu.Generated.SysAbiExportRegistry.CreateExports(Generation.Gen5));

        Assert.True(manager.TryGetExport(nid, out var export));
        Assert.Equal(expectedName, export.Name);
    }

    [Theory]
    [InlineData(0u, 0, 1, 2, 3)]
    [InlineData(1u, 2, 1, 0, 3)]
    [InlineData(2u, 3, 2, 1, 0)]
    [InlineData(3u, 3, 0, 1, 2)]
    public void ColorTargetComponentSwapMatchesCbColorInfo(
        uint componentSwap,
        int r,
        int g,
        int b,
        int a)
    {
        Assert.Equal(r, Gen5ColorComponentSwap.GetSourceIndex(componentSwap, 0));
        Assert.Equal(g, Gen5ColorComponentSwap.GetSourceIndex(componentSwap, 1));
        Assert.Equal(b, Gen5ColorComponentSwap.GetSourceIndex(componentSwap, 2));
        Assert.Equal(a, Gen5ColorComponentSwap.GetSourceIndex(componentSwap, 3));
    }
}
