// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.Libs.VideoOut;
using Xunit;

namespace SharpEmu.Libs.Tests.VideoOut;

public sealed class VulkanGuestImageAliasTests
{
    [Fact]
    public void AcceptsScaledDynamicResolutionExtent()
    {
        Assert.True(VulkanVideoPresenter.IsCompatibleGuestImageExtent(
            textureWidth: 2432,
            textureHeight: 1368,
            textureTileMode: 10,
            imageWidth: 1920,
            imageHeight: 1080));
    }

    [Fact]
    public void RejectsLargerDescriptorWithDifferentAspectRatio()
    {
        Assert.False(VulkanVideoPresenter.IsCompatibleGuestImageExtent(
            textureWidth: 2432,
            textureHeight: 1368,
            textureTileMode: 10,
            imageWidth: 1920,
            imageHeight: 1024));
    }

    [Fact]
    public void RejectsScaledLinearDescriptor()
    {
        Assert.False(VulkanVideoPresenter.IsCompatibleGuestImageExtent(
            textureWidth: 2432,
            textureHeight: 1368,
            textureTileMode: 0,
            imageWidth: 1920,
            imageHeight: 1080));
    }

    [Fact]
    public void ClosestDynamicResolutionVariantHasSmallestDistance()
    {
        var currentDistance = VulkanVideoPresenter.GetGuestImageExtentDistance(
            2432, 1368, 1920, 1080);
        var retiredDistance = VulkanVideoPresenter.GetGuestImageExtentDistance(
            2432, 1368, 960, 540);

        Assert.True(currentDistance < retiredDistance);
    }
}
