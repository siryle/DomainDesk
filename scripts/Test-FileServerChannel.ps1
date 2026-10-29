param(
    [Parameter(Mandatory = $true)][string]$Server,
    [PSCredential]$Credential,
    [switch]$SkipWmi
)
$ErrorActionPreference = 'Stop'
if ($Server -match '[/\\\s]' -or $Server -in @('localhost', '.', '127.0.0.1', '::1')) { throw 'Specify a remote hostname or IP address only.' }

function Test-TcpPort([string]$Target, [int]$Port) {
    $tcp = [Net.Sockets.TcpClient]::new()
    $timer = [Diagnostics.Stopwatch]::StartNew()
    try {
        $task = $tcp.ConnectAsync($Target, $Port)
        if (-not $task.Wait(2500)) { $state = 'TIMEOUT' }
        elseif ($tcp.Connected) { $state = 'OPEN' }
        else { $state = 'CLOSED' }
    } catch { $state = 'FAILED: ' + $_.Exception.GetBaseException().Message }
    finally { $tcp.Dispose() }
    [pscustomobject]@{ Server = $Target; Port = $Port; State = $state; Milliseconds = $timer.ElapsedMilliseconds }
}

Write-Output ('Client: {0} | {1} | {2:o}' -f $env:COMPUTERNAME, [Security.Principal.WindowsIdentity]::GetCurrent().Name, (Get-Date))
Write-Output 'Read-only diagnostics. No configuration changes, session closes, or file closes.'
Write-Output '--- TCP transport ---'
135,445,5985,5986 | ForEach-Object { Test-TcpPort $Server $_ } | Format-Table -AutoSize | Out-String | Write-Output

if (-not ('DomainDeskRpcProbe' -as [type])) {
Add-Type -TypeDefinition @'
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Runtime.InteropServices;
public static class DomainDeskRpcProbe {
    [StructLayout(LayoutKind.Sequential)] struct InterfaceId { public Guid Id; public ushort Major; public ushort Minor; }
    [DllImport("Rpcrt4.dll", CharSet=CharSet.Unicode, ExactSpelling=true)] static extern int RpcBindingFromStringBindingW(string text, out IntPtr binding);
    [DllImport("Rpcrt4.dll", ExactSpelling=true)] static extern int RpcBindingSetOption(IntPtr binding, uint option, UIntPtr value);
    [DllImport("Rpcrt4.dll", ExactSpelling=true)] static extern int RpcMgmtEpEltInqBegin(IntPtr binding, uint type, IntPtr id, uint version, IntPtr uuid, out IntPtr context);
    [DllImport("Rpcrt4.dll", CharSet=CharSet.Unicode, ExactSpelling=true)] static extern int RpcMgmtEpEltInqNextW(IntPtr context, out InterfaceId id, out IntPtr binding, out Guid uuid, out IntPtr annotation);
    [DllImport("Rpcrt4.dll", CharSet=CharSet.Unicode, ExactSpelling=true)] static extern int RpcBindingToStringBindingW(IntPtr binding, out IntPtr text);
    [DllImport("Rpcrt4.dll", CharSet=CharSet.Unicode, ExactSpelling=true)] static extern int RpcStringFreeW(ref IntPtr text);
    [DllImport("Rpcrt4.dll", ExactSpelling=true)] static extern int RpcBindingFree(ref IntPtr binding);
    [DllImport("Rpcrt4.dll", ExactSpelling=true)] static extern int RpcMgmtEpEltInqDone(ref IntPtr context);
    static void Check(int status) { if (status != 0) throw new Win32Exception(status); }
    public static string[] Enumerate(string server) {
        IntPtr binding = IntPtr.Zero, context = IntPtr.Zero;
        var rows = new List<string>();
        try {
            Check(RpcBindingFromStringBindingW("ncacn_ip_tcp:" + server + "[135]", out binding));
            Check(RpcBindingSetOption(binding, 12, new UIntPtr(5000))); // Per-call timeout, milliseconds.
            Check(RpcMgmtEpEltInqBegin(binding, 0, IntPtr.Zero, 1, IntPtr.Zero, out context));
            for (int i = 0; i < 512; i++) {
                InterfaceId id; Guid uuid;
                IntPtr entry = IntPtr.Zero, annotation = IntPtr.Zero, text = IntPtr.Zero;
                try {
                    int status = RpcMgmtEpEltInqNextW(context, out id, out entry, out uuid, out annotation);
                    if (status == 1772) return rows.ToArray(); // RPC_X_NO_MORE_ENTRIES.
                    Check(status);
                    Check(RpcBindingToStringBindingW(entry, out text));
                    rows.Add(id.Id + " | " + Marshal.PtrToStringUni(text));
                } finally {
                    if (text != IntPtr.Zero) RpcStringFreeW(ref text);
                    if (annotation != IntPtr.Zero) RpcStringFreeW(ref annotation);
                    if (entry != IntPtr.Zero) RpcBindingFree(ref entry);
                }
            }
            throw new InvalidOperationException("Endpoint result exceeds 512 entries; no port probes performed.");
        } finally {
            if (context != IntPtr.Zero) RpcMgmtEpEltInqDone(ref context);
            if (binding != IntPtr.Zero) RpcBindingFree(ref binding);
        }
    }
}
'@
}
Write-Output '--- RPC endpoint mapper (read-only) ---'
try {
    $entries = [DomainDeskRpcProbe]::Enumerate($Server)
    $tcpEntries = @($entries | Where-Object { $_ -match 'ncacn_ip_tcp:' })
    Write-Output ('RPC mapper responded: {0} total endpoints, {1} TCP endpoints.' -f $entries.Count, $tcpEntries.Count)
    $tcpEntries | Write-Output
    # Probe only ports explicitly returned by the target, never a broad port range.
    $ports = @($tcpEntries | ForEach-Object { if ($_ -match '\[(\d+)\]$') { [int]$Matches[1] } } | Sort-Object -Unique)
    Write-Output '--- Advertised TCP endpoints, tested against the requested server ---'
    $ports | ForEach-Object { Test-TcpPort $Server $_ } | Format-Table -AutoSize | Out-String | Write-Output
} catch { Write-Output ('RPC mapper failed: ' + $_.Exception.GetBaseException().Message) }

