param([string]$Executable = (Join-Path $PSScriptRoot '..\artifacts\DomainDesk-win-x64-v1.5.1\DomainDesk.exe'))
$ErrorActionPreference = 'Stop'
Add-Type @'
using System;
using System.Text;
using System.Runtime.InteropServices;
public static class DomainDeskSmokeWindows {
    public delegate bool Callback(IntPtr window, IntPtr parameter);
    [DllImport("user32.dll")] public static extern bool EnumWindows(Callback callback, IntPtr parameter);
    [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr window, out uint processId);
    [DllImport("user32.dll", CharSet=CharSet.Unicode)] public static extern int GetWindowText(IntPtr window, StringBuilder text, int length);
    [DllImport("user32.dll")] public static extern bool PostMessage(IntPtr window, uint message, IntPtr wparam, IntPtr lparam);
}
'@
$Executable = [IO.Path]::GetFullPath($Executable)
if (-not (Test-Path -LiteralPath $Executable)) { throw 'Publish the portable executable first.' }
# Own only the new process; never find/stop a running user application by name.
$owned = Start-Process -FilePath $Executable -WindowStyle Hidden -PassThru
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
            if ($title.ToString().Contains('Domain Desk 1.5.1')) { $script:mainWindow = $window }
        }
        return $true
    }
    [void][DomainDeskSmokeWindows]::EnumWindows($callback,[IntPtr]::Zero)
    if ($script:mainWindow -eq [IntPtr]::Zero) { throw 'Portable main window was not created.' }
    [void][DomainDeskSmokeWindows]::PostMessage($script:mainWindow,0x0010,[IntPtr]::Zero,[IntPtr]::Zero)
    if (-not $owned.WaitForExit(10000)) { throw 'Portable application did not close cleanly.' }
    if ($owned.ExitCode -ne 0) { throw "Portable app failed: $($owned.ExitCode)" }
    Write-Host 'PASS: portable EXE starts its WPF window and exits cleanly. No live servers contacted.'
} finally {
    $owned.Refresh()
    if (-not $owned.HasExited) { $owned.Kill() }
    $owned.Dispose()
}
