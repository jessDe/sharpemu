// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.HLE;

namespace SharpEmu.Libs.Network;

public static class RudpExports
{
    private static int _initialized;
    private static ulong _eventHandler;
    private static ulong _eventHandlerArgument;

    [SysAbiExport(
        Nid = "amuBfI-AQc4",
        ExportName = "sceRudpInit",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceRudp")]
    public static int RudpInit(CpuContext ctx)
    {
        // RUDP is optional for offline play. Preserve idempotent platform
        // initialization semantics while retaining the supplied pool for
        // diagnostics; no network worker is started until a socket is used.
        Interlocked.Exchange(ref _initialized, 1);
        return ctx.SetReturn(OrbisGen2Result.ORBIS_GEN2_OK);
    }

    [SysAbiExport(
        Nid = "SUEVes8gvmw",
        ExportName = "sceRudpSetEventHandler",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceRudp")]
    public static int RudpSetEventHandler(CpuContext ctx)
    {
        // The callback is process-global and has the ABI
        // (handler, userArgument). Offline emulation never creates an RUDP
        // socket, so retaining the registration is sufficient; invoking it
        // without an actual network event would be incorrect.
        Volatile.Write(ref _eventHandler, ctx[CpuRegister.Rdi]);
        Volatile.Write(ref _eventHandlerArgument, ctx[CpuRegister.Rsi]);
        return ctx.SetReturn(OrbisGen2Result.ORBIS_GEN2_OK);
    }

    [SysAbiExport(
        Nid = "6PBNpsgyaxw",
        ExportName = "sceRudpEnableInternalIOThread",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceRudp")]
    public static int RudpEnableInternalIoThread(CpuContext ctx)
    {
        // No socket/context is created in the offline backend, therefore there
        // is no I/O work to schedule. Report successful configuration so titles
        // can finish their optional network-library initialization.
        return ctx.SetReturn(OrbisGen2Result.ORBIS_GEN2_OK);
    }
}
