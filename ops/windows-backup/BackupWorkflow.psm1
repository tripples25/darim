$ErrorActionPreference = 'Stop'
Import-Module (Join-Path $PSScriptRoot 'ArchiveCrypto.psm1') -Force

if (-not ('DarimBackupErrorSink' -as [type])) {
    Add-Type -TypeDefinition @'
using System;
using System.Diagnostics;
public sealed class DarimBackupErrorSink {
    public void OnDataReceived(object sender, DataReceivedEventArgs args) { }
}
'@
}

function Set-DarimBackupDirectoryAcl {
    [CmdletBinding()]
    param([Parameter(Mandatory)][string]$Directory)

    $fullPath = [System.IO.Path]::GetFullPath($Directory)
    if (-not [System.IO.Directory]::Exists($fullPath)) {
        [void][System.IO.Directory]::CreateDirectory($fullPath)
    }
    $acl = Get-Acl -LiteralPath $fullPath
    $acl.SetAccessRuleProtection($true, $false)
    $sid = [System.Security.Principal.WindowsIdentity]::GetCurrent().User
    $rule = [System.Security.AccessControl.FileSystemAccessRule]::new(
        $sid, [System.Security.AccessControl.FileSystemRights]::FullControl,
        [System.Security.AccessControl.InheritanceFlags]::ContainerInherit -bor
        [System.Security.AccessControl.InheritanceFlags]::ObjectInherit,
        [System.Security.AccessControl.PropagationFlags]::None,
        [System.Security.AccessControl.AccessControlType]::Allow)
    [void]$acl.AddAccessRule($rule)
    Set-Acl -LiteralPath $fullPath -AclObject $acl
}

function Write-DarimBackupStatus {
    param([Parameter(Mandatory)][string]$StatusPath, [Parameter(Mandatory)][string]$ArchivePath)
    $file = [System.IO.FileInfo]::new([System.IO.Path]::GetFullPath($ArchivePath))
    $status = [ordered]@{
        lastSuccessUtc = [DateTime]::UtcNow.ToString('o')
        archive = $file.Name
        bytes = $file.Length
    }
    $json = $status | ConvertTo-Json -Compress
    $fullStatus = [System.IO.Path]::GetFullPath($StatusPath)
    $partial = $fullStatus + '.partial'
    if ([System.IO.File]::Exists($partial)) { throw 'A status update is already in progress.' }
    try {
        [System.IO.File]::WriteAllText($partial, $json, [System.Text.UTF8Encoding]::new($false))
        if ([System.IO.File]::Exists($fullStatus)) {
            [System.IO.File]::Replace($partial, $fullStatus, $null)
        } else {
            [System.IO.File]::Move($partial, $fullStatus)
        }
    } finally {
        if ([System.IO.File]::Exists($partial)) { [System.IO.File]::Delete($partial) }
    }
}

function Remove-DarimBackupRetention {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)][string]$Directory,
        [ValidateRange(1, 3650)][int]$KeepDays = 30,
        [string]$NewestPath
    )
    $fullDirectory = [System.IO.Path]::GetFullPath($Directory)
    $cutoff = [DateTime]::UtcNow.AddDays(-$KeepDays)
    $newest = if ($NewestPath) { [System.IO.Path]::GetFullPath($NewestPath) } else { $null }
    foreach ($file in [System.IO.Directory]::EnumerateFiles($fullDirectory, 'darim-*.darim')) {
        $fullPath = [System.IO.Path]::GetFullPath($file)
        if ($newest -and [string]::Equals($fullPath, $newest, [StringComparison]::OrdinalIgnoreCase)) { continue }
        $info = [System.IO.FileInfo]::new($fullPath)
        if ($info.LastWriteTimeUtc -ge $cutoff -or ($info.Attributes -band [System.IO.FileAttributes]::ReparsePoint)) { continue }
        if (Test-DarimArchive -Path $fullPath) { [System.IO.File]::Delete($fullPath) }
    }
}

