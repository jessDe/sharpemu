// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.HLE;
using System.Collections.Concurrent;

namespace SharpEmu.Libs.Np;

public static class NpWebApi2Exports
{
    private const int NpWebApi2ErrorInvalidArgument = unchecked((int)0x80553402);

    private static int _initialized;
    private static int _nextUserContextId;
    private static readonly ConcurrentDictionary<(int LibraryContextId, int UserId), int> UserContexts = new();

    [SysAbiExport(
        Nid = "+o9816YQhqQ",
        ExportName = "sceNpWebApi2Initialize",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceNpWebApi2")]
    public static int NpWebApi2Initialize(CpuContext ctx)
    {
        var httpContextId = unchecked((int)ctx[CpuRegister.Rdi]);
        var poolSize = ctx[CpuRegister.Rsi];

        if (httpContextId <= 0 || poolSize == 0)
        {
            return ctx.SetReturn(NpWebApi2ErrorInvalidArgument);
        }

        Interlocked.Exchange(ref _initialized, 1);
        TraceNpWebApi2("init", httpContextId, poolSize);
        return ctx.SetReturn(0);
    }

    [SysAbiExport(
        Nid = "WV1GwM32NgY",
        ExportName = "sceNpWebApi2PushEventCreateHandle",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceNpWebApi2")]
    public static int NpWebApi2InitializeAlt(CpuContext ctx)
    {
        Interlocked.Exchange(ref _initialized, 1);
        TraceNpWebApi2("init-alt", unchecked((int)ctx[CpuRegister.Rdi]), ctx[CpuRegister.Rsi]);
        return ctx.SetReturn(0);
    }

    [SysAbiExport(
        Nid = "sk54bi6FtYM",
        ExportName = "sceNpWebApi2CreateUserContext",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceNpWebApi2")]
    public static int NpWebApi2CreateUserContext(CpuContext ctx)
    {
        var libraryContextId = unchecked((int)ctx[CpuRegister.Rdi]);
        var userId = unchecked((int)ctx[CpuRegister.Rsi]);

        // A user context is only a local routing handle. It is safe to provide
        // one without a PSN backend; individual online requests can still report
        // their offline result. Returning INVALID_ARGUMENT here makes titles
        // retry context creation every frame and prevents offline startup.
        var userContextId = UserContexts.GetOrAdd(
            (libraryContextId, userId),
            static _ => Interlocked.Increment(ref _nextUserContextId));
        Interlocked.Exchange(ref _initialized, 1);
        TraceNpWebApi2("create-user-context", libraryContextId, unchecked((uint)userId));
        return ctx.SetReturn(userContextId);
    }

    [SysAbiExport(
        Nid = "bEvXpcEk200",
        ExportName = "sceNpWebApi2Terminate",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceNpWebApi2")]
    public static int NpWebApi2Terminate(CpuContext ctx)
    {
        var libraryContextId = unchecked((int)ctx[CpuRegister.Rdi]);
        Interlocked.Exchange(ref _initialized, 0);
        UserContexts.Clear();
        TraceNpWebApi2("term", libraryContextId, 0);
        return ctx.SetReturn(0);
    }

    private static void TraceNpWebApi2(string operation, int id, ulong arg0)
    {
        if (!string.Equals(Environment.GetEnvironmentVariable("SHARPEMU_LOG_NP_WEB_API2"), "1", StringComparison.Ordinal))
        {
            return;
        }

        Console.Error.WriteLine(
            $"[LOADER][TRACE] npwebapi2.{operation} id={id} arg0=0x{arg0:X16} initialized={Volatile.Read(ref _initialized)}");
    }
}
