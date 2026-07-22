// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.Libs.Agc;
using Xunit;

namespace SharpEmu.Libs.Tests.Agc;

public sealed class GpuWaitRegistryTests : IDisposable
{
    private readonly object _memory = new();

    public GpuWaitRegistryTests() => GpuWaitRegistry.Clear();

    public void Dispose() => GpuWaitRegistry.Clear();

    [Fact]
    public void ExpiredOrphanedComputeWait_IsReleased()
    {
        var waiter = CreateWaiter();
        GpuWaitRegistry.Register(waiter.WaitAddress, waiter);

        var released = GpuWaitRegistry.CollectExpiredOrphanedComputeWaits(
            _memory,
            nowTicks: 200,
            minAgeTicks: 100);

        Assert.Single(released!);
        Assert.Equal(0, GpuWaitRegistry.Count);
    }

    [Fact]
    public void ObservedProducer_PreservesWait()
    {
        var waiter = CreateWaiter();
        GpuWaitRegistry.Register(waiter.WaitAddress, waiter);
        GpuWaitRegistry.MarkProducerObservedInRange(_memory, waiter.WaitAddress, sizeof(ulong));

        var released = GpuWaitRegistry.CollectExpiredOrphanedComputeWaits(
            _memory,
            nowTicks: 200,
            minAgeTicks: 100);

        Assert.Null(released);
        Assert.Equal(1, GpuWaitRegistry.Count);
    }

    [Fact]
    public void StandardOrGraphicsWaits_AreNeverReleasedAsOrphans()
    {
        var standard = CreateWaiter();
        standard.IsStandard = true;
        GpuWaitRegistry.Register(standard.WaitAddress, standard);
        var graphics = CreateWaiter();
        graphics.WaitAddress += 0x10;
        graphics.QueueName = "dcb.graphics";
        GpuWaitRegistry.Register(graphics.WaitAddress, graphics);

        var released = GpuWaitRegistry.CollectExpiredOrphanedComputeWaits(
            _memory,
            nowTicks: 200,
            minAgeTicks: 100);

        Assert.Null(released);
        Assert.Equal(2, GpuWaitRegistry.Count);
    }

    [Fact]
    public void YoungOrphan_RemainsSuspendedDuringGracePeriod()
    {
        var waiter = CreateWaiter();
        GpuWaitRegistry.Register(waiter.WaitAddress, waiter);

        var released = GpuWaitRegistry.CollectExpiredOrphanedComputeWaits(
            _memory,
            nowTicks: 99,
            minAgeTicks: 100);

        Assert.Null(released);
        Assert.Equal(1, GpuWaitRegistry.Count);
    }

    [Fact]
    public void HistoricalProducer_ExtendsGraceButDoesNotBlockForever()
    {
        var waiter = CreateWaiter();
        GpuWaitRegistry.RecordProduced(_memory, waiter.WaitAddress, value: 0);
        GpuWaitRegistry.Register(waiter.WaitAddress, waiter);

        var early = GpuWaitRegistry.CollectExpiredOrphanedComputeWaits(
            _memory,
            nowTicks: 200,
            minAgeTicks: 100);
        Assert.Null(early);

        var released = GpuWaitRegistry.CollectExpiredOrphanedComputeWaits(
            _memory,
            nowTicks: 6_401,
            minAgeTicks: 100);

        Assert.Single(released!);
        Assert.True(released![0].ProducerObservedFromHistory);
    }

    [Fact]
    public void CurrentProducer_ReplacesHistoricalMarkerAndPreservesWait()
    {
        var waiter = CreateWaiter();
        GpuWaitRegistry.RecordProduced(_memory, waiter.WaitAddress, value: 0);
        GpuWaitRegistry.Register(waiter.WaitAddress, waiter);
        GpuWaitRegistry.MarkProducerObservedInRange(
            _memory,
            waiter.WaitAddress,
            sizeof(ulong));

        var released = GpuWaitRegistry.CollectExpiredOrphanedComputeWaits(
            _memory,
            nowTicks: 10_000,
            minAgeTicks: 100);

        Assert.Null(released);
        Assert.Equal(1, GpuWaitRegistry.Count);
    }

    [Fact]
    public void ExpireRetry_MakesMatchingIndirectRetryCollectible()
    {
        var waiter = CreateWaiter();
        waiter.ResumeAddress = 0x5000;
        waiter.RetryDeadlineTicks = 10_000;
        GpuWaitRegistry.Register(waiter.WaitAddress, waiter);

        Assert.True(GpuWaitRegistry.ExpireRetry(_memory, waiter.ResumeAddress, nowTicks: 200));
        var expired = GpuWaitRegistry.CollectExpiredRetries(_memory, nowTicks: 200);

        Assert.Single(expired!);
        Assert.Equal(0, GpuWaitRegistry.Count);
    }

    private GpuWaitRegistry.WaitingDcb CreateWaiter() => new()
    {
        WaitAddress = 0x40002000,
        ReferenceValue = 1,
        Mask = uint.MaxValue,
        CompareFunction = 3,
        Is64Bit = true,
        IsStandard = false,
        Memory = _memory,
        QueueName = "acb.compute[64]",
        RegisteredTicks = 0,
    };
}
