// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.Core.Cpu.Native;
using Xunit;

namespace SharpEmu.Libs.Tests;

public sealed class DirectExecutionBackendTlsTests
{
    [Fact]
    public void TlsScanStartsAtImageBaseWhenEntryPointIsLaterInExecutable()
    {
        const ulong imageBase = 0x0000_0008_0000_0000;
        const ulong entryPoint = imageBase + 0x0293_3CC0;
        const ulong sarosTlsLoad = imageBase + 0x004E_534B;
        const ulong maxScanBytes = 128UL * 1024 * 1024;

        var scanStart = DirectExecutionBackend.ResolveTlsScanStart(entryPoint, imageBase, maxScanBytes);

        Assert.Equal(imageBase, scanStart);
        Assert.InRange(sarosTlsLoad, scanStart, scanStart + maxScanBytes - 1);
    }

    [Fact]
    public void TlsScanKeepsEntryPointWhenAllocationBaseIsOutsideScanWindow()
    {
        const ulong allocationBase = 0x0000_0008_0000_0000;
        const ulong entryPoint = allocationBase + 0x0900_0000;
        const ulong maxScanBytes = 128UL * 1024 * 1024;

        Assert.Equal(
            entryPoint,
            DirectExecutionBackend.ResolveTlsScanStart(entryPoint, allocationBase, maxScanBytes));
    }
}