function Invoke-EncryptedBackupProcess {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)][System.Diagnostics.ProcessStartInfo]$ProcessStartInfo,
        [Parameter(Mandatory)][string]$ArchivePath,
        [Parameter(Mandatory)][string]$StatusPath,
        [Parameter(Mandatory)][string]$RemoteScript,
        [ValidateRange(1, 3650)][int]$RetentionDays = 30
    )
    $finalPath = [System.IO.Path]::GetFullPath($ArchivePath)
    $directory = [System.IO.Path]::GetDirectoryName($finalPath)
    if (-not [System.IO.Directory]::Exists($directory)) { throw 'Backup directory does not exist.' }
    if ([System.IO.File]::Exists($finalPath)) { throw 'Backup destination already exists.' }
    $pendingPath = $finalPath + '.' + [guid]::NewGuid().ToString('N') + '.pending'
    $ProcessStartInfo.UseShellExecute = $false
    $ProcessStartInfo.CreateNoWindow = $true
    $ProcessStartInfo.RedirectStandardInput = $true
    $ProcessStartInfo.RedirectStandardOutput = $true
    $ProcessStartInfo.RedirectStandardError = $true
    $process = [System.Diagnostics.Process]::new()
    $process.StartInfo = $ProcessStartInfo
    $archivePublished = $false
    try {
        if (-not $process.Start()) { throw 'Could not start the backup source process.' }
        # Drain and discard stderr so it cannot block the binary stdout pipe or reveal source details.
        $errorSink = [DarimBackupErrorSink]::new()
        $handlerMethod = [DarimBackupErrorSink].GetMethod('OnDataReceived')
        $errorHandler = [System.Delegate]::CreateDelegate([System.Diagnostics.DataReceivedEventHandler], $errorSink, $handlerMethod)
        $process.add_ErrorDataReceived($errorHandler)
        $process.BeginErrorReadLine()
        $process.StandardInput.Write($RemoteScript)
        $process.StandardInput.Close()

        $encryptedLength = Protect-DarimArchive -SourceStream $process.StandardOutput.BaseStream -Path $pendingPath
        $process.WaitForExit()
        if ($process.ExitCode -ne 0) { throw "Backup source failed with exit code $($process.ExitCode)." }
        if ($encryptedLength -le 0 -or [System.IO.File]::Exists($finalPath)) { throw 'Backup source produced no usable archive.' }
        [System.IO.File]::Move($pendingPath, $finalPath)
        $archivePublished = $true
        Write-DarimBackupStatus -StatusPath $StatusPath -ArchivePath $finalPath
        Remove-DarimBackupRetention -Directory $directory -KeepDays $RetentionDays -NewestPath $finalPath
        return [System.IO.FileInfo]::new($finalPath)
    } catch {
        if ($process -and -not $process.HasExited) {
            try { $process.Kill(); $process.WaitForExit() } catch { }
        }
        if ($archivePublished) {
            # Keep the authenticated backup if only status/retention metadata failed.
            throw
        }
        throw
    } finally {
        if ([System.IO.File]::Exists($pendingPath)) { [System.IO.File]::Delete($pendingPath) }
        if ([System.IO.File]::Exists($pendingPath + '.partial')) { [System.IO.File]::Delete($pendingPath + '.partial') }
        $process.Dispose()
    }
}

function Get-DarimBackupStatus {
    [CmdletBinding()]
    param([Parameter(Mandatory)][string]$StatusPath)
    if (-not [System.IO.File]::Exists($StatusPath)) { return 'No successful backup is recorded.' }
    $status = Get-Content -LiteralPath $StatusPath -Raw | ConvertFrom-Json
    $timestamp = [DateTime]::Parse($status.lastSuccessUtc).ToUniversalTime()
    $age = [DateTime]::UtcNow - $timestamp
    return ('Last successful backup: {0:u} UTC ({1:N1} hours ago).' -f $timestamp, $age.TotalHours)
}

function ConvertTo-DarimWindowsArgument([string]$Value) {
    return '"' + $Value.Replace('"', '\"') + '"'
}

function New-DarimBackupScheduleDefinition {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)][string]$ScriptPath,
        [Parameter(Mandatory)][string]$BackupDirectory,
        [Parameter(Mandatory)][string]$ExecutablePath,
        [Parameter(Mandatory)][string]$UserId,
        [ValidatePattern('^[a-z0-9]{24}$')][string]$ServiceUuid = '6o4qlqy50orrzxavuw0c7rp5',
        [ValidateRange(1, 3650)][int]$KeepDays = 30
    )
    $scriptFullPath = [System.IO.Path]::GetFullPath($ScriptPath)
    $backupFullPath = [System.IO.Path]::GetFullPath($BackupDirectory)
    $arguments = '-NoProfile -ExecutionPolicy Bypass -File {0} -Action Backup -BackupDirectory {1} -ServiceUuid {2} -KeepDays {3}' -f `
        (ConvertTo-DarimWindowsArgument $scriptFullPath), (ConvertTo-DarimWindowsArgument $backupFullPath), $ServiceUuid, $KeepDays
    return [pscustomobject]@{
        TaskName = 'Darim Windows Backup'
        Description = 'Best-effort encrypted Darim backup when this Windows user is available.'
        Action = [pscustomobject]@{ Execute = $ExecutablePath; Argument = $arguments }
        Triggers = @('AtLogOn', 'Daily')
        Principal = [pscustomobject]@{ UserId = $UserId; LogonType = 'Interactive'; RunLevel = 'Limited' }
        Settings = [pscustomobject]@{
            StartWhenAvailable = $true
            MultipleInstances = 'IgnoreNew'
            ExecutionTimeLimit = [TimeSpan]::FromHours(2)
        }
    }
}

Export-ModuleMember -Function Set-DarimBackupDirectoryAcl, Invoke-EncryptedBackupProcess,
    Remove-DarimBackupRetention, Get-DarimBackupStatus, Write-DarimBackupStatus,
    New-DarimBackupScheduleDefinition
