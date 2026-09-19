param([string]$Name = 'game-window')
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing
Add-Type @'
using System;
using System.Runtime.InteropServices;
public static class BootWindowCapture {
    [StructLayout(LayoutKind.Sequential)] public struct Rect { public int Left, Top, Right, Bottom; }
    [DllImport("user32.dll")] public static extern bool GetClientRect(IntPtr hwnd, out Rect rect);
    [DllImport("user32.dll")] public static extern bool PrintWindow(IntPtr hwnd, IntPtr hdc, uint flags);
}
'@
$taskProcess = Get-Process SharpEmu | Where-Object { $_.MainWindowHandle -ne 0 -and $_.Path -like '*SharpEmu-Source*' } | Select-Object -First 1
if (!$taskProcess) { throw 'No test game window found.' }
$taskRect = New-Object BootWindowCapture+Rect
[void][BootWindowCapture]::GetClientRect($taskProcess.MainWindowHandle, [ref]$taskRect)
$taskBitmap = New-Object Drawing.Bitmap($taskRect.Right, $taskRect.Bottom)
$taskGraphics = [Drawing.Graphics]::FromImage($taskBitmap)
$taskDc = $taskGraphics.GetHdc()
try { [void][BootWindowCapture]::PrintWindow($taskProcess.MainWindowHandle, $taskDc, 3) }
finally { $taskGraphics.ReleaseHdc($taskDc) }
$taskPath = Join-Path $PSScriptRoot "$Name.png"
$taskBitmap.Save($taskPath, [Drawing.Imaging.ImageFormat]::Png)
$taskGraphics.Dispose()
$taskBitmap.Dispose()
$taskPath
