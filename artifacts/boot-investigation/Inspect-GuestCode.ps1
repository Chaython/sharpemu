# Copyright (C) 2026 SharpEmu Emulator Project
# SPDX-License-Identifier: GPL-2.0-or-later
param([string]$Path, [uint64]$Address, [uint64]$ImageBase = 0, [int]$Length = 256)
$ErrorActionPreference = 'Stop'
Add-Type -Path (Join-Path $PSScriptRoot '..\..\.packages\iced\1.21.0\lib\net45\Iced.dll')
$taskData = [IO.File]::ReadAllBytes($Path)
$taskIsSelf = $taskData[0] -ne 0x7f
$taskElf = 0
$taskSegments = 0
if ($taskIsSelf) {
    $taskSegments = [BitConverter]::ToUInt16($taskData, 24)
    $taskElf = 32 + 32 * $taskSegments
}
$taskPh = $taskElf + [BitConverter]::ToUInt64($taskData, $taskElf + 32)
$taskCount = [BitConverter]::ToUInt16($taskData, $taskElf + 56)
$taskRelative = $Address - $ImageBase
for ($taskI = 0; $taskI -lt $taskCount; $taskI++) {
    $taskHeader = $taskPh + $taskI * 56
    $taskVaddr = [BitConverter]::ToUInt64($taskData, $taskHeader + 16)
    $taskSize = [BitConverter]::ToUInt64($taskData, $taskHeader + 32)
    if ($taskRelative -lt $taskVaddr -or $taskRelative -ge $taskVaddr + $taskSize) { continue }
    $taskOffset = [BitConverter]::ToUInt64($taskData, $taskHeader + 8)
    if ($taskIsSelf) {
        for ($taskJ = 0; $taskJ -lt $taskSegments; $taskJ++) {
            $taskType = [BitConverter]::ToUInt64($taskData, 32 + $taskJ * 32)
            if (($taskType -band 0x800) -ne 0 -and (($taskType -shr 20) -band 0xfff) -eq $taskI) {
                $taskOffset = [BitConverter]::ToUInt64($taskData, 40 + $taskJ * 32)
                break
            }
        }
    }
    $taskOffset += $taskRelative - $taskVaddr
    $taskBytes = New-Object byte[] $Length
    [Array]::Copy($taskData, [long]$taskOffset, $taskBytes, 0, $Length)
    $taskDecoder = [Iced.Intel.Decoder]::Create(64, [Iced.Intel.ByteArrayCodeReader]::new($taskBytes))
    $taskDecoder.IP = $Address
    while ($taskDecoder.IP -lt $Address + $Length) {
        $taskInstruction = $taskDecoder.Decode()
        '{0:X16} {1}' -f $taskInstruction.IP, $taskInstruction.ToString()
    }
    break
}
