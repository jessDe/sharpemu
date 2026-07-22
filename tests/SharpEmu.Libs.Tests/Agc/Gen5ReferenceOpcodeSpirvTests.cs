// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Buffers.Binary;
using SharpEmu.HLE;
using SharpEmu.ShaderCompiler;
using SharpEmu.ShaderCompiler.Vulkan;
using Xunit;

namespace SharpEmu.Libs.Tests.Agc;

public sealed class Gen5ReferenceOpcodeSpirvTests
{
    private const ulong ShaderAddress = 0x1_0000_0000;

    [Fact]
    public void FfbhU32_LowersToFindUnsignedMostSignificantBit()
    {
        const uint instruction = (0x3Fu << 25) | (0x39u << 9);

        var spirv = Compile(instruction);

        Assert.True(ContainsExtInst(spirv, 75));
    }

    [Fact]
    public void Signed16Compare_LowersWithSignExtension()
    {
        var spirv = Compile(Vopc(0x89));

        Assert.True(ContainsOpcode(spirv, SpirvOp.BitFieldSExtract));
        Assert.True(ContainsOpcode(spirv, SpirvOp.SLessThan));
    }

    [Theory]
    [InlineData(0xC9u, SpirvOp.FOrdLessThan)]
    [InlineData(0xE5u, SpirvOp.INotEqual)]
    public void HalfAnd64BitCompares_LowerToExpectedOperation(
        uint opcode,
        SpirvOp expectedOperation)
    {
        var spirv = Compile(Vopc(opcode));

        Assert.True(ContainsOpcode(spirv, expectedOperation));
    }

    private static uint Vopc(uint opcode) =>
        (0x3Eu << 25) | (opcode << 17);

    private static byte[] Compile(params uint[] programWords)
    {
        var memory = new FakeCpuMemory(ShaderAddress, 0x2000);
        var ctx = new CpuContext(memory, Generation.Gen5);
        Gen5ShaderAtomicDecodeTests.WriteProgram(memory, ShaderAddress, programWords);
        var shaderRegisters = new Dictionary<uint, uint>
        {
            [Gen5ShaderAtomicDecodeTests.ComputePgmRsrc2Register] = 16u << 1,
        };

        Assert.True(
            Gen5ShaderTranslator.TryCreateState(
                ctx,
                ShaderAddress,
                0,
                shaderRegisters,
                Gen5ShaderAtomicDecodeTests.ComputeUserDataRegister,
                out var state,
                out var error),
            error);
        Assert.True(
            Gen5ShaderScalarEvaluator.TryEvaluate(ctx, state, out var evaluation, out error),
            error);
        Assert.True(
            Gen5SpirvTranslator.TryCompileComputeShader(
                state,
                evaluation,
                1,
                1,
                1,
                out var shader,
                out error),
            error);
        return shader.Spirv;
    }

    private static bool ContainsOpcode(byte[] spirv, SpirvOp expected)
    {
        foreach (var (opcode, _, _) in EnumerateInstructions(spirv))
        {
            if (opcode == (ushort)expected)
            {
                return true;
            }
        }

        return false;
    }

    private static bool ContainsExtInst(byte[] spirv, uint instruction)
    {
        foreach (var (opcode, wordCount, offset) in EnumerateInstructions(spirv))
        {
            if (opcode == (ushort)SpirvOp.ExtInst &&
                wordCount >= 5 &&
                ReadWord(spirv, offset + 16) == instruction)
            {
                return true;
            }
        }

        return false;
    }

    private static IEnumerable<(ushort Opcode, int WordCount, int Offset)>
        EnumerateInstructions(byte[] spirv)
    {
        for (var offset = 5 * sizeof(uint); offset + sizeof(uint) <= spirv.Length;)
        {
            var word = ReadWord(spirv, offset);
            var wordCount = (int)(word >> 16);
            if (wordCount <= 0)
            {
                yield break;
            }

            yield return ((ushort)word, wordCount, offset);
            offset += wordCount * sizeof(uint);
        }
    }

    private static uint ReadWord(byte[] spirv, int offset) =>
        BinaryPrimitives.ReadUInt32LittleEndian(
            spirv.AsSpan(offset, sizeof(uint)));
}
