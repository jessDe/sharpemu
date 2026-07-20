// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.Libs.Gpu;
using SharpEmu.Libs.VideoOut;
using Xunit;

namespace SharpEmu.Libs.Tests.VideoOut;

public sealed class VulkanGuestPayloadAccountingTests
{
    [Fact]
    public void GlobalBuffers_SharingBackingArray_AreChargedOnce()
    {
        var shared = new byte[16 * 1024 * 1024];
        GuestMemoryBuffer[] buffers =
        [
            new(0x1000, shared, shared.Length, Pooled: true),
            new(0x2000, shared, shared.Length / 2, Pooled: true),
            new(0x3000, new byte[4096], 4096, Pooled: true),
        ];

        Assert.Equal(
            (ulong)shared.LongLength + 4096UL,
            VulkanVideoPresenter.GetGlobalBufferPayloadBytes(buffers));
    }
}
