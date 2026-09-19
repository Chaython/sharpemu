# Copyright (C) 2026 SharpEmu Emulator Project
# SPDX-License-Identifier: GPL-2.0-or-later
param([string]$Name = 'probe', [int]$Seconds = 30, [switch]$AllImports)
$ErrorActionPreference = 'Stop'
$taskRoot = $PSScriptRoot
$taskExe = [IO.Path]::GetFullPath((Join-Path $taskRoot '..\bin\Release\net10.0\win-x64\SharpEmu.exe'))
$env:SHARPEMU_LOG_GUEST_THREADS = '0'
$env:SHARPEMU_LOG_GUEST_THREAD_SNAPSHOTS = '1'
$env:SHARPEMU_PERIODIC_SNAPSHOT_SECONDS = '5'
$env:SHARPEMU_LOG_IMPORT_RECENT = '0'
$env:SHARPEMU_STALL_WATCHDOG_SECONDS = '10'
$env:SHARPEMU_LOG_ALL_IMPORTS = [string][int]$AllImports.IsPresent
$taskProc = Start-Process -FilePath $taskExe -ArgumentList @('--log-level=debug', "--log-file=$taskRoot\$Name.log", 'C:\Users\chayt\Downloads\PSX\eboot.bin') -WorkingDirectory $taskRoot -WindowStyle Hidden -RedirectStandardOutput "$taskRoot\$Name.stdout.log" -RedirectStandardError "$taskRoot\$Name.stderr.log" -PassThru
$taskProc.Id | Set-Content "$taskRoot\$Name.pid"
try {
    if ($taskProc.WaitForExit($Seconds * 1000)) {
        "ExitCode=$($taskProc.ExitCode)" | Set-Content "$taskRoot\$Name.result.txt"
    } else {
        "Still running after $Seconds seconds; stopped probe PID $($taskProc.Id)." | Set-Content "$taskRoot\$Name.result.txt"
    }
} finally {
    if (!$taskProc.HasExited) { Stop-Process -Id $taskProc.Id -Force }
}
Get-Content "$taskRoot\$Name.result.txt"
