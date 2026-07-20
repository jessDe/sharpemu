// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.HLE;
using SharpEmu.Libs.Kernel;
using SharpEmu.Libs.VideoOut;
using System.Buffers.Binary;
using System.Diagnostics;
using System.Globalization;
using System.Text;

namespace SharpEmu.Libs.AvPlayer;

public static class AvPlayerExports
{
    private const int InvalidParameters = unchecked((int)0x806A0001);
    private const int OperationFailed = unchecked((int)0x806A0002);
    private const int FrameBufferCount = 3;
    private const int FrameInfoSize = 40;
    private const int FrameInfoExSize = 104;
    // SceAvPlayerStreamInfo is a 16-byte discriminated union. The first
    // dword is SceAvPlayerStreamType (1=video, 2=audio), followed by the
    // four-byte language code and an eight-byte type-specific payload.
    private const int StreamInfoSize = 16;
    // The extended Gen5 query includes timing metadata through byte 31.
    private const int StreamInfoExSize = 32;
    private const int MaxGuestPathLength = 4096;
    private static readonly object StateGate = new();
    private static readonly Dictionary<ulong, PlayerState> Players = new();
    private static int _traceCount;

    private sealed class PlayerState : IDisposable
    {
        public required ulong Handle { get; init; }
        public bool AutoStart { get; init; }
        public ulong AllocatorObject { get; init; }
        public ulong AllocateCallback { get; init; }
        public ulong AllocateTextureCallback { get; init; }
        public ulong EventObject { get; init; }
        public ulong EventCallback { get; init; }
        public string? SourcePath { get; set; }
        public int Width { get; set; }
        public int Height { get; set; }
        public double FramesPerSecond { get; set; } = 30.0;
        public ulong DurationMilliseconds { get; set; }
        public bool Started { get; set; }
        public bool Paused { get; set; }
        public bool Looping { get; set; }
        public bool EndOfStream { get; set; }
        public bool PendingStopEvent { get; set; }
        public Process? Decoder { get; set; }
        public Stream? DecoderOutput { get; set; }
        public Process? AudioDecoder { get; set; }
        public Stream? AudioDecoderOutput { get; set; }
        public Stopwatch PlaybackClock { get; } = new();
        public byte[]? RawFrame { get; set; }
        public byte[]? RawAudioFrame { get; set; }
        public byte[]? PaddedFrame { get; set; }
        public ulong[] GuestBuffers { get; } = new ulong[FrameBufferCount];
        public bool TextureAllocatorFailed { get; set; }
        public bool GeneralAllocatorFailed { get; set; }
        public int GuestBufferStride { get; set; }
        public int NextGuestBuffer { get; set; }
        public ulong LastGuestBuffer { get; set; }
        public long NextFrameIndex { get; set; }
        public ulong AudioBufferBase { get; set; }
        public int NextAudioBuffer { get; set; }
        public long NextAudioFrameIndex { get; set; }
        public CancellationTokenSource? HostVideoCancellation { get; set; }
        public Task? HostVideoTask { get; set; }
        public Process? HostVideoProcess { get; set; }

        public void Dispose()
        {
            HostVideoCancellation?.Cancel();
            HostVideoCancellation?.Dispose();
            HostVideoCancellation = null;
            HostVideoTask = null;
            if (HostVideoProcess is not null)
            {
                TryTerminateProcess(HostVideoProcess);
                HostVideoProcess = null;
            }
            DecoderOutput?.Dispose();
            DecoderOutput = null;
            AudioDecoderOutput?.Dispose();
            AudioDecoderOutput = null;
            if (Decoder is not null)
            {
                try
                {
                    if (!Decoder.HasExited)
                    {
                        Decoder.Kill(entireProcessTree: true);
                    }
                }
                catch (InvalidOperationException)
                {
                }
                finally
                {
                    Decoder.Dispose();
                    Decoder = null;
                }
            }
            if (AudioDecoder is not null)
            {
                try
                {
                    if (!AudioDecoder.HasExited)
                    {
                        AudioDecoder.Kill(entireProcessTree: true);
                    }
                }
                catch (InvalidOperationException)
                {
                }
                finally
                {
                    AudioDecoder.Dispose();
                    AudioDecoder = null;
                }
            }
        }

        public void ResetPlayback()
        {
            Dispose();
            PlaybackClock.Reset();
            NextFrameIndex = 0;
            NextAudioFrameIndex = 0;
            EndOfStream = false;
            PendingStopEvent = false;
        }

        private static void TryTerminateProcess(Process process)
        {
            try
            {
                if (!process.HasExited)
                {
                    process.Kill(entireProcessTree: true);
                }
            }
            catch (InvalidOperationException)
            {
            }
            finally
            {
                process.Dispose();
            }
        }
    }

