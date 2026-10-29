param([string]$Executable = (Join-Path $PSScriptRoot '..\artifacts\DomainDesk-win-x64-v1.6.2\DomainDesk.exe'))
$ErrorActionPreference = 'Stop'
Add-Type @'
using System;
using System.Text;
using System.Runtime.InteropServices;
public static class DomainDeskSmokeWindows {
    [StructLayout(LayoutKind.Sequential)] public struct Rect { public int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential)] public struct MonitorInfo { public int Size; public Rect Monitor, Work; public uint Flags; }
    public delegate bool Callback(IntPtr window, IntPtr parameter);
    public delegate bool MonitorCallback(IntPtr monitor, IntPtr hdc, ref Rect bounds, IntPtr parameter);
    [DllImport("user32.dll")] public static extern bool EnumWindows(Callback callback, IntPtr parameter);
    [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr window, out uint processId);
    [DllImport("user32.dll", CharSet=CharSet.Unicode)] public static extern int GetWindowText(IntPtr window, StringBuilder text, int length);
    [DllImport("user32.dll")] public static extern bool PostMessage(IntPtr window, uint message, IntPtr wparam, IntPtr lparam);
    [DllImport("user32.dll")] public static extern IntPtr SetThreadDpiAwarenessContext(IntPtr context);
    [DllImport("user32.dll")] public static extern bool EnumDisplayMonitors(IntPtr hdc, IntPtr clip, MonitorCallback callback, IntPtr parameter);
    [DllImport("user32.dll", CharSet=CharSet.Auto)] public static extern bool GetMonitorInfo(IntPtr monitor, ref MonitorInfo info);
    [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr window, out Rect bounds);
    [DllImport("user32.dll")] public static extern bool SetWindowPos(IntPtr window, IntPtr after, int x, int y, int width, int height, uint flags);
    [DllImport("user32.dll")] public static extern IntPtr SendMessageTimeout(IntPtr window, uint message, IntPtr wparam, IntPtr lparam, uint flags, uint timeout, out IntPtr result);
    [DllImport("user32.dll")] public static extern bool IsZoomed(IntPtr window);
    [DllImport("user32.dll")] public static extern bool IsIconic(IntPtr window);
}
'@
function Get-OwnedBounds {
    $bounds = [DomainDeskSmokeWindows+Rect]::new()
    if (-not [DomainDeskSmokeWindows]::GetWindowRect($script:mainWindow, [ref]$bounds)) { throw 'Could not read portable window bounds.' }
    return $bounds
}
function Invoke-OwnedWindowCommand([int]$command) {
    $result = [IntPtr]::Zero
    if ([DomainDeskSmokeWindows]::SendMessageTimeout($script:mainWindow, 0x0112, [IntPtr]$command, [IntPtr]::Zero, 2, 5000, [ref]$result) -eq [IntPtr]::Zero) {
        throw 'Portable window did not respond to a window command.'
    }
    Start-Sleep -Milliseconds 250
}
function Assert-SameBounds($actual, $expected, [string]$operation) {
    foreach ($edge in @('Left', 'Top', 'Right', 'Bottom')) {
        if ([Math]::Abs($actual.$edge - $expected.$edge) -gt 2) {
            throw "$operation ($edge): expected $($expected.$edge), got $($actual.$edge)."
        }
    }
}
$Executable = [IO.Path]::GetFullPath($Executable)
if (-not (Test-Path -LiteralPath $Executable)) { throw 'Publish the portable executable first.' }
# Own only the new process; never find/stop a running user application by name.
$owned = Start-Process -FilePath $Executable -WindowStyle Hidden -PassThru
$previousDpiContext = [DomainDeskSmokeWindows]::SetThreadDpiAwarenessContext([IntPtr](-4))
try {
    [void]$owned.WaitForInputIdle(20000)
    Start-Sleep -Milliseconds 1000
    $owned.Refresh()
    if ($owned.HasExited) { throw "Portable app exited unexpectedly: $($owned.ExitCode)" }
    $script:mainWindow = [IntPtr]::Zero
    $callback = [DomainDeskSmokeWindows+Callback] {
        param([IntPtr]$window, [IntPtr]$parameter)
        [uint32]$ownerId = 0
        [void][DomainDeskSmokeWindows]::GetWindowThreadProcessId($window, [ref]$ownerId)
        if ($ownerId -eq $owned.Id) {
            $title = New-Object Text.StringBuilder 512
            [void][DomainDeskSmokeWindows]::GetWindowText($window,$title,$title.Capacity)
            if ($title.ToString().Contains('Domain Desk 1.6.2')) { $script:mainWindow = $window }
        }
        return $true
    }
    [void][DomainDeskSmokeWindows]::EnumWindows($callback,[IntPtr]::Zero)
    if ($script:mainWindow -eq [IntPtr]::Zero) { throw 'Portable main window was not created.' }
    $script:monitors = [Collections.Generic.List[DomainDeskSmokeWindows+MonitorInfo]]::new()
    $monitorCallback = [DomainDeskSmokeWindows+MonitorCallback] {
        param([IntPtr]$monitor, [IntPtr]$hdc, [ref]$bounds, [IntPtr]$parameter)
        $info = [DomainDeskSmokeWindows+MonitorInfo]::new()
        $info.Size = [Runtime.InteropServices.Marshal]::SizeOf($info)
        if (-not [DomainDeskSmokeWindows]::GetMonitorInfo($monitor, [ref]$info)) { return $false }
        $script:monitors.Add($info)
        return $true
    }
    if (-not [DomainDeskSmokeWindows]::EnumDisplayMonitors([IntPtr]::Zero, [IntPtr]::Zero, $monitorCallback, [IntPtr]::Zero) -or $script:monitors.Count -eq 0) {
        throw 'Could not enumerate display work areas.'
    }
    Invoke-OwnedWindowCommand 0xF120 # Restore.
    foreach ($monitor in $script:monitors) {
        $work = $monitor.Work
        if (-not [DomainDeskSmokeWindows]::SetWindowPos($script:mainWindow, [IntPtr]::Zero, $work.Left + 20, $work.Top + 20, 1120, 740, 0x0014)) {
            throw 'Could not move/resize portable window for the display check.'
        }
        Start-Sleep -Milliseconds 250
        $normal = Get-OwnedBounds
        Invoke-OwnedWindowCommand 0xF030 # Maximize using the same system command as the caption button.
        if (-not [DomainDeskSmokeWindows]::IsZoomed($script:mainWindow)) { throw 'Portable window did not maximize.' }
        Assert-SameBounds (Get-OwnedBounds) $work 'Maximized window must exactly fit this monitor work area'
        Invoke-OwnedWindowCommand 0xF120
        if ([DomainDeskSmokeWindows]::IsZoomed($script:mainWindow)) { throw 'Portable window did not restore.' }
        Assert-SameBounds (Get-OwnedBounds) $normal 'Restore must preserve previous window position and size'
        Invoke-OwnedWindowCommand 0xF020 # Minimize.
        if (-not [DomainDeskSmokeWindows]::IsIconic($script:mainWindow)) { throw 'Portable window did not minimize.' }
        Invoke-OwnedWindowCommand 0xF120
        Assert-SameBounds (Get-OwnedBounds) $normal 'Restore from minimized must preserve window bounds'
    }
    [void][DomainDeskSmokeWindows]::PostMessage($script:mainWindow,0x0010,[IntPtr]::Zero,[IntPtr]::Zero)
    if (-not $owned.WaitForExit(10000)) { throw 'Portable application did not close cleanly.' }
    if ($owned.ExitCode -ne 0) { throw "Portable app failed: $($owned.ExitCode)" }
    Write-Host "PASS: portable EXE starts, maximizes within the taskbar work area, restores and minimizes on $($script:monitors.Count) display(s), then exits cleanly. No live servers contacted."
} finally {
    if ($previousDpiContext -ne [IntPtr]::Zero) { [void][DomainDeskSmokeWindows]::SetThreadDpiAwarenessContext($previousDpiContext) }
    $owned.Refresh()
    if (-not $owned.HasExited) { $owned.Kill() }
    $owned.Dispose()
}
