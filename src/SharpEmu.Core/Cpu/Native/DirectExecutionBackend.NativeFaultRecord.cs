// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System;
using System.IO;
using System.IO.MemoryMappedFiles;

namespace SharpEmu.Core.Cpu.Native;

public sealed unsafe partial class DirectExecutionBackend
{
	private const int NativeFaultRecordSize = 4096;
	private const ulong NativeFaultRecordMagic = 0x31544C5541464D45UL; // "EMFAULT1"
	private MemoryMappedFile? _nativeFaultRecordMap;
	private MemoryMappedViewAccessor? _nativeFaultRecordView;
	private byte* _nativeFaultRecordPointer;
	private bool _nativeFaultRecordPointerAcquired;

	private void SetupNativeFaultRecord()
	{
		if (!OperatingSystem.IsWindows() || _nativeFaultRecordPointer != null)
		{
			return;
		}

		try
		{
			var path = Environment.GetEnvironmentVariable("SHARPEMU_NATIVE_FAULT_RECORD");
			if (string.IsNullOrWhiteSpace(path))
			{
				path = Path.Combine(AppContext.BaseDirectory, "SharpEmuNativeFault.bin");
			}

			path = Path.GetFullPath(path);
			var directory = Path.GetDirectoryName(path);
			if (!string.IsNullOrEmpty(directory))
			{
				Directory.CreateDirectory(directory);
			}

			_nativeFaultRecordMap = MemoryMappedFile.CreateFromFile(
				path,
				FileMode.Create,
				mapName: null,
				capacity: NativeFaultRecordSize,
				MemoryMappedFileAccess.ReadWrite);
			_nativeFaultRecordView = _nativeFaultRecordMap.CreateViewAccessor(
				0,
				NativeFaultRecordSize,
				MemoryMappedFileAccess.ReadWrite);
			byte* pointer = null;
			_nativeFaultRecordView.SafeMemoryMappedViewHandle.AcquirePointer(ref pointer);
			_nativeFaultRecordPointerAcquired = true;
			_nativeFaultRecordPointer = pointer + checked((nint)_nativeFaultRecordView.PointerOffset);
			new Span<byte>(_nativeFaultRecordPointer, NativeFaultRecordSize).Clear();
			_nativeFaultRecordView.Flush();
			Console.Error.WriteLine($"[LOADER][INFO] Native fault record: {path}");
		}
		catch (Exception exception)
		{
			Console.Error.WriteLine($"[LOADER][WARN] Native fault record unavailable: {exception.Message}");
			DisposeNativeFaultRecord();
		}
	}

	private void DisposeNativeFaultRecord()
	{
		if (_nativeFaultRecordPointerAcquired && _nativeFaultRecordView is not null)
		{
			_nativeFaultRecordView.SafeMemoryMappedViewHandle.ReleasePointer();
			_nativeFaultRecordPointerAcquired = false;
		}
		_nativeFaultRecordPointer = null;
		_nativeFaultRecordView?.Dispose();
		_nativeFaultRecordView = null;
		_nativeFaultRecordMap?.Dispose();
		_nativeFaultRecordMap = null;
	}

	// This runs as emitted x64 instructions inside VEH before any reverse P/Invoke.
	// It performs no calls or allocations, so even faults on unmanaged/driver threads
	// leave enough state in the mapped file to identify the crashing instruction.
	private void EmitNativeFaultRecordCapture(byte* code, ref int offset)
	{
		if (_nativeFaultRecordPointer == null)
		{
			return;
		}

		EmitByte(code, ref offset, 0x48); EmitByte(code, ref offset, 0x8B); EmitByte(code, ref offset, 0x01); // mov rax,[rcx]
		EmitByte(code, ref offset, 0x81); EmitByte(code, ref offset, 0x38); EmitUInt32(code, ref offset, 0xC0000005u); // cmp dword [rax],AV
		EmitByte(code, ref offset, 0x0F); EmitByte(code, ref offset, 0x85); // jne done
		var doneJump = offset;
		EmitUInt32(code, ref offset, 0);

		EmitByte(code, ref offset, 0x49); EmitByte(code, ref offset, 0xBA); // mov r10,record
		*(ulong*)(code + offset) = (ulong)_nativeFaultRecordPointer;
		offset += sizeof(ulong);

		EmitCaptureLoadStore(code, ref offset, 0, 16);   // code + flags
		EmitCaptureLoadStore(code, ref offset, 16, 24);  // exception address
		EmitCaptureLoadStore(code, ref offset, 32, 32);  // access type
		EmitCaptureLoadStore(code, ref offset, 40, 40);  // access target
		EmitCaptureLoadStore(code, ref offset, 24, 184); // parameter count

		EmitByte(code, ref offset, 0x48); EmitByte(code, ref offset, 0x8B); EmitByte(code, ref offset, 0x41); EmitByte(code, ref offset, 0x08); // mov rax,[rcx+8]
		EmitCaptureLoadStore(code, ref offset, 248, 48); // RIP
		EmitCaptureLoadStore(code, ref offset, 152, 56); // RSP
		EmitCaptureLoadStore(code, ref offset, 120, 64); // RAX
		EmitCaptureLoadStore(code, ref offset, 144, 72); // RBX
		EmitCaptureLoadStore(code, ref offset, 128, 80); // RCX
		EmitCaptureLoadStore(code, ref offset, 136, 88); // RDX
		EmitCaptureLoadStore(code, ref offset, 168, 96); // RSI
		EmitCaptureLoadStore(code, ref offset, 176, 104); // RDI
		EmitCaptureLoadStore(code, ref offset, 184, 112); // R8
		EmitCaptureLoadStore(code, ref offset, 192, 120); // R9
		EmitCaptureLoadStore(code, ref offset, 200, 128); // R10
		EmitCaptureLoadStore(code, ref offset, 208, 136); // R11
		EmitCaptureLoadStore(code, ref offset, 216, 144); // R12
		EmitCaptureLoadStore(code, ref offset, 224, 152); // R13
		EmitCaptureLoadStore(code, ref offset, 232, 160); // R14
		EmitCaptureLoadStore(code, ref offset, 240, 168); // R15
		EmitCaptureLoadStore(code, ref offset, 160, 176); // RBP

		EmitByte(code, ref offset, 0xF0); EmitByte(code, ref offset, 0x49); EmitByte(code, ref offset, 0xFF); EmitByte(code, ref offset, 0x42); EmitByte(code, ref offset, 0x08); // lock inc qword [r10+8]
		EmitByte(code, ref offset, 0x49); EmitByte(code, ref offset, 0xBB); // mov r11,magic
		*(ulong*)(code + offset) = NativeFaultRecordMagic;
		offset += sizeof(ulong);
		EmitByte(code, ref offset, 0x4D); EmitByte(code, ref offset, 0x89); EmitByte(code, ref offset, 0x1A); // mov [r10],r11

		*(int*)(code + doneJump) = offset - (doneJump + sizeof(int));
	}

	private static void EmitCaptureLoadStore(byte* code, ref int offset, int sourceOffset, int destinationOffset)
	{
		EmitByte(code, ref offset, 0x4C); EmitByte(code, ref offset, 0x8B); EmitByte(code, ref offset, 0x98); // mov r11,[rax+disp32]
		EmitUInt32(code, ref offset, unchecked((uint)sourceOffset));
		EmitByte(code, ref offset, 0x4D); EmitByte(code, ref offset, 0x89); EmitByte(code, ref offset, 0x9A); // mov [r10+disp32],r11
		EmitUInt32(code, ref offset, unchecked((uint)destinationOffset));
	}
}