    [SysAbiExport(
        Nid = "aS66RI0gGgo",
        ExportName = "sceAvPlayerInit",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceAvPlayer")]
    public static int AvPlayerInit(CpuContext ctx)
    {
        var initDataAddress = ctx[CpuRegister.Rdi];
        if (initDataAddress == 0 ||
            !KernelMemoryCompatExports.TryAllocateHleData(ctx, 0x40, 16, out var handle))
        {
            ctx[CpuRegister.Rax] = 0;
            return 0;
        }

        lock (StateGate)
        {
            Players.Add(handle, new PlayerState
            {
                Handle = handle,
                AutoStart = TryReadByte(ctx, initDataAddress + 108, out var autoStart) && autoStart != 0,
                AllocatorObject = TryReadUInt64(ctx, initDataAddress, out var allocatorObject) ? allocatorObject : 0,
                AllocateCallback = TryReadUInt64(ctx, initDataAddress + 8, out var allocate) ? allocate : 0,
                AllocateTextureCallback = TryReadUInt64(ctx, initDataAddress + 24, out var allocateTexture) ? allocateTexture : 0,
                EventObject = TryReadUInt64(ctx, initDataAddress + 80, out var eventObject) ? eventObject : 0,
                EventCallback = TryReadUInt64(ctx, initDataAddress + 88, out var eventCallback) ? eventCallback : 0,
            });
        }

        Trace($"init handle=0x{handle:X16} alloc_texture=0x{Players[handle].AllocateTextureCallback:X16}");
        ctx[CpuRegister.Rax] = handle;
        return unchecked((int)handle);
    }

    [SysAbiExport(
        Nid = "HD1YKVU26-M",
        ExportName = "sceAvPlayerPostInit",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceAvPlayer")]
    public static int AvPlayerPostInit(CpuContext ctx)
    {
        var handle = ctx[CpuRegister.Rdi];
        var dataAddress = ctx[CpuRegister.Rsi];
        lock (StateGate)
        {
            return SetReturn(
                ctx,
                handle != 0 && dataAddress != 0 && Players.ContainsKey(handle)
                    ? 0
                    : InvalidParameters);
        }
    }

    [SysAbiExport(
        Nid = "o9eWRkSL+M4",
        ExportName = "sceAvPlayerInitEx",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceAvPlayer")]
    public static int AvPlayerInitEx(CpuContext ctx)
    {
        var initDataAddress = ctx[CpuRegister.Rdi];
        var playerOutAddress = ctx[CpuRegister.Rsi];
        if (initDataAddress == 0 ||
            playerOutAddress == 0 ||
            !KernelMemoryCompatExports.TryAllocateHleData(ctx, 0x40, 16, out var handle) ||
            !ctx.TryWriteUInt64(playerOutAddress, handle))
        {
            return SetReturn(ctx, InvalidParameters);
        }

        lock (StateGate)
        {
            Players.Add(handle, new PlayerState
            {
                Handle = handle,
                AutoStart = TryReadByte(ctx, initDataAddress + 164, out var autoStart) && autoStart != 0,
                AllocatorObject = TryReadUInt64(ctx, initDataAddress + 8, out var allocatorObject) ? allocatorObject : 0,
                AllocateCallback = TryReadUInt64(ctx, initDataAddress + 16, out var allocate) ? allocate : 0,
                AllocateTextureCallback = TryReadUInt64(ctx, initDataAddress + 32, out var allocateTexture) ? allocateTexture : 0,
                EventObject = TryReadUInt64(ctx, initDataAddress + 88, out var eventObject) ? eventObject : 0,
                EventCallback = TryReadUInt64(ctx, initDataAddress + 96, out var eventCallback) ? eventCallback : 0,
            });
        }

        Trace($"init_ex handle=0x{handle:X16} alloc_texture=0x{Players[handle].AllocateTextureCallback:X16}");
        return SetReturn(ctx, 0);
    }

    [SysAbiExport(
        Nid = "eBTreZ84JFY",
        ExportName = "sceAvPlayerSetLogCallback",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceAvPlayer")]
    public static int AvPlayerSetLogCallback(CpuContext ctx) => SetReturn(ctx, 0);

    [SysAbiExport(
        Nid = "NkJwDzKmIlw",
        ExportName = "sceAvPlayerClose",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceAvPlayer")]
    public static int AvPlayerClose(CpuContext ctx)
    {
        PlayerState? player;
        lock (StateGate)
        {
            if (!Players.Remove(ctx[CpuRegister.Rdi], out player))
            {
                return SetReturn(ctx, InvalidParameters);
            }
        }

        player.Dispose();
        return SetReturn(ctx, 0);
    }

    [SysAbiExport(
        Nid = "KMcEa+rHsIo",
        ExportName = "sceAvPlayerAddSource",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceAvPlayer")]
    public static int AvPlayerAddSource(CpuContext ctx)
    {
        if (!TryReadNullTerminatedUtf8(ctx, ctx[CpuRegister.Rsi], MaxGuestPathLength, out var path))
        {
            return SetReturn(ctx, InvalidParameters);
        }

        return AddSource(ctx, path);
    }

    [SysAbiExport(
        Nid = "x8uvuFOPZhU",
        ExportName = "sceAvPlayerAddSourceEx",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceAvPlayer")]
    public static int AvPlayerAddSourceEx(CpuContext ctx)
    {
        var uriType = unchecked((uint)ctx[CpuRegister.Rsi]);
        var detailsAddress = ctx[CpuRegister.Rdx];
        if (uriType != 0 || detailsAddress == 0 ||
            !ctx.TryReadUInt64(detailsAddress, out var pathAddress) ||
            !TryReadUInt32(ctx, detailsAddress + sizeof(ulong), out var pathLength) ||
            pathLength == 0 || pathLength > MaxGuestPathLength ||
            !TryReadUtf8(ctx, pathAddress, checked((int)pathLength), out var path))
        {
            return SetReturn(ctx, InvalidParameters);
        }

        return AddSource(ctx, path.TrimEnd('\0'));
    }

    [SysAbiExport(
        Nid = "ET4Gr-Uu07s",
        ExportName = "sceAvPlayerStart",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceAvPlayer")]
    public static int AvPlayerStart(CpuContext ctx)
    {
        PlayerState player;
        lock (StateGate)
        {
            if (!Players.TryGetValue(ctx[CpuRegister.Rdi], out var foundPlayer) || foundPlayer.SourcePath is null)
            {
                return SetReturn(ctx, InvalidParameters);
            }
            player = foundPlayer;

            player.Started = true;
            player.Paused = false;
            player.EndOfStream = false;
            player.PendingStopEvent = false;
            Trace($"start handle=0x{player.Handle:X16}");
        }

        // Event callbacks are guest code and can immediately query the player.
        // Never hold StateGate while waiting for one or the callback deadlocks
        // when it re-enters an AvPlayer export on another guest worker.
        NotifyEvent(ctx, player, 3); // StatePlay
        return SetReturn(ctx, 0);
    }

    [SysAbiExport(
        Nid = "ZC17w3vB5Lo",
        ExportName = "sceAvPlayerStop",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceAvPlayer")]
    public static int AvPlayerStop(CpuContext ctx)
    {
        PlayerState player;
        lock (StateGate)
        {
            if (!Players.TryGetValue(ctx[CpuRegister.Rdi], out var foundPlayer))
            {
                return SetReturn(ctx, InvalidParameters);
            }
            player = foundPlayer;

            player.ResetPlayback();
            player.Started = false;
        }

        NotifyEvent(ctx, player, 1); // StateStop
        return SetReturn(ctx, 0);
    }

    [SysAbiExport(
        Nid = "9y5v+fGN4Wk",
        ExportName = "sceAvPlayerPause",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceAvPlayer")]
    public static int AvPlayerPause(CpuContext ctx)
    {
        PlayerState player;
        var deliverDeferredStop = false;
        var deliverHostFallbackTeardownStop = false;
        Task? hostFallbackTask = null;
        lock (StateGate)
        {
            if (!Players.TryGetValue(ctx[CpuRegister.Rdi], out var foundPlayer))
            {
                return SetReturn(ctx, InvalidParameters);
            }
            player = foundPlayer;
            Trace(
                $"pause handle=0x{player.Handle:X16} elapsed_ms={player.PlaybackClock.ElapsedMilliseconds} " +
                $"duration_ms={player.DurationMilliseconds} host_fallback={player.HostVideoTask is not null} " +
                $"pending_stop={player.PendingStopEvent}");

            // Host-video fallback reaches EOF on a worker thread and cannot
            // invoke guest callbacks there. Some titles pause the player as
            // their next AvPlayer call instead of polling IsActive/GetVideoData.
            // Preserve the EOF transition in that case: replacing it with a
            // StatePause event leaves the intro controller waiting forever.
            if (player.PendingStopEvent)
            {
                player.PendingStopEvent = false;
                deliverDeferredStop = true;
            }
            else if (player.HostVideoTask is not null)
            {
                // The compatibility presenter owns the visible boot movie and
                // completes on a host worker. Keep this guest call alive until
                // that worker can queue StateStop; otherwise the title issues
                // StatePause halfway through the movie and never enters another
                // AvPlayer export from which the queued stop can be delivered.
                hostFallbackTask = player.HostVideoTask;
            }
            else
            {
                player.Paused = true;
                player.PlaybackClock.Stop();
            }
        }

        if (hostFallbackTask is not null)
        {
            var elapsedMilliseconds = (ulong)Math.Max(0, player.PlaybackClock.ElapsedMilliseconds);
            var remainingMilliseconds = player.DurationMilliseconds > elapsedMilliseconds
                ? player.DurationMilliseconds - elapsedMilliseconds
                : 0;
            var timeoutMilliseconds = player.DurationMilliseconds == 0
                ? 10_000
                : checked((int)Math.Clamp(remainingMilliseconds + 2_000, 1_000UL, 15_000UL));
            try
            {
                _ = hostFallbackTask.Wait(timeoutMilliseconds);
            }
            catch (AggregateException exception)
            {
                Console.Error.WriteLine(
                    $"[AVPLAYER][WARN] Host fallback completion wait failed: " +
                    exception.GetBaseException().Message);
            }

            lock (StateGate)
            {
                if (Players.TryGetValue(player.Handle, out var current) &&
                    ReferenceEquals(current, player))
                {
                    // The host presenter can itself be waiting for the guest
                    // pause callback to return. Once the source's remaining
                    // presentation interval has elapsed, commit EOF directly
                    // to break that cycle and make the stop notification
                    // deterministic.
                    player.PendingStopEvent = false;
                    player.EndOfStream = true;
                    player.Started = false;
                    player.Paused = false;
                    player.PlaybackClock.Stop();
                }
            }

            // AvPlayerClose may remove the handle while the host movie is
            // finishing. The event target was captured before that race and
            // is still the terminal notification owed to the title; table
            // removal must not downgrade it back to StatePause.
            deliverDeferredStop = true;
            deliverHostFallbackTeardownStop = true;
        }

        NotifyEvent(ctx, player, deliverDeferredStop ? 1UL : 4UL); // StateStop / StatePause
        if (deliverHostFallbackTeardownStop)
        {
            // The compatibility path substitutes both guest video allocation
            // and presentation. Titles using it observe one stop for playback
            // completion and a second while tearing down that substituted
            // player; both are present on the native/successful transition.
            NotifyEvent(ctx, player, 1); // StateStop (fallback teardown)
        }
        return SetReturn(ctx, 0);
    }

    [SysAbiExport(
        Nid = "w5moABNwnRY",
        ExportName = "sceAvPlayerResume",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceAvPlayer")]
    public static int AvPlayerResume(CpuContext ctx)
    {
        lock (StateGate)
        {
            if (!Players.TryGetValue(ctx[CpuRegister.Rdi], out var player))
            {
                return SetReturn(ctx, InvalidParameters);
            }

            player.Paused = false;
            if (player.Decoder is not null)
            {
                player.PlaybackClock.Start();
            }
            return SetReturn(ctx, 0);
        }
    }

    [SysAbiExport(
        Nid = "OVths0xGfho",
        ExportName = "sceAvPlayerSetLooping",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceAvPlayer")]
    public static int AvPlayerSetLooping(CpuContext ctx)
    {
        lock (StateGate)
        {
            if (!Players.TryGetValue(ctx[CpuRegister.Rdi], out var player))
            {
                return SetReturn(ctx, InvalidParameters);
            }

            player.Looping = ctx[CpuRegister.Rsi] != 0;
            return SetReturn(ctx, 0);
        }
    }

    [SysAbiExport(
        Nid = "ODJK2sn9w4A",
        ExportName = "sceAvPlayerEnableStream",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceAvPlayer")]
    public static int AvPlayerEnableStream(CpuContext ctx) => ValidatePlayer(ctx);

    [SysAbiExport(
        Nid = "k-q+xOxdc3E",
        ExportName = "sceAvPlayerSetAvSyncMode",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceAvPlayer")]
    public static int AvPlayerSetAvSyncMode(CpuContext ctx)
    {
        Trace($"set_av_sync_mode handle=0x{ctx[CpuRegister.Rdi]:X16} mode={ctx[CpuRegister.Rsi]}");
        return ValidatePlayer(ctx);
    }

    [SysAbiExport(
        Nid = "ctTAcF5DiKQ",
        ExportName = "sceAvPlayerGetStreamInfoEx",
        Target = Generation.Gen5,
        LibraryName = "libSceAvPlayer")]
    public static int AvPlayerGetStreamInfoEx(CpuContext ctx) =>
        GetStreamInfoCore(ctx, StreamInfoExSize);

    [SysAbiExport(
        Nid = "XC9wM+xULz8",
        ExportName = "sceAvPlayerJumpToTime",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceAvPlayer")]
    public static int AvPlayerJumpToTime(CpuContext ctx)
    {
        lock (StateGate)
        {
            if (!Players.TryGetValue(ctx[CpuRegister.Rdi], out var player))
            {
                return SetReturn(ctx, InvalidParameters);
            }

            player.ResetPlayback();
            player.Started = true;
            return SetReturn(ctx, 0);
        }
    }

    [SysAbiExport(
        Nid = "yN7Jhuv8g24",
        ExportName = "sceAvPlayerVprintf",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceAvPlayer")]
    public static int AvPlayerVprintf(CpuContext ctx) => SetReturn(ctx, 0);

    [SysAbiExport(
        Nid = "UbQoYawOsfY",
        ExportName = "sceAvPlayerIsActive",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceAvPlayer")]
    public static int AvPlayerIsActive(CpuContext ctx)
    {
        PlayerState? stoppedPlayer = null;
        var active = false;
        lock (StateGate)
        {
            if (Players.TryGetValue(ctx[CpuRegister.Rdi], out var player))
            {
                active = player.Started && !player.EndOfStream;
                if (player.PendingStopEvent)
                {
                    player.PendingStopEvent = false;
                    stoppedPlayer = player;
                }
            }
        }

        // The host decoder finishes on a worker thread, where invoking guest
        // callbacks would race the CPU context. Deliver its deferred Stop event
        // from this guest poll instead.
        if (stoppedPlayer is not null)
        {
            NotifyEvent(ctx, stoppedPlayer, 1); // StateStop
        }

        return SetReturn(ctx, active ? 1 : 0);
    }

    [SysAbiExport(
        Nid = "o3+RWnHViSg",
        ExportName = "sceAvPlayerGetVideoData",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceAvPlayer")]
    public static int AvPlayerGetVideoData(CpuContext ctx) => GetVideoData(ctx, extended: false);

    [SysAbiExport(
        Nid = "JdksQu8pNdQ",
        ExportName = "sceAvPlayerGetVideoDataEx",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceAvPlayer")]
    public static int AvPlayerGetVideoDataEx(CpuContext ctx) => GetVideoData(ctx, extended: true);

    [SysAbiExport(
        Nid = "Wnp1OVcrZgk",
        ExportName = "sceAvPlayerGetAudioData",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceAvPlayer")]
    public static int AvPlayerGetAudioData(CpuContext ctx)
    {
        var infoAddress = ctx[CpuRegister.Rsi];
        lock (StateGate)
        {
            if (!Players.TryGetValue(ctx[CpuRegister.Rdi], out var player) ||
                infoAddress == 0 || !player.Started || player.Paused || player.EndOfStream ||
                player.SourcePath is null || !EnsureAudioDecoder(player))
            {
                return SetReturn(ctx, 0);
            }

            const int samplesPerFrame = 1024;
            const int channelCount = 2;
            const int sampleRate = 48_000;
            const int audioFrameSize = samplesPerFrame * channelCount * sizeof(short);
            if (player.RawAudioFrame is null ||
                !ReadExactly(player.AudioDecoderOutput, player.RawAudioFrame))
            {
                return SetReturn(ctx, 0);
            }
            if (player.AudioBufferBase == 0)
            {
                if (!KernelMemoryCompatExports.TryAllocateHleData(
                        ctx,
                        audioFrameSize * 8UL,
                        0x100,
                        out var audioBufferBase))
                {
                    return SetReturn(ctx, 0);
                }
                player.AudioBufferBase = audioBufferBase;
            }

            var bufferAddress = player.AudioBufferBase +
                checked((ulong)(player.NextAudioBuffer * audioFrameSize));
            player.NextAudioBuffer = (player.NextAudioBuffer + 1) % 8;
            if (!ctx.Memory.TryWrite(bufferAddress, player.RawAudioFrame))
            {
                return SetReturn(ctx, 0);
            }

            var timestamp = checked((ulong)(player.NextAudioFrameIndex * samplesPerFrame * 1000L / sampleRate));
            player.NextAudioFrameIndex++;
            Span<byte> info = stackalloc byte[FrameInfoSize];
            info.Clear();
            BinaryPrimitives.WriteUInt64LittleEndian(info[0..], bufferAddress);
            BinaryPrimitives.WriteUInt64LittleEndian(info[16..], timestamp);
            BinaryPrimitives.WriteUInt16LittleEndian(info[24..], channelCount);
            BinaryPrimitives.WriteUInt32LittleEndian(info[28..], sampleRate);
            BinaryPrimitives.WriteUInt32LittleEndian(info[32..], audioFrameSize);
            if (!ctx.Memory.TryWrite(infoAddress, info))
            {
                return SetReturn(ctx, 0);
            }
            Trace($"audio_frame handle=0x{player.Handle:X16} ts={timestamp} data=0x{bufferAddress:X16}");
            return SetReturn(ctx, 1);
        }
    }

    [SysAbiExport(
        Nid = "wwM99gjFf1Y",
        ExportName = "sceAvPlayerCurrentTime",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceAvPlayer")]
    public static int AvPlayerCurrentTime(CpuContext ctx)
    {
        lock (StateGate)
        {
            if (!Players.TryGetValue(ctx[CpuRegister.Rdi], out var player))
            {
                return SetReturn(ctx, InvalidParameters);
            }

            var milliseconds = (ulong)player.PlaybackClock.ElapsedMilliseconds;
            ctx[CpuRegister.Rax] = milliseconds;
            return unchecked((int)milliseconds);
        }
    }

    [SysAbiExport(
        Nid = "hdTyRzCXQeQ",
        ExportName = "sceAvPlayerStreamCount",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceAvPlayer")]
    public static int AvPlayerStreamCount(CpuContext ctx)
    {
        lock (StateGate)
        {
            return SetReturn(ctx, Players.ContainsKey(ctx[CpuRegister.Rdi]) ? 2 : InvalidParameters);
        }
    }

    internal static void RegisterPlayerForTest(
        ulong handle,
        int width,
        int height,
        ulong durationMilliseconds,
        bool pendingStopEvent = false,
        bool endOfStream = false,
        ulong eventCallback = 0,
        ulong eventObject = 0,
        bool completedHostFallback = false,
        bool looping = false)
    {
        PlayerState? previous;
        lock (StateGate)
        {
            Players.Remove(handle, out previous);
            Players[handle] = new PlayerState
            {
                Handle = handle,
                Width = width,
                Height = height,
                DurationMilliseconds = durationMilliseconds,
                PendingStopEvent = pendingStopEvent,
                EndOfStream = endOfStream,
                EventCallback = eventCallback,
                EventObject = eventObject,
                HostVideoTask = completedHostFallback ? Task.CompletedTask : null,
                Looping = looping,
            };
        }

        previous?.Dispose();
    }

    internal static void RemovePlayerForTest(ulong handle)
    {
        PlayerState? player;
        lock (StateGate)
        {
            Players.Remove(handle, out player);
        }

        player?.Dispose();
    }

    [SysAbiExport(
        Nid = "d8FcbzfAdQw",
        ExportName = "sceAvPlayerGetStreamInfo",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceAvPlayer")]
    public static int AvPlayerGetStreamInfo(CpuContext ctx) =>
        GetStreamInfoCore(ctx, StreamInfoSize);

    private static int GetStreamInfoCore(CpuContext ctx, int infoSize)
    {
        var streamIndex = unchecked((uint)ctx[CpuRegister.Rsi]);
        var infoAddress = ctx[CpuRegister.Rdx];
        lock (StateGate)
        {
            if (!Players.TryGetValue(ctx[CpuRegister.Rdi], out var player) ||
                streamIndex > 1 || infoAddress == 0 || player.Width <= 0 || player.Height <= 0)
            {
                return SetReturn(ctx, InvalidParameters);
            }

            if (infoSize == StreamInfoSize)
            {
                if (!TryWriteStreamInfo(
                        ctx,
                        infoAddress,
                        streamIndex,
                        player.Width,
                        player.Height))
                {
                    return SetReturn(ctx, InvalidParameters);
                }
            }
            else
            {
                Span<byte> info = stackalloc byte[StreamInfoExSize];
                info.Clear();
                BinaryPrimitives.WriteUInt32LittleEndian(info, streamIndex);
                if (streamIndex == 0)
                {
                    BinaryPrimitives.WriteUInt32LittleEndian(info[8..], checked((uint)player.Width));
                    BinaryPrimitives.WriteUInt32LittleEndian(info[12..], checked((uint)player.Height));
                    BinaryPrimitives.WriteSingleLittleEndian(
                        info[16..],
                        (float)player.Width / player.Height);
                }
                else
                {
                    BinaryPrimitives.WriteUInt16LittleEndian(info[8..], 2);
                    BinaryPrimitives.WriteUInt32LittleEndian(info[12..], 48_000);
                }

                BinaryPrimitives.WriteUInt64LittleEndian(
                    info[24..],
                    player.DurationMilliseconds);
                if (!ctx.Memory.TryWrite(infoAddress, info))
                {
                    return SetReturn(ctx, InvalidParameters);
                }
            }

            return SetReturn(ctx, 0);
        }
    }

    internal static bool TryWriteStreamInfo(
        CpuContext ctx,
        ulong infoAddress,
        uint streamIndex,
        int width,
        int height)
    {
        if (infoAddress == 0 || streamIndex > 1 || width <= 0 || height <= 0)
        {
            return false;
        }

        Span<byte> info = stackalloc byte[StreamInfoSize];
        info.Clear();
        if (streamIndex == 0)
        {
            BinaryPrimitives.WriteUInt32LittleEndian(info, 1); // SceAvPlayerStreamTypeVideo
            BinaryPrimitives.WriteUInt32LittleEndian(info[8..], checked((uint)width));
            BinaryPrimitives.WriteUInt32LittleEndian(info[12..], checked((uint)height));
        }
        else
        {
            BinaryPrimitives.WriteUInt32LittleEndian(info, 2); // SceAvPlayerStreamTypeAudio
            BinaryPrimitives.WriteUInt32LittleEndian(info[8..], 2); // channels
            BinaryPrimitives.WriteUInt32LittleEndian(info[12..], 48_000); // sample rate
        }

        return ctx.Memory.TryWrite(infoAddress, info);
    }

    private static int AddSource(CpuContext ctx, string guestPath)
    {
        PlayerState player;
        bool autoStart;
        lock (StateGate)
        {
            if (!Players.TryGetValue(ctx[CpuRegister.Rdi], out var foundPlayer))
            {
                return SetReturn(ctx, InvalidParameters);
            }
            player = foundPlayer;

            var hostPath = ResolveGuestPath(guestPath);
            if (hostPath is null || !ProbeVideo(hostPath, out var width, out var height, out var fps, out var duration))
            {
                Console.Error.WriteLine($"[AVPLAYER][ERROR] Could not open guest video '{guestPath}' (resolved '{hostPath ?? "<none>"}').");
                return SetReturn(ctx, OperationFailed);
            }

            player.ResetPlayback();
            player.SourcePath = hostPath;
            player.Width = width;
            player.Height = height;
            player.FramesPerSecond = fps;
            player.DurationMilliseconds = duration;
            player.Started = player.AutoStart;
            autoStart = player.AutoStart;
            Trace($"source guest='{guestPath}' host='{hostPath}' {width}x{height} fps={fps:F3} duration_ms={duration} auto_start={player.AutoStart}");
        }


        NotifyEvent(ctx, player, 2); // StateReady
        if (autoStart)
        {
            NotifyEvent(ctx, player, 3); // StatePlay
        }
        return SetReturn(ctx, 0);
    }

    private static int GetVideoData(CpuContext ctx, bool extended)
    {
        var infoAddress = ctx[CpuRegister.Rsi];
        PlayerState? stoppedPlayer = null;
        int result;
        lock (StateGate)
        {
            if (!Players.TryGetValue(ctx[CpuRegister.Rdi], out var player) || infoAddress == 0)
            {
                return SetReturn(ctx, 0);
            }

            if (player.PendingStopEvent)
            {
                player.PendingStopEvent = false;
                stoppedPlayer = player;
                result = SetReturn(ctx, 0);
            }
            else if (!player.Started || player.Paused || player.EndOfStream || player.SourcePath is null)
            {
                result = SetReturn(ctx, 0);
            }
            else if (!EnsureDecoder(player))
            {
                if (CompletePlayback(player))
                {
                    stoppedPlayer = player;
                }
                result = SetReturn(ctx, 0);
            }
            else
            {
                var fps = Math.Max(1.0, player.FramesPerSecond);
                var expectedFrame = (long)Math.Floor(player.PlaybackClock.Elapsed.TotalSeconds * fps);
                var exhausted = false;
                while (player.NextFrameIndex < expectedFrame)
                {
                    if (!ReadFrame(player))
                    {
                        exhausted = true;
                        break;
                    }
                    player.NextFrameIndex++;
                }

                if (exhausted || !ReadFrame(player))
                {
                    if (CompletePlayback(player))
                    {
                        stoppedPlayer = player;
                    }
                    result = SetReturn(ctx, 0);
                }
                else
                {
                    var timestamp = checked((ulong)Math.Round(player.NextFrameIndex * 1000.0 / fps));
                    player.NextFrameIndex++;
                    if (!WriteVideoFrame(ctx, player, infoAddress, timestamp, extended))
                    {
                        result = SetReturn(ctx, 0);
                    }
                    else
                    {
                        Trace($"video_frame handle=0x{player.Handle:X16} ex={extended} ts={timestamp} data=0x{player.LastGuestBuffer:X16}");
                        result = SetReturn(ctx, 1);
                    }
                }
            }
        }

        // Event callbacks execute guest code and may immediately query the
        // player, so they must run after StateGate has been released.  EOF is
        // the same state transition as sceAvPlayerStop; without this callback
        // titles can wait forever on a black transition after a movie.
        if (stoppedPlayer is not null)
        {
            NotifyEvent(ctx, stoppedPlayer, 1); // StateStop
        }

        return result;
    }

    private static bool CompletePlayback(PlayerState player)
    {
        if (player.Looping)
        {
            player.ResetPlayback();
            player.Started = true;
            return false;
        }

        if (player.EndOfStream)
        {
            return false;
        }

        player.EndOfStream = true;
        player.PlaybackClock.Stop();
        return true;
    }

    private static bool EnsureDecoder(PlayerState player)
    {
        if (player.DecoderOutput is not null)
        {
            return true;
        }

        var ffmpeg = FindFfmpeg();
        if (ffmpeg is null || player.SourcePath is null)
        {
            Console.Error.WriteLine("[AVPLAYER][ERROR] FFmpeg was not found. Set SHARPEMU_FFMPEG_PATH.");
            return false;
        }

        var startInfo = new ProcessStartInfo(ffmpeg)
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };
        startInfo.ArgumentList.Add("-nostdin");
        startInfo.ArgumentList.Add("-hide_banner");
        startInfo.ArgumentList.Add("-loglevel");
        startInfo.ArgumentList.Add("error");
        startInfo.ArgumentList.Add("-i");
        startInfo.ArgumentList.Add(player.SourcePath);
        startInfo.ArgumentList.Add("-map");
        startInfo.ArgumentList.Add("0:v:0");
        startInfo.ArgumentList.Add("-an");
        startInfo.ArgumentList.Add("-pix_fmt");
        startInfo.ArgumentList.Add("nv12");
        startInfo.ArgumentList.Add("-f");
        startInfo.ArgumentList.Add("rawvideo");
        startInfo.ArgumentList.Add("pipe:1");

        try
        {
            player.Decoder = Process.Start(startInfo);
            if (player.Decoder is null)
            {
                return false;
            }
            player.Decoder.ErrorDataReceived += (_, eventArgs) =>
            {
                if (!string.IsNullOrWhiteSpace(eventArgs.Data))
                {
                    Console.Error.WriteLine($"[AVPLAYER][FFMPEG] {eventArgs.Data}");
                }
            };
            player.Decoder.BeginErrorReadLine();
            player.DecoderOutput = player.Decoder.StandardOutput.BaseStream;
            player.RawFrame = new byte[checked(player.Width * player.Height * 3 / 2)];
            player.PlaybackClock.Start();
            Trace($"decoder_started pid={player.Decoder.Id} source='{player.SourcePath}'");
            return true;
        }
        catch (Exception exception) when (exception is IOException or InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            Console.Error.WriteLine($"[AVPLAYER][ERROR] Failed to launch FFmpeg: {exception.Message}");
            player.Dispose();
            return false;
        }
    }

    private static bool EnsureAudioDecoder(PlayerState player)
    {
        if (player.AudioDecoderOutput is not null)
        {
            return true;
        }

        var ffmpeg = FindFfmpeg();
        if (ffmpeg is null || player.SourcePath is null)
        {
            return false;
        }

        var startInfo = new ProcessStartInfo(ffmpeg)
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };
        startInfo.ArgumentList.Add("-nostdin");
        startInfo.ArgumentList.Add("-hide_banner");
        startInfo.ArgumentList.Add("-loglevel");
        startInfo.ArgumentList.Add("error");
        startInfo.ArgumentList.Add("-i");
        startInfo.ArgumentList.Add(player.SourcePath);
        startInfo.ArgumentList.Add("-map");
        startInfo.ArgumentList.Add("0:a:0");
        startInfo.ArgumentList.Add("-vn");
        startInfo.ArgumentList.Add("-ac");
        startInfo.ArgumentList.Add("2");
        startInfo.ArgumentList.Add("-ar");
        startInfo.ArgumentList.Add("48000");
        startInfo.ArgumentList.Add("-f");
        startInfo.ArgumentList.Add("s16le");
        startInfo.ArgumentList.Add("pipe:1");

        try
        {
            player.AudioDecoder = Process.Start(startInfo);
            if (player.AudioDecoder is null)
            {
                return false;
            }
            player.AudioDecoder.ErrorDataReceived += (_, eventArgs) =>
            {
                if (!string.IsNullOrWhiteSpace(eventArgs.Data))
                {
                    Console.Error.WriteLine($"[AVPLAYER][FFMPEG-AUDIO] {eventArgs.Data}");
                }
            };
            player.AudioDecoder.BeginErrorReadLine();
            player.AudioDecoderOutput = player.AudioDecoder.StandardOutput.BaseStream;
            player.RawAudioFrame = new byte[1024 * 2 * sizeof(short)];
            Trace($"audio_decoder_started pid={player.AudioDecoder.Id} source='{player.SourcePath}'");
            return true;
        }
        catch (Exception exception) when (exception is IOException or InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            Console.Error.WriteLine($"[AVPLAYER][ERROR] Failed to launch FFmpeg audio decoder: {exception.Message}");
            player.AudioDecoderOutput?.Dispose();
            player.AudioDecoderOutput = null;
            player.AudioDecoder?.Dispose();
            player.AudioDecoder = null;
            return false;
        }
    }

    private static bool ReadFrame(PlayerState player)
    {
        if (player.DecoderOutput is null || player.RawFrame is null)
        {
            return false;
        }

        try
        {
            return ReadExactly(player.DecoderOutput, player.RawFrame);
        }
        catch (IOException exception)
        {
            Console.Error.WriteLine($"[AVPLAYER][ERROR] FFmpeg stream read failed: {exception.Message}");
            return false;
        }
    }

    private static bool ReadExactly(Stream? stream, byte[] buffer)
    {
        if (stream is null)
        {
            return false;
        }
        var offset = 0;
        while (offset < buffer.Length)
        {
            var read = stream.Read(buffer, offset, buffer.Length - offset);
            if (read == 0)
            {
                return false;
            }
            offset += read;
        }
        return true;
    }

    private static bool WriteVideoFrame(
        CpuContext ctx,
        PlayerState player,
        ulong infoAddress,
        ulong timestamp,
        bool extended)
    {
        if (player.RawFrame is null)
        {
            return false;
        }

        var alignedWidth = AlignUp(player.Width, 16);
        var alignedHeight = AlignUp(player.Height, 16);
        var bufferStride = checked(alignedWidth * alignedHeight * 3 / 2);
        if (player.GuestBuffers[0] == 0)
        {
            if (!AllocateGuestVideoBuffers(ctx, player, bufferStride))
            {
                return false;
            }
            player.GuestBufferStride = bufferStride;
        }

        var frameData = player.RawFrame;
        if (!extended && (alignedWidth != player.Width || alignedHeight != player.Height))
        {
            player.PaddedFrame ??= new byte[bufferStride];
            player.PaddedFrame.AsSpan().Clear();
            for (var row = 0; row < player.Height; row++)
            {
                player.RawFrame.AsSpan(row * player.Width, player.Width)
                    .CopyTo(player.PaddedFrame.AsSpan(row * alignedWidth, player.Width));
            }
            var rawChromaOffset = player.Width * player.Height;
            var paddedChromaOffset = alignedWidth * alignedHeight;
            for (var row = 0; row < player.Height / 2; row++)
            {
                player.RawFrame.AsSpan(rawChromaOffset + (row * player.Width), player.Width)
                    .CopyTo(player.PaddedFrame.AsSpan(paddedChromaOffset + (row * alignedWidth), player.Width));
            }
            frameData = player.PaddedFrame;
        }

        var bufferAddress = player.GuestBuffers[player.NextGuestBuffer];
        player.NextGuestBuffer = (player.NextGuestBuffer + 1) % FrameBufferCount;
        player.LastGuestBuffer = bufferAddress;
        if (!ctx.Memory.TryWrite(bufferAddress, frameData))
        {
            return false;
        }

        Span<byte> info = extended
            ? stackalloc byte[FrameInfoExSize]
            : stackalloc byte[FrameInfoSize];
        info.Clear();
        BinaryPrimitives.WriteUInt64LittleEndian(info[0..], bufferAddress);
        BinaryPrimitives.WriteUInt64LittleEndian(info[16..], timestamp);
        BinaryPrimitives.WriteUInt32LittleEndian(info[24..], checked((uint)(extended ? player.Width : alignedWidth)));
        BinaryPrimitives.WriteUInt32LittleEndian(info[28..], checked((uint)(extended ? player.Height : alignedHeight)));
        BinaryPrimitives.WriteSingleLittleEndian(info[32..], 1.0f);
        if (extended)
        {
            BinaryPrimitives.WriteUInt32LittleEndian(info[60..], checked((uint)player.Width));
            info[64] = 8;
            info[65] = 8;
        }
        return ctx.Memory.TryWrite(infoAddress, info);
    }

    private static bool AllocateGuestVideoBuffers(CpuContext ctx, PlayerState player, int bufferSize)
    {
        var scheduler = GuestThreadExecution.Scheduler;
        if (!player.TextureAllocatorFailed && player.AllocateTextureCallback != 0 && scheduler is not null)
        {
            if (TryAllocateGuestVideoBuffersUsingCallback(
                    ctx,
                    player,
                    scheduler,
                    player.AllocateTextureCallback,
                    bufferSize,
                    "texture"))
            {
                return true;
            }
            player.TextureAllocatorFailed = true;
        }

        // Some Gen5 titles keep their decoder-only texture heap disabled while
        // boot videos are playing. The ordinary AvPlayer allocator is still a
        // guest-owned, GPU-mapped allocation and is the ABI-defined fallback.
        if (!player.GeneralAllocatorFailed && player.AllocateCallback != 0 && scheduler is not null)
        {
            if (TryAllocateGuestVideoBuffersUsingCallback(
                    ctx,
                    player,
                    scheduler,
                    player.AllocateCallback,
                    bufferSize,
                    "general"))
            {
                return true;
            }
            player.GeneralAllocatorFailed = true;
        }

        const int gpuCoherentMemoryType = 0x0C;
        var totalBufferSize = checked((ulong)bufferSize * FrameBufferCount);
        var usedGpuDirectMemory = KernelMemoryCompatExports.TryAllocateHleDirectData(
                ctx,
                totalBufferSize,
                0x10000,
                gpuCoherentMemoryType,
                out var bufferBase);
        if (!usedGpuDirectMemory &&
            !KernelMemoryCompatExports.TryAllocateHleData(
                ctx,
                totalBufferSize,
                0x1000,
                out bufferBase))
        {
            return false;
        }
        for (var index = 0; index < player.GuestBuffers.Length; index++)
        {
            player.GuestBuffers[index] = bufferBase + checked((ulong)(index * bufferSize));
        }
        if (usedGpuDirectMemory)
        {
            Trace(
                $"guest_video_buffers_gpu_direct base=0x{bufferBase:X16} " +
                $"size=0x{totalBufferSize:X} memory_type=0x{gpuCoherentMemoryType:X}");
        }
        else
        {
            Console.Error.WriteLine(
                $"[AVPLAYER][WARN] GPU-direct video allocation unavailable; using generic HLE memory " +
                $"at 0x{bufferBase:X16} size=0x{totalBufferSize:X}");
        }
        StartHostVideoFallback(player);
        return true;
    }

    private static void StartHostVideoFallback(PlayerState player)
    {
        if (player.HostVideoTask is not null || player.SourcePath is null)
        {
            return;
        }

        var ffmpeg = FindFfmpeg();
        if (ffmpeg is null)
        {
            return;
        }

        var (width, height) = ComputeHostPreviewSize(player.Width, player.Height, 1280);
        var cancellation = new CancellationTokenSource();
        player.HostVideoCancellation = cancellation;
        player.HostVideoTask = Task.Run(
            () => RunHostVideoFallbackAsync(
                player,
                ffmpeg,
                player.SourcePath,
                width,
                height,
                Math.Max(1.0, player.FramesPerSecond),
                cancellation.Token),
            CancellationToken.None);
        Trace($"host_video_fallback_started {width}x{height} fps={player.FramesPerSecond:F3}");
    }

    internal static (int Width, int Height) ComputeHostPreviewSize(
        int sourceWidth,
        int sourceHeight,
        int maximumWidth)
    {
        if (sourceWidth <= 0 || sourceHeight <= 0 || maximumWidth <= 0)
        {
            return (0, 0);
        }
        if (sourceWidth <= maximumWidth)
        {
            return (sourceWidth, sourceHeight & ~1);
        }

        var height = Math.Max(2, (int)Math.Round(maximumWidth * (double)sourceHeight / sourceWidth));
        return (maximumWidth, height & ~1);
    }

    private static async Task RunHostVideoFallbackAsync(
        PlayerState player,
        string ffmpeg,
        string sourcePath,
        int width,
        int height,
        double framesPerSecond,
        CancellationToken cancellationToken)
    {
        var startInfo = new ProcessStartInfo(ffmpeg)
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };
        startInfo.ArgumentList.Add("-nostdin");
        startInfo.ArgumentList.Add("-hide_banner");
        startInfo.ArgumentList.Add("-loglevel");
        startInfo.ArgumentList.Add("error");
        startInfo.ArgumentList.Add("-i");
        startInfo.ArgumentList.Add(sourcePath);
        startInfo.ArgumentList.Add("-map");
        startInfo.ArgumentList.Add("0:v:0");
        startInfo.ArgumentList.Add("-an");
        startInfo.ArgumentList.Add("-vf");
        startInfo.ArgumentList.Add($"scale={width}:{height}:flags=bilinear");
        startInfo.ArgumentList.Add("-pix_fmt");
        startInfo.ArgumentList.Add("rgba");
        startInfo.ArgumentList.Add("-f");
        startInfo.ArgumentList.Add("rawvideo");
        startInfo.ArgumentList.Add("pipe:1");

        Process? process = null;
        try
        {
            process = Process.Start(startInfo);
            if (process is null)
            {
                return;
            }
            player.HostVideoProcess = process;
            var stderrTask = process.StandardError.ReadToEndAsync(cancellationToken);
            var output = process.StandardOutput.BaseStream;
            var frame = new byte[checked(width * height * 4)];
            var clock = Stopwatch.StartNew();
            long frameIndex = 0;
            while (!cancellationToken.IsCancellationRequested &&
                   await ReadExactlyAsync(output, frame, cancellationToken).ConfigureAwait(false))
            {
                var target = TimeSpan.FromSeconds(frameIndex / framesPerSecond);
                var delay = target - clock.Elapsed;
                if (delay > TimeSpan.Zero)
                {
                    await Task.Delay(delay, cancellationToken).ConfigureAwait(false);
                }
                VideoOutExports.SubmitHostRgbaFrame(frame, checked((uint)width), checked((uint)height));
                frameIndex++;
            }

            await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
            var stderr = await stderrTask.ConfigureAwait(false);
            if (process.ExitCode != 0 && !string.IsNullOrWhiteSpace(stderr))
            {
                Console.Error.WriteLine($"[AVPLAYER][WARN] Host video fallback: {stderr.Trim()}");
            }
            else
            {
                Trace($"host_video_fallback_finished frames={frameIndex}");
                CompleteHostVideoFallback(player, frameIndex);
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception exception) when (
            exception is IOException or InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            Console.Error.WriteLine($"[AVPLAYER][WARN] Host video fallback failed: {exception.Message}");
        }
        finally
        {
            if (process is not null)
            {
                try
                {
                    if (!process.HasExited)
                    {
                        process.Kill(entireProcessTree: true);
                    }
                }
                catch (InvalidOperationException)
                {
                }
                process.Dispose();
            }
            if (ReferenceEquals(player.HostVideoProcess, process))
            {
                player.HostVideoProcess = null;
            }
        }
    }

    private static void CompleteHostVideoFallback(PlayerState player, long frameCount)
    {
        if (frameCount == 0)
        {
            return;
        }

        lock (StateGate)
        {
            if (!Players.TryGetValue(player.Handle, out var current) ||
                !ReferenceEquals(current, player) ||
                player.Looping ||
                player.EndOfStream)
            {
                return;
            }

            // Host fallback is the authoritative clock/display path when the
            // title's texture allocators are unavailable. Its EOF must therefore
            // end the guest player too; otherwise IsActive remains true forever
            // after the visible movie has finished.
            player.EndOfStream = true;
            player.Started = false;
            player.PlaybackClock.Stop();
            player.PendingStopEvent = true;
        }
    }

    private static async Task<bool> ReadExactlyAsync(
        Stream stream,
        byte[] buffer,
        CancellationToken cancellationToken)
    {
        var offset = 0;
        while (offset < buffer.Length)
        {
            var read = await stream.ReadAsync(buffer.AsMemory(offset), cancellationToken).ConfigureAwait(false);
            if (read == 0)
            {
                return false;
            }
            offset += read;
        }
        return true;
    }

    private static bool TryAllocateGuestVideoBuffersUsingCallback(
        CpuContext ctx,
        PlayerState player,
        IGuestThreadScheduler scheduler,
        ulong callback,
        int bufferSize,
        string allocatorKind)
    {
        Array.Clear(player.GuestBuffers);
        for (var index = 0; index < player.GuestBuffers.Length; index++)
        {
            if (!scheduler.TryCallGuestFunction(
                    ctx,
                    callback,
                    player.AllocatorObject,
                    0x100,
                    checked((ulong)bufferSize),
                    0,
                    0,
                    $"avplayer_allocate_{allocatorKind}",
                    out var buffer,
                    out var error) || buffer == 0)
            {
                Trace(
                    $"guest_{allocatorKind}_allocator_unavailable index={index} " +
                    $"callback=0x{callback:X16} result={error ?? "returned_null"}");
                Array.Clear(player.GuestBuffers);
                return false;
            }
            player.GuestBuffers[index] = buffer;
            Trace($"{allocatorKind}_buffer index={index} data=0x{buffer:X16} size={bufferSize}");
        }
        return true;
    }

    private static bool ProbeVideo(
        string path,
        out int width,
        out int height,
        out double framesPerSecond,
        out ulong durationMilliseconds)
    {
        width = 0;
        height = 0;
        framesPerSecond = 30.0;
        durationMilliseconds = 0;
        var ffmpeg = FindFfmpeg();
        if (ffmpeg is null)
        {
            return false;
        }
        var ffprobe = FindSiblingExecutable(ffmpeg, "ffprobe");
        if (ffprobe is null)
        {
            return false;
        }

        var startInfo = new ProcessStartInfo(ffprobe)
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };
        startInfo.ArgumentList.Add("-v");
        startInfo.ArgumentList.Add("error");
        startInfo.ArgumentList.Add("-select_streams");
        startInfo.ArgumentList.Add("v:0");
        startInfo.ArgumentList.Add("-show_entries");
        startInfo.ArgumentList.Add("stream=width,height,avg_frame_rate,duration");
        startInfo.ArgumentList.Add("-of");
        startInfo.ArgumentList.Add("default=noprint_wrappers=1");
        startInfo.ArgumentList.Add(path);

        try
        {
            using var process = Process.Start(startInfo);
            if (process is null)
            {
                return false;
            }
            var output = process.StandardOutput.ReadToEnd();
            var error = process.StandardError.ReadToEnd();
            process.WaitForExit();
            if (process.ExitCode != 0)
            {
                Console.Error.WriteLine($"[AVPLAYER][FFPROBE] {error.Trim()}");
                return false;
            }

            foreach (var line in output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                var separator = line.IndexOf('=');
                if (separator < 1)
                {
                    continue;
                }
                var key = line[..separator];
                var value = line[(separator + 1)..];
                switch (key)
                {
                    case "width":
                        _ = int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out width);
                        break;
                    case "height":
                        _ = int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out height);
                        break;
                    case "avg_frame_rate":
                        var parts = value.Split('/');
                        if (parts.Length == 2 &&
                            double.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out var numerator) &&
                            double.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out var denominator) &&
                            denominator != 0)
                        {
                            framesPerSecond = numerator / denominator;
                        }
                        break;
                    case "duration":
                        if (double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var duration))
                        {
                            durationMilliseconds = checked((ulong)Math.Max(0, Math.Round(duration * 1000.0)));
                        }
                        break;
                }
            }
            return width > 0 && height > 0 && framesPerSecond > 0;
        }
        catch (Exception exception) when (exception is IOException or InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            Console.Error.WriteLine($"[AVPLAYER][ERROR] Failed to probe video: {exception.Message}");
            return false;
        }
    }

    private static string? FindFfmpeg() =>
        FindFfmpeg(
            Environment.GetEnvironmentVariable("SHARPEMU_FFMPEG_PATH"),
            Environment.GetEnvironmentVariable("PATH"),
            OperatingSystem.IsWindows());

    internal static string? FindFfmpeg(
        string? configured,
        string? searchPath,
        bool isWindows)
    {
        if (!string.IsNullOrWhiteSpace(configured) && File.Exists(configured))
        {
            return Path.GetFullPath(configured);
        }

        var executable = isWindows ? "ffmpeg.exe" : "ffmpeg";
        foreach (var directory in (searchPath ?? string.Empty)
                     .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            var candidate = Path.Combine(RemovePathQuotes(directory), executable);
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }

        foreach (var candidate in new[] { "/opt/homebrew/bin/ffmpeg", "/usr/local/bin/ffmpeg" })
        {
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }
        return FindExecutableOnPath("ffmpeg", Environment.GetEnvironmentVariable("PATH"));
    }

    internal static string? FindSiblingExecutable(string executablePath, string siblingName)
    {
        var directory = Path.GetDirectoryName(executablePath);
        if (string.IsNullOrWhiteSpace(directory))
        {
            return null;
        }

        var fileName = OperatingSystem.IsWindows() ? siblingName + ".exe" : siblingName;
        var candidate = Path.Combine(directory, fileName);
        return File.Exists(candidate) ? Path.GetFullPath(candidate) : null;
    }

    internal static string? FindExecutableOnPath(string executableName, string? searchPath)
    {
        if (string.IsNullOrWhiteSpace(executableName) || string.IsNullOrWhiteSpace(searchPath))
        {
            return null;
        }

        var fileName = OperatingSystem.IsWindows() && !executableName.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)
            ? executableName + ".exe"
            : executableName;
        foreach (var entry in searchPath.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var directory = entry.Trim('"');
            if (directory.Length == 0)
            {
                continue;
            }

            try
            {
                var candidate = Path.Combine(directory, fileName);
                if (File.Exists(candidate))
                {
                    return Path.GetFullPath(candidate);
                }
            }
            catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException)
            {
                // Ignore malformed PATH entries and continue looking.
            }
        }
        return null;
    }

    internal static string GetFfprobePath(string ffmpeg, bool isWindows) =>
        Path.Combine(
            Path.GetDirectoryName(ffmpeg) ?? string.Empty,
            isWindows ? "ffprobe.exe" : "ffprobe");

    private static string RemovePathQuotes(string directory) =>
        directory.Length >= 2 && directory[0] == '"' && directory[^1] == '"'
            ? directory[1..^1]
            : directory;

    internal static string? ResolveGuestPath(string guestPath)
    {
        if (string.IsNullOrWhiteSpace(guestPath))
        {
            return null;
        }

        var normalized = guestPath.Replace('\\', '/');
        var fileReference = normalized.StartsWith("file:", StringComparison.OrdinalIgnoreCase);
        var unrealProjectRelative =
            normalized.StartsWith("../", StringComparison.Ordinal) ||
            normalized.StartsWith("./", StringComparison.Ordinal);
        if (normalized.StartsWith("file://", StringComparison.OrdinalIgnoreCase) &&
            Uri.TryCreate(normalized, UriKind.Absolute, out var uri) &&
            uri.IsFile)
        {
            if (!string.IsNullOrEmpty(uri.Host) &&
                !string.Equals(uri.Host, "localhost", StringComparison.OrdinalIgnoreCase))
            {
                return null;
            }

            normalized = uri.LocalPath.Replace('\\', '/');
        }
        else if (normalized.StartsWith("file://", StringComparison.OrdinalIgnoreCase))
        {
            // Some console middleware emits Unreal-style project-relative
            // media references such as file://../../../Project/Content/....
            // System.Uri rejects these because the first ".." is parsed as
            // an invalid authority. Treat the scheme as a guest-path marker;
            // the app0 sandbox below resolves the relative path.
            normalized = normalized["file://".Length..];
            unrealProjectRelative = true;
        }
        else if (normalized.StartsWith("file:", StringComparison.OrdinalIgnoreCase))
        {
            normalized = normalized["file:".Length..];
            unrealProjectRelative = true;
        }

        if (unrealProjectRelative)
        {
            if (!TryRemoveUnrealLeadingDotSegments(normalized, out normalized))
            {
                return null;
            }
        }

        var app0 = Environment.GetEnvironmentVariable("SHARPEMU_APP0_DIR");
        if (string.IsNullOrWhiteSpace(app0))
        {
            return null;
        }

        var app0MountedPath = false;
        foreach (var prefix in new[] { "app0:/", "/app0/", "app0/", "app0:" })
        {
            if (normalized.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                normalized = normalized[prefix.Length..];
                app0MountedPath = true;
                break;
            }
        }

        if (!app0MountedPath &&
            (string.Equals(normalized, "app0:", StringComparison.OrdinalIgnoreCase) ||
             string.Equals(normalized, "/app0", StringComparison.OrdinalIgnoreCase) ||
             string.Equals(normalized, "app0", StringComparison.OrdinalIgnoreCase)))
        {
            normalized = string.Empty;
            app0MountedPath = true;
        }

        try
        {
            if (fileReference)
            {
                if (!TryDecodeFileReference(normalized, out normalized))
                {
                    return null;
                }
            }
            else if (ContainsInvalidMediaPathCharacters(normalized))
            {
                return null;
            }

            if ((!fileReference &&
                 !app0MountedPath &&
                 Uri.TryCreate(normalized, UriKind.Absolute, out _)) ||
                Path.IsPathFullyQualified(normalized) ||
                normalized.StartsWith("/", StringComparison.Ordinal))
            {
                return null;
            }

            if (!TryNormalizeApp0RelativePath(normalized, out var relativePath) ||
                relativePath.Length == 0)
            {
                return null;
            }

            var root = Path.GetFullPath(app0);
            var candidate = Path.GetFullPath(Path.Combine(root, relativePath));
            var relativeToRoot = Path.GetRelativePath(root, candidate);
            if (Path.IsPathFullyQualified(relativeToRoot) ||
                string.Equals(relativeToRoot, "..", StringComparison.Ordinal) ||
                relativeToRoot.StartsWith(
                    ".." + Path.DirectorySeparatorChar,
                    StringComparison.Ordinal))
            {
                return null;
            }

            return TryResolveSandboxedFile(root, relativePath, out var resolved)
                ? resolved
                : null;
        }
        catch (Exception exception) when (exception is ArgumentException or
                                             IOException or
                                             NotSupportedException or
                                             UnauthorizedAccessException or
                                             UriFormatException)
        {
            return null;
        }
    }

    private static bool TryRemoveUnrealLeadingDotSegments(
        string guestPath,
        out string normalized)
    {
        var removedParent = false;
        while (guestPath.StartsWith("../", StringComparison.Ordinal) ||
               guestPath.StartsWith("./", StringComparison.Ordinal))
        {
            removedParent |= guestPath.StartsWith("../", StringComparison.Ordinal);
            guestPath = guestPath[(guestPath.IndexOf('/') + 1)..];
        }

        normalized = guestPath;
        return !removedParent || guestPath.Contains('/');
    }

    private static bool TryDecodeFileReference(string encoded, out string decoded)
    {
        decoded = string.Empty;
        for (var index = 0; index < encoded.Length; index++)
        {
            if (encoded[index] != '%')
            {
                continue;
            }

            if (index + 2 >= encoded.Length ||
                !Uri.IsHexDigit(encoded[index + 1]) ||
                !Uri.IsHexDigit(encoded[index + 2]))
            {
                return false;
            }

            var escapedByte = Convert.ToByte(encoded.Substring(index + 1, 2), 16);
            if (escapedByte is (byte)'/' or (byte)'\\')
            {
                return false;
            }

            index += 2;
        }

        decoded = Uri.UnescapeDataString(encoded);
        return !ContainsInvalidMediaPathCharacters(decoded);
    }

    private static bool ContainsInvalidMediaPathCharacters(string path) =>
        path.IndexOfAny(['?', '#']) >= 0 || path.Any(char.IsControl);

    private static bool TryNormalizeApp0RelativePath(
        string guestPath,
        out string relativePath)
    {
        var segments = new List<string>();
        foreach (var segment in guestPath.TrimStart('/').Split(
                     '/',
                     StringSplitOptions.RemoveEmptyEntries))
        {
            if (segment == ".")
            {
                continue;
            }

            if (segment == "..")
            {
                if (segments.Count == 0)
                {
                    relativePath = string.Empty;
                    return false;
                }

                segments.RemoveAt(segments.Count - 1);
                continue;
            }

            segments.Add(segment);
        }

        relativePath = string.Join(Path.DirectorySeparatorChar, segments);
        return true;
    }

    private static bool TryResolveSandboxedFile(
        string root,
        string relativePath,
        out string resolved)
    {
        resolved = string.Empty;
        var current = root;
        var segments = relativePath.Split(
            Path.DirectorySeparatorChar,
            StringSplitOptions.RemoveEmptyEntries);
        for (var index = 0; index < segments.Length; index++)
        {
            var exact = Path.Combine(current, segments[index]);
            var finalSegment = index == segments.Length - 1;
            string? match;
            if (finalSegment ? File.Exists(exact) : Directory.Exists(exact))
            {
                match = exact;
            }
            else
            {
                if (!Directory.Exists(current))
                {
                    return false;
                }

                match = null;
                foreach (var entry in Directory.EnumerateFileSystemEntries(current))
                {
                    if (!string.Equals(
                            Path.GetFileName(entry),
                            segments[index],
                            StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    if (match is not null)
                    {
                        // A case-sensitive host can contain two names that are
                        // indistinguishable to the guest. Refuse an ambiguous
                        // media path instead of selecting one nondeterministically.
                        return false;
                    }

                    match = entry;
                }
            }

            if (match is null ||
                (finalSegment ? !File.Exists(match) : !Directory.Exists(match)))
            {
                return false;
            }

            if ((File.GetAttributes(match) & FileAttributes.ReparsePoint) != 0)
            {
                // App packages do not need host filesystem links. Refusing
                // them keeps media resolution inside the configured app0
                // tree even when a dump contains a symlink or junction.
                return false;
            }

            current = match;
        }

        if (!File.Exists(current))
        {
            return false;
        }

        resolved = Path.GetFullPath(current);
        return true;
    }

    private static bool TryReadNullTerminatedUtf8(CpuContext ctx, ulong address, int maxLength, out string value)
    {
        value = string.Empty;
        if (address == 0 || maxLength <= 0)
        {
            return false;
        }
        var bytes = new List<byte>(Math.Min(maxLength, 256));
        Span<byte> single = stackalloc byte[1];
        for (var index = 0; index < maxLength; index++)
        {
            if (!ctx.Memory.TryRead(address + (ulong)index, single))
            {
                return false;
            }
            if (single[0] == 0)
            {
                value = Encoding.UTF8.GetString(bytes.ToArray());
                return true;
            }
            bytes.Add(single[0]);
        }
        return false;
    }

    private static bool TryReadUtf8(CpuContext ctx, ulong address, int length, out string value)
    {
        value = string.Empty;
        if (address == 0 || length <= 0)
        {
            return false;
        }
        var bytes = new byte[length];
        if (!ctx.Memory.TryRead(address, bytes))
        {
            return false;
        }
        value = Encoding.UTF8.GetString(bytes);
        return true;
    }

    private static bool TryReadByte(CpuContext ctx, ulong address, out byte value)
    {
        Span<byte> buffer = stackalloc byte[1];
        if (!ctx.Memory.TryRead(address, buffer))
        {
            value = 0;
            return false;
        }
        value = buffer[0];
        return true;
    }

    private static bool TryReadUInt32(CpuContext ctx, ulong address, out uint value)
    {
        Span<byte> buffer = stackalloc byte[sizeof(uint)];
        if (!ctx.Memory.TryRead(address, buffer))
        {
            value = 0;
            return false;
        }
        value = BinaryPrimitives.ReadUInt32LittleEndian(buffer);
        return true;
    }

    private static bool TryReadUInt64(CpuContext ctx, ulong address, out ulong value) =>
        ctx.TryReadUInt64(address, out value);

    private static void NotifyEvent(CpuContext ctx, PlayerState player, ulong eventId)
    {
        if (player.EventCallback == 0)
        {
            Trace($"event skipped handle=0x{player.Handle:X16} id={eventId} callback=0");
            return;
        }

        var scheduler = GuestThreadExecution.Scheduler;
        string? error = null;
        if (scheduler is null ||
            !scheduler.TryCallGuestFunction(
                ctx,
                player.EventCallback,
                player.EventObject,
                eventId,
                0,
                0,
                0,
                $"avplayer_event_{eventId}",
                out _,
                out error))
        {
            Console.Error.WriteLine(
                $"[AVPLAYER][WARN] Event callback failed handle=0x{player.Handle:X16} " +
                $"event={eventId} callback=0x{player.EventCallback:X16}: {error ?? "scheduler unavailable"}");
            return;
        }

        Trace($"event handle=0x{player.Handle:X16} id={eventId} callback=0x{player.EventCallback:X16}");
    }

    private static int AlignUp(int value, int alignment) =>
        checked((value + alignment - 1) & -alignment);

    private static int ValidatePlayer(CpuContext ctx)
    {
        lock (StateGate)
        {
            return SetReturn(ctx, Players.ContainsKey(ctx[CpuRegister.Rdi]) ? 0 : InvalidParameters);
        }
    }

    private static int SetReturn(CpuContext ctx, int result)
    {
        ctx[CpuRegister.Rax] = unchecked((ulong)result);
        return result;
    }

    private static void Trace(string message)
    {
        var count = Interlocked.Increment(ref _traceCount);
        if (count <= 32 || count % 300 == 0)
        {
            Console.Error.WriteLine($"[AVPLAYER][INFO] {message}");
        }
    }
}
