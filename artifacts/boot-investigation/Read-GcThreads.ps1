param([UInt64]$Table = 0x809B02670, [UInt64]$Cycle = 0x809B03030)
$ErrorActionPreference = 'Stop'
Add-Type @'
using System;
using System.Runtime.InteropServices;
public static class BootGcMemory {
 [DllImport("kernel32.dll",SetLastError=true)] public static extern IntPtr OpenProcess(uint access,bool inherit,int id);
 [DllImport("kernel32.dll",SetLastError=true)] public static extern bool ReadProcessMemory(IntPtr process,IntPtr address,byte[] buffer,IntPtr size,out IntPtr read);
 [DllImport("kernel32.dll")] public static extern bool CloseHandle(IntPtr handle);
}
'@
$taskExpectedPath = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\bin\Release\net10.0\win-x64\SharpEmu.exe'))
$taskProcess = Get-Process SharpEmu | Where-Object Path -eq $taskExpectedPath | Sort-Object WorkingSet64 -Descending | Select-Object -First 1
if (!$taskProcess) { throw 'No probe running.' }
$taskHandle = [BootGcMemory]::OpenProcess(0x10,$false,$taskProcess.Id)
function Read-GcMemory([UInt64]$Address,[int]$Length) {
    $taskBuffer = New-Object byte[] $Length
    $taskRead = [IntPtr]::Zero
    if (![BootGcMemory]::ReadProcessMemory($taskHandle,[IntPtr][long]$Address,$taskBuffer,[IntPtr]$Length,[ref]$taskRead)) { throw "Read failed at $Address" }
    return ,$taskBuffer
}
try {
    $taskCycleData = Read-GcMemory ($Cycle-8) 16
    'Expected acknowledgements: {0}, collection cycle: {1}' -f [BitConverter]::ToUInt32($taskCycleData,0),[BitConverter]::ToUInt64($taskCycleData,8)
    $taskBuckets = Read-GcMemory $Table 0x800
    $taskSeen = [Collections.Generic.HashSet[UInt64]]::new()
    for ($taskBucket = 0; $taskBucket -lt 256; $taskBucket++) {
        $taskRecord = [BitConverter]::ToUInt64($taskBuckets,$taskBucket*8)
        while ($taskRecord -ne 0 -and $taskSeen.Add($taskRecord)) {
            $taskData = Read-GcMemory $taskRecord 0x108
            'record={0:X16} thread={1:X16} cycle={2} flags={3:X4} rsp={4:X16} stack={5:X16}' -f $taskRecord,[BitConverter]::ToUInt64($taskData,8),[BitConverter]::ToUInt64($taskData,0x10),[BitConverter]::ToUInt16($taskData,0xF8),[BitConverter]::ToUInt64($taskData,0x18),[BitConverter]::ToUInt64($taskData,0x100)
            $taskRecord = [BitConverter]::ToUInt64($taskData,0)
        }
    }
} finally { [void][BootGcMemory]::CloseHandle($taskHandle) }
