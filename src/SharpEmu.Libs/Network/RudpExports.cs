// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.HLE;

namespace SharpEmu.Libs.Network;

public static class RudpExports
{
    private static int _initialized;

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
}
