# Copyright (C) 2026 SharpEmu Emulator Project
# SPDX-License-Identifier: GPL-2.0-or-later
param([uint64]$Address, [int]$Length = 256, [switch]$Frames)
$ErrorActionPreference = 'Stop'
Add-Type -TypeDefinition @'
using System;
using System.Runtime.InteropServices;
public static class BootProcessMemory {
    [DllImport("kernel32.dll", SetLastError=true)] public static extern IntPtr OpenProcess(uint access, bool inherit, int id);
    [DllImport("kernel32.dll", SetLastError=true)] public static extern bool ReadProcessMemory(IntPtr process, IntPtr address, byte[] buffer, IntPtr size, out IntPtr read);
    [DllImport("kernel32.dll")] public static extern bool CloseHandle(IntPtr handle);
}
'@
$taskExpectedPath = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\bin\Release\net10.0\win-x64\SharpEmu.exe'))
$taskProcess = Get-Process SharpEmu | Where-Object Path -eq $taskExpectedPath | Sort-Object WorkingSet64 -Descending | Select-Object -First 1
$taskHandle = [BootProcessMemory]::OpenProcess(0x10, $false, $taskProcess.Id)
try {
    for ($taskFrame = 0; $taskFrame -lt 30; $taskFrame++) {
        $taskBytes = New-Object byte[] $Length
        $taskRead = [IntPtr]::Zero
        if (![BootProcessMemory]::ReadProcessMemory($taskHandle, [IntPtr][long]$Address, $taskBytes, [IntPtr]$Length, [ref]$taskRead)) { throw "Read failed at $Address : $([Runtime.InteropServices.Marshal]::GetLastWin32Error())" }
        if ($Frames) {
            '{0:X16}: savedRbp={1:X16} return={2:X16}' -f $Address, [BitConverter]::ToUInt64($taskBytes,0), [BitConverter]::ToUInt64($taskBytes,8)
            $Address = [BitConverter]::ToUInt64($taskBytes,0)
            if ($Address -eq 0) { break }
        } else {
            for($taskIndex=0; $taskIndex -lt $Length; $taskIndex+=8) { '{0:X16}: {1:X16}' -f ($Address+$taskIndex),[BitConverter]::ToUInt64($taskBytes,$taskIndex) }
            break
        }
    }
} finally { [void][BootProcessMemory]::CloseHandle($taskHandle) }
