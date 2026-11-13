[CmdletBinding()]
param(
    [ValidateSet('Backup', 'Verify', 'Status', 'ExportRecoveryKey', 'InstallSchedule')]
    [string]$Action = 'Status',
    [string]$BackupDirectory = (Join-Path $env:USERPROFILE '.codex/darim-backups'),
    [string]$BackupPath,
    [string]$Destination,
    [ValidatePattern('^[a-z0-9]{24}$')][string]$ServiceUuid = '6o4qlqy50orrzxavuw0c7rp5',
    [ValidateRange(1, 3650)][int]$KeepDays = 30
)

$ErrorActionPreference = 'Stop'
Import-Module (Join-Path $PSScriptRoot 'BackupWorkflow.psm1') -Force
Import-Module (Join-Path $PSScriptRoot 'ArchiveCrypto.psm1') -Force

function Test-SecureStringEqual([System.Security.SecureString]$Left, [System.Security.SecureString]$Right) {
    if ($Left.Length -ne $Right.Length) { return $false }
    $leftPointer = [IntPtr]::Zero
    $rightPointer = [IntPtr]::Zero
    $leftChars = [char[]]::new($Left.Length)
    $rightChars = [char[]]::new($Right.Length)
    try {
        $leftPointer = [Runtime.InteropServices.Marshal]::SecureStringToGlobalAllocUnicode($Left)
        $rightPointer = [Runtime.InteropServices.Marshal]::SecureStringToGlobalAllocUnicode($Right)
        [Runtime.InteropServices.Marshal]::Copy($leftPointer, $leftChars, 0, $leftChars.Length)
        [Runtime.InteropServices.Marshal]::Copy($rightPointer, $rightChars, 0, $rightChars.Length)
        $different = 0
        for ($i = 0; $i -lt $leftChars.Length; $i++) { $different = $different -bor ($leftChars[$i] -bxor $rightChars[$i]) }
        return ($different -eq 0)
    } finally {
        [Array]::Clear($leftChars, 0, $leftChars.Length)
        [Array]::Clear($rightChars, 0, $rightChars.Length)
        if ($leftPointer -ne [IntPtr]::Zero) { [Runtime.InteropServices.Marshal]::ZeroFreeGlobalAllocUnicode($leftPointer) }
        if ($rightPointer -ne [IntPtr]::Zero) { [Runtime.InteropServices.Marshal]::ZeroFreeGlobalAllocUnicode($rightPointer) }
    }
}

$backupRoot = [System.IO.Path]::GetFullPath($BackupDirectory)
$statusPath = Join-Path $backupRoot 'status.json'

switch ($Action) {
    'Backup' {
        Set-DarimBackupDirectoryAcl -Directory $backupRoot
        $ssh = (Get-Command ssh.exe -ErrorAction Stop).Source
        $remoteScript = [System.IO.File]::ReadAllText((Join-Path $PSScriptRoot 'remote-export.sh'))
        $stamp = [DateTime]::UtcNow.ToString('yyyyMMddTHHmmssZ')
        $archivePath = Join-Path $backupRoot ('darim-' + $stamp + '-' + [guid]::NewGuid().ToString('N') + '.darim')
        $startInfo = New-DarimBackupSshProcessStartInfo -SshPath $ssh -ServiceUuid $ServiceUuid
        $archive = Invoke-EncryptedBackupProcess -ProcessStartInfo $startInfo -ArchivePath $archivePath `
            -StatusPath $statusPath -RemoteScript $remoteScript -RetentionDays $KeepDays
        Write-Output ('Encrypted backup created: {0} ({1:N0} bytes).' -f $archive.Name, $archive.Length)
    }
    'Status' {
        Write-Output (Get-DarimBackupStatus -StatusPath $statusPath)
    }
    'Verify' {
        if (-not $BackupPath) { throw 'Specify -BackupPath.' }
        if (Test-DarimArchive -Path $BackupPath) { Write-Output 'Backup archive authentication passed.' }
        else { throw 'Backup archive authentication failed.' }
    }
    'ExportRecoveryKey' {
        if (-not $BackupPath -or -not $Destination) { throw 'Specify -BackupPath and -Destination.' }
        Set-DarimBackupDirectoryAcl -Directory ([System.IO.Path]::GetDirectoryName([System.IO.Path]::GetFullPath($Destination)))
        $first = Read-Host 'Recovery password (12 or more characters)' -AsSecureString
        $second = Read-Host 'Repeat recovery password' -AsSecureString
        try {
            if (-not (Test-SecureStringEqual $first $second)) { throw 'Recovery passwords did not match.' }
            Export-DarimRecoveryKey -ArchivePath $BackupPath -Destination $Destination -Password $first
            Write-Output 'Encrypted recovery key exported.'
        } finally {
            $first.Dispose()
            $second.Dispose()
        }
    }
    'InstallSchedule' {
        Set-DarimBackupDirectoryAcl -Directory $backupRoot
        $scriptPath = Join-Path $PSScriptRoot 'darim-backup.ps1'
        $hostExe = (Get-Process -Id $PID).Path
        $definition = New-DarimBackupScheduleDefinition -ScriptPath $scriptPath -BackupDirectory $backupRoot `
            -ServiceUuid $ServiceUuid -KeepDays $KeepDays -ExecutablePath $hostExe `
            -UserId ([System.Security.Principal.WindowsIdentity]::GetCurrent().Name)
        $taskAction = New-ScheduledTaskAction -Execute $definition.Action.Execute -Argument $definition.Action.Argument
        $triggers = @(
            (New-ScheduledTaskTrigger -AtLogOn -User $definition.Principal.UserId),
            (New-ScheduledTaskTrigger -Daily -At 1:00AM)
        )
        $principal = New-ScheduledTaskPrincipal -UserId $definition.Principal.UserId `
            -LogonType $definition.Principal.LogonType -RunLevel $definition.Principal.RunLevel
        $settings = New-ScheduledTaskSettingsSet -StartWhenAvailable `
            -MultipleInstances $definition.Settings.MultipleInstances -ExecutionTimeLimit $definition.Settings.ExecutionTimeLimit
        Register-ScheduledTask -TaskName $definition.TaskName -Description $definition.Description `
            -Action $taskAction -Trigger $triggers -Principal $principal -Settings $settings -Force | Out-Null
        Write-Output 'Backup schedule installed for this interactive Windows user.'
    }
}
