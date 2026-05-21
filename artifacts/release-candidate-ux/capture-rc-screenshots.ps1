Add-Type -AssemblyName System.Windows.Forms
Add-Type -AssemblyName System.Drawing
Add-Type -AssemblyName UIAutomationClient
Add-Type -AssemblyName UIAutomationTypes
Add-Type @'
using System;
using System.Runtime.InteropServices;

public static class NativeWin
{
    [DllImport("user32.dll")] public static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int X, int Y, int cx, int cy, uint uFlags);
    [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr hWnd);
    [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr hWnd, out RECT lpRect);
    [DllImport("user32.dll")] public static extern uint GetDpiForWindow(IntPtr hWnd);
    [DllImport("user32.dll")] public static extern bool SetCursorPos(int X, int Y);
    [DllImport("user32.dll")] public static extern void mouse_event(uint dwFlags, uint dx, uint dy, uint dwData, UIntPtr dwExtraInfo);

    public struct RECT
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }
}
'@

$ErrorActionPreference = 'Stop'

$artifact = 'C:\Users\justi\Downloads\files\artifacts\release-candidate-ux'
New-Item -ItemType Directory -Force -Path $artifact | Out-Null

$env:LOCALAPPDATA = Join-Path $artifact 'localappdata-final'
New-Item -ItemType Directory -Force -Path $env:LOCALAPPDATA | Out-Null

$exe = 'C:\Users\justi\Downloads\files\bin\Debug\net8.0-windows\win-x64\MeshChat.exe'
$process = Start-Process -FilePath $exe -PassThru

try {
    $deadline = (Get-Date).AddSeconds(20)
    do {
        Start-Sleep -Milliseconds 250
        $process.Refresh()
    } until ($process.MainWindowHandle -ne 0 -or (Get-Date) -gt $deadline)

    if ($process.MainWindowHandle -eq 0) {
        throw 'MeshChat window handle was not created.'
    }

    $hwnd = $process.MainWindowHandle
    $topMost = [IntPtr](-1)
    [NativeWin]::SetWindowPos($hwnd, $topMost, 80, 80, 1200, 720, 0x0040) | Out-Null
    [NativeWin]::SetForegroundWindow($hwnd) | Out-Null
    Start-Sleep -Seconds 5

    $root = [System.Windows.Automation.AutomationElement]::FromHandle($hwnd)

    function FindByName([string]$name) {
        $condition = New-Object System.Windows.Automation.PropertyCondition(
            [System.Windows.Automation.AutomationElement]::NameProperty,
            $name)
        return $root.FindFirst([System.Windows.Automation.TreeScope]::Descendants, $condition)
    }

    function InvokeOrToggleByName([string]$name) {
        $element = FindByName $name
        if ($null -eq $element) {
            throw "Missing automation element: $name"
        }

        try {
            $invoke = $element.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern)
            $invoke.Invoke()
        }
        catch {
            try {
                $toggle = $element.GetCurrentPattern([System.Windows.Automation.TogglePattern]::Pattern)
                $toggle.Toggle()
            }
            catch {
                $bounds = $element.Current.BoundingRectangle
                if ($bounds.Width -le 0 -or $bounds.Height -le 0) {
                    throw
                }

                [NativeWin]::SetCursorPos([int]($bounds.X + $bounds.Width / 2), [int]($bounds.Y + $bounds.Height / 2)) | Out-Null
                Start-Sleep -Milliseconds 100
                [NativeWin]::mouse_event(0x0002, 0, 0, 0, [UIntPtr]::Zero)
                [NativeWin]::mouse_event(0x0004, 0, 0, 0, [UIntPtr]::Zero)
            }
        }

        Start-Sleep -Milliseconds 650
        $script:root = [System.Windows.Automation.AutomationElement]::FromHandle($hwnd)
    }

    function SetValueByName([string]$name, [string]$value) {
        $element = FindByName $name
        if ($null -eq $element) {
            throw "Missing automation element: $name"
        }

        $pattern = $element.GetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern)
        $pattern.SetValue($value)
        Start-Sleep -Milliseconds 350
        $script:root = [System.Windows.Automation.AutomationElement]::FromHandle($hwnd)
    }

    function SetWindowAndCapture([int]$width, [int]$height, [string]$fileName) {
        [NativeWin]::SetWindowPos($hwnd, $topMost, 80, 80, $width, $height, 0x0040) | Out-Null
        [NativeWin]::SetForegroundWindow($hwnd) | Out-Null
        Start-Sleep -Milliseconds 1000

        $rect = New-Object NativeWin+RECT
        [NativeWin]::GetWindowRect($hwnd, [ref]$rect) | Out-Null
        $actualWidth = $rect.Right - $rect.Left
        $actualHeight = $rect.Bottom - $rect.Top

        $bitmap = New-Object System.Drawing.Bitmap($actualWidth, $actualHeight)
        $graphics = [System.Drawing.Graphics]::FromImage($bitmap)
        $graphics.CopyFromScreen($rect.Left, $rect.Top, 0, 0, $bitmap.Size)

        $path = Join-Path $artifact $fileName
        $bitmap.Save($path, [System.Drawing.Imaging.ImageFormat]::Png)
        $graphics.Dispose()
        $bitmap.Dispose()

        $dpi = [NativeWin]::GetDpiForWindow($hwnd)
        [pscustomobject]@{
            Path = $path
            RequestedWidth = $width
            RequestedHeight = $height
            CapturedWidth = $actualWidth
            CapturedHeight = $actualHeight
            Dpi = $dpi
            Scaling = ('{0:P0}' -f ($dpi / 96.0))
        }
    }

    InvokeOrToggleByName 'Hide network log'
    InvokeOrToggleByName 'Show network log'

    $results = @()
    $results += SetWindowAndCapture 1200 720 '01-1200x720-log-visible.png'

    InvokeOrToggleByName 'Hide network log'
    $results += SetWindowAndCapture 1200 720 '02-1200x720-log-hidden.png'

    InvokeOrToggleByName 'Show network log'
    $results += SetWindowAndCapture 900 600 '03-900x600-log-visible.png'

    SetWindowAndCapture 1200 720 '04-1200x720-log-visible-before-search.png' | Out-Null
    SetValueByName 'Search messages' 'zzzz-no-results-rc'
    $results += SetWindowAndCapture 1200 720 '05-1200x720-search-no-results.png'

    InvokeOrToggleByName 'Clear message search'
    InvokeOrToggleByName 'Message encryption off'
    $results += SetWindowAndCapture 1200 720 '06-1200x720-encrypt-on.png'

    InvokeOrToggleByName 'Hide network log'
    $results += SetWindowAndCapture 1200 720 '07-1200x720-corrected-show-log-label.png'

    $results | ConvertTo-Json -Depth 3
}
finally {
    if ($process -and -not $process.HasExited) {
        $process.CloseMainWindow() | Out-Null
        Start-Sleep -Milliseconds 500
        if (-not $process.HasExited) {
            $process.Kill()
        }
    }
}
