// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.HLE;

namespace SharpEmu.Libs.Psml;

// PS5 Machine Learning (PSML) provides the MFSR implementation used by PSSR.
// SharpEmu does not currently execute PSML's generated GPU dispatch packets, but
// games still require the process-wide library initializer to succeed before they
// can select and configure their render path. The initializer has no guest-visible
// output or caller-owned state, so accepting it is sufficient and avoids reporting
// the misleading ORBIS_GEN2_ERROR_NOT_FOUND returned by an unresolved import.
public static class PsmlExports
{
    private const ulong SharedResourceBlockSize = 0x1_0000;
    private const ulong SharedResourceBlockAlignment = 0x1_0000;
    private const ulong ContextBlockSize = 0x1_0000;
    private const ulong ContextBlockAlignment = 0x1_0000;

    [SysAbiExport(
        Nid = "3WVD91e12ZQ",
        ExportName = "scePsmlMfsrInit",
        Target = Generation.Gen5,
        LibraryName = "libScePsml")]
    public static int MfsrInit(CpuContext ctx) =>
        ctx.SetReturn((int)OrbisGen2Result.ORBIS_GEN2_OK);

    // The output is a three-qword allocation requirement consumed by the game as
    // {block size, block alignment, block count}. A single 64 KiB-aligned block is
    // enough for the compatibility path; no host-side PSSR resources are created.
    [SysAbiExport(
        Nid = "+2KpvixvL6E",
        ExportName = "scePsmlMfsrGetSharedResourcesInitRequirement",
        Target = Generation.Gen5,
        LibraryName = "libScePsml")]
    public static int MfsrGetSharedResourcesInitRequirement(CpuContext ctx)
    {
        var requirementAddress = ctx[CpuRegister.Rdi];
        if (requirementAddress == 0 ||
            !ctx.TryWriteUInt64(requirementAddress, SharedResourceBlockSize) ||
            !ctx.TryWriteUInt64(requirementAddress + sizeof(ulong), SharedResourceBlockAlignment) ||
            !ctx.TryWriteUInt64(requirementAddress + (2 * sizeof(ulong)), 1))
        {
            return ctx.SetReturn((int)OrbisGen2Result.ORBIS_GEN2_ERROR_INVALID_ARGUMENT);
        }

        return ctx.SetReturn((int)OrbisGen2Result.ORBIS_GEN2_OK);
    }

    // The guest owns and maps the allocation described above before this call.
    // There is no host-side PSML state to construct while dispatch packets are
    // unsupported, but accepting the mapped allocation preserves the API lifecycle.
    [SysAbiExport(
        Nid = "eWoKNeB6V-k",
        ExportName = "scePsmlMfsrCreateSharedResources",
        Target = Generation.Gen5,
        LibraryName = "libScePsml")]
    public static int MfsrCreateSharedResources(CpuContext ctx) =>
        ctx.SetReturn((int)OrbisGen2Result.ORBIS_GEN2_OK);

    [SysAbiExport(
        Nid = "ArakEpzsZo0",
        ExportName = "scePsmlMfsrGetContextBufferRequirement800M3_2",
        Target = Generation.Gen5,
        LibraryName = "libScePsml")]
    public static int MfsrGetContextBufferRequirement800M3_2(CpuContext ctx)
    {
        var requirementAddress = ctx[CpuRegister.Rdi];
        if (requirementAddress == 0 ||
            !ctx.TryWriteUInt64(requirementAddress, ContextBlockSize) ||
            !ctx.TryWriteUInt64(requirementAddress + sizeof(ulong), ContextBlockAlignment) ||
            !ctx.TryWriteUInt64(requirementAddress + (2 * sizeof(ulong)), 1))
        {
            return ctx.SetReturn((int)OrbisGen2Result.ORBIS_GEN2_ERROR_INVALID_ARGUMENT);
        }

        return ctx.SetReturn((int)OrbisGen2Result.ORBIS_GEN2_OK);
    }

    [SysAbiExport(
        Nid = "gxv3i+MTEzU",
        ExportName = "scePsmlMfsrCreateContext800M3_2",
        Target = Generation.Gen5,
        LibraryName = "libScePsml")]
    public static int MfsrCreateContext800M3_2(CpuContext ctx) =>
        ctx.SetReturn((int)OrbisGen2Result.ORBIS_GEN2_OK);

    [SysAbiExport(
        Nid = "AHalTX9wFZY",
        ExportName = "scePsmlMfsrGetDispatchMfsrPacketSizeInDwords",
        Target = Generation.Gen5,
        LibraryName = "libScePsml")]
    public static int MfsrGetDispatchMfsrPacketSizeInDwords(CpuContext ctx)
    {
        var sizeAddress = ctx[CpuRegister.Rsi];
        if (sizeAddress == 0 || !ctx.TryWriteUInt32(sizeAddress, 0))
        {
            return ctx.SetReturn((int)OrbisGen2Result.ORBIS_GEN2_ERROR_INVALID_ARGUMENT);
        }

        // Native PSML emits GPU dispatch words here. SharpEmu's compatibility
        // path does not execute those packets, so reserve and emit zero words.
        return ctx.SetReturn((int)OrbisGen2Result.ORBIS_GEN2_OK);
    }

    [SysAbiExport(
        Nid = "RUNLFro+qok",
        ExportName = "scePsmlMfsrGetDispatchMfsrPacket900",
        Target = Generation.Gen5,
        LibraryName = "libScePsml")]
    public static int MfsrGetDispatchMfsrPacket900(CpuContext ctx) =>
        ctx.SetReturn((int)OrbisGen2Result.ORBIS_GEN2_OK);

    [SysAbiExport(
        Nid = "s2psNHUIdjk",
        ExportName = "scePsmlMfsrGetDispatchMfsrPacket1000",
        Target = Generation.Gen5,
        LibraryName = "libScePsml")]
    public static int MfsrGetDispatchMfsrPacket1000(CpuContext ctx) =>
        ctx.SetReturn((int)OrbisGen2Result.ORBIS_GEN2_OK);

    [SysAbiExport(
        Nid = "94iBp3KvIuI",
        ExportName = "scePsmlMfsrGetDispatchMfsrPacket1100",
        Target = Generation.Gen5,
        LibraryName = "libScePsml")]
    public static int MfsrGetDispatchMfsrPacket1100(CpuContext ctx) =>
        ctx.SetReturn((int)OrbisGen2Result.ORBIS_GEN2_OK);
}
