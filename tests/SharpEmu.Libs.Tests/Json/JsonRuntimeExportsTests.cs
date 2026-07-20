// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Text;
using SharpEmu.HLE;
using SharpEmu.Libs.Json;
using Xunit;

namespace SharpEmu.Libs.Tests.Json;

[Collection("JsonObjectHeap")]
public sealed class JsonRuntimeExportsTests
{
    private const ulong BaseAddress = 0x1_0000_0000;
    private const ulong ValueAddress = BaseAddress + 0x100;
    private const ulong BufferAddress = BaseAddress + 0x200;
    private const ulong StringAddress = BaseAddress + 0x300;

    [Fact]
    public void ParserParseBuffer_AcceptsSizeIncludingTrailingNull()
    {
        var memory = new AllocatingCpuMemory(BaseAddress, 0x10000, BaseAddress + 0x8000);
        var context = new CpuContext(memory, Generation.Gen5);
        var json = Encoding.UTF8.GetBytes("{\"enabled\":true}\0");
        Assert.True(memory.TryWrite(BufferAddress, json));
        context[CpuRegister.Rdi] = ValueAddress;
        context[CpuRegister.Rsi] = BufferAddress;
        context[CpuRegister.Rdx] = (ulong)json.Length;

        var result = JsonExports.ParserParseBuffer(context);

        Assert.Equal(0, result);
        Assert.Equal(0UL, context[CpuRegister.Rax]);
        context[CpuRegister.Rdi] = ValueAddress;
        JsonExports.ValueGetType(context);
        Assert.Equal(7UL, context[CpuRegister.Rax]);
    }

    [Fact]
    public void ValueReferString_ReturnsReferencedBooleanValue()
    {
        JsonObjectHeap.ResetForTests();
        var memory = new AllocatingCpuMemory(BaseAddress, 0x10000, BaseAddress + 0x8000);
        var context = new CpuContext(memory, Generation.Gen5);
        var json = Encoding.UTF8.GetBytes("{\"enabled\":true}");
        Assert.True(memory.TryWrite(BufferAddress, json));
        context[CpuRegister.Rdi] = ValueAddress;
        context[CpuRegister.Rsi] = BufferAddress;
        context[CpuRegister.Rdx] = (ulong)json.Length;
        Assert.Equal(0, JsonExports.ParserParseBuffer(context));

        JsonObjectHeap.SetString(StringAddress, "enabled");
        context[CpuRegister.Rdi] = ValueAddress;
        context[CpuRegister.Rsi] = StringAddress;
        Assert.Equal(0, JsonExports.ValueReferString(context));
        var referencedValue = context[CpuRegister.Rax];
        Assert.NotEqual(0UL, referencedValue);

        context[CpuRegister.Rdi] = referencedValue;
        JsonExports.ValueGetType(context);
        Assert.Equal(1UL, context[CpuRegister.Rax]);
    }

    [Fact]
    public void ValueReferString_MissingMemberReturnsNullPointer()
    {
        JsonObjectHeap.ResetForTests();
        var memory = new AllocatingCpuMemory(BaseAddress, 0x10000, BaseAddress + 0x8000);
        var context = new CpuContext(memory, Generation.Gen5);
        var json = Encoding.UTF8.GetBytes("{\"name\":\"default\"}");
        Assert.True(memory.TryWrite(BufferAddress, json));
        context[CpuRegister.Rdi] = ValueAddress;
        context[CpuRegister.Rsi] = BufferAddress;
        context[CpuRegister.Rdx] = (ulong)json.Length;
        Assert.Equal(0, JsonExports.ParserParseBuffer(context));

        JsonObjectHeap.SetString(StringAddress, "applyToAll");
        context[CpuRegister.Rdi] = ValueAddress;
        context[CpuRegister.Rsi] = StringAddress;

        Assert.Equal(0, JsonExports.ValueReferString(context));
        Assert.Equal(0UL, context[CpuRegister.Rax]);
    }

    private sealed class AllocatingCpuMemory : ICpuMemory, IGuestMemoryAllocator
    {
        private readonly ulong _baseAddress;
        private readonly byte[] _storage;
        private ulong _nextAllocation;

        public AllocatingCpuMemory(ulong baseAddress, int size, ulong firstAllocation)
        {
            _baseAddress = baseAddress;
            _storage = new byte[size];
            _nextAllocation = firstAllocation;
        }

        public bool TryRead(ulong virtualAddress, Span<byte> destination)
        {
            if (!TryResolve(virtualAddress, destination.Length, out var offset))
            {
                return false;
            }

            _storage.AsSpan(offset, destination.Length).CopyTo(destination);
            return true;
        }

        public bool TryWrite(ulong virtualAddress, ReadOnlySpan<byte> source)
        {
            if (!TryResolve(virtualAddress, source.Length, out var offset))
            {
                return false;
            }

            source.CopyTo(_storage.AsSpan(offset, source.Length));
            return true;
        }

        public bool TryAllocateGuestMemory(ulong size, ulong alignment, out ulong address)
        {
            var mask = alignment - 1;
            address = (_nextAllocation + mask) & ~mask;
            if (!TryResolve(address, checked((int)size), out _))
            {
                address = 0;
                return false;
            }

            _nextAllocation = address + size;
            return true;
        }

        public bool TryFreeGuestMemory(ulong address) => true;

        private bool TryResolve(ulong virtualAddress, int length, out int offset)
        {
            offset = 0;
            if (virtualAddress < _baseAddress)
            {
                return false;
            }

            var relative = virtualAddress - _baseAddress;
            if (relative + (ulong)length > (ulong)_storage.Length)
            {
                return false;
            }

            offset = (int)relative;
            return true;
        }
    }
}