if ($SkipWmi) { return }
Add-Type -AssemblyName System.Management
Write-Output '--- WMI/DCOM, same PacketPrivacy settings as Domain Desk ---'
$identity = if ($Credential) { $Credential.UserName } else { [Security.Principal.WindowsIdentity]::GetCurrent().Name }
Write-Output ('Authentication identity: ' + $identity + ' (no password saved)')
foreach ($namespace in @('root\cimv2', 'root\Microsoft\Windows\Smb')) {
    $timer = [Diagnostics.Stopwatch]::StartNew()
    try {
        $options = [System.Management.ConnectionOptions]::new()
        $options.Authentication = [System.Management.AuthenticationLevel]::PacketPrivacy
        $options.Impersonation = [System.Management.ImpersonationLevel]::Impersonate
        $options.Timeout = [TimeSpan]::FromSeconds(15)
        if ($Credential) { $options.Username = $Credential.UserName; $options.SecurePassword = $Credential.Password }
        $scope = [System.Management.ManagementScope]::new("\\$Server\$namespace", $options)
        $scope.Connect()
        Write-Output ('WMI {0}: CONNECTED ({1} ms)' -f $namespace, $timer.ElapsedMilliseconds)
        $queryText = if ($namespace -eq 'root\cimv2') { 'SELECT Name,Version FROM Win32_OperatingSystem' } else { 'SELECT * FROM meta_class WHERE __CLASS="MSFT_SmbSession" OR __CLASS="MSFT_SmbOpenFile"' }
        $search = [System.Management.ManagementObjectSearcher]::new($scope, [System.Management.ObjectQuery]::new($queryText))
        try {
            $search.Options.Timeout = [TimeSpan]::FromSeconds(15)
            $results = $search.Get()
            try { Write-Output ('Read-only query: {0} result(s)' -f $results.Count) }
            finally { $results.Dispose() }
        } finally { $search.Dispose() }
    } catch {
        $errorObject = $_.Exception.GetBaseException()
        Write-Output ('WMI {0}: FAILED 0x{1:X8} ({2} ms): {3}' -f $namespace, $errorObject.HResult, $timer.ElapsedMilliseconds, $errorObject.Message)
    }
}
