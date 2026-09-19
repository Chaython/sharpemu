param([Parameter(Mandatory=$true)][string]$Path, [int]$Width=1920, [int]$Height=1080)
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing
$taskImagePath = (Resolve-Path -LiteralPath $Path).Path
$taskPixels = [IO.File]::ReadAllBytes($taskImagePath)
if ($taskPixels.Length -ne $Width * $Height * 4) { throw 'Unexpected BGRA frame size.' }
$taskBitmap = [Drawing.Bitmap]::new($Width,$Height,[Drawing.Imaging.PixelFormat]::Format32bppArgb)
try {
    $taskData = $taskBitmap.LockBits([Drawing.Rectangle]::new(0,0,$Width,$Height),[Drawing.Imaging.ImageLockMode]::WriteOnly,[Drawing.Imaging.PixelFormat]::Format32bppArgb)
    try { [Runtime.InteropServices.Marshal]::Copy($taskPixels,0,$taskData.Scan0,$taskPixels.Length) }
    finally { $taskBitmap.UnlockBits($taskData) }
    $taskBitmap.Save("$taskImagePath.png",[Drawing.Imaging.ImageFormat]::Png)
} finally { $taskBitmap.Dispose() }
"$taskImagePath.png"
