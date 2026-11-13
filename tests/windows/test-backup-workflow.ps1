$ErrorActionPreference = 'Stop'
$workflow = Join-Path $PSScriptRoot '../../ops/windows-backup/BackupWorkflow.psm1'
Import-Module $workflow -Force
Import-Module (Join-Path $PSScriptRoot '../../ops/windows-backup/ArchiveCrypto.psm1') -Force

function Assert-That([bool]$condition, [string]$message) {
    if (-not $condition) { throw "ASSERTION FAILED: $message" }
}

function Get-QuotedArgument([string]$value) {
    return '"' + $value.Replace('"', '\"') + '"'
}

function New-FixtureProcessInfo([string]$tarPath, [int]$exitCode) {
    $pwsh = (Get-Command pwsh.exe -ErrorAction Stop).Source
    $fixture = Join-Path $PSScriptRoot 'fixtures/emit-tar.ps1'
    $info = [System.Diagnostics.ProcessStartInfo]::new()
    $info.FileName = $pwsh
    $info.Arguments = '-NoProfile -ExecutionPolicy Bypass -File ' + (Get-QuotedArgument $fixture) +
        ' -TarPath ' + (Get-QuotedArgument $tarPath) + ' -ExitCode ' + $exitCode
    return $info
}

$directory = Join-Path ([System.IO.Path]::GetTempPath()) ('darim-backup-workflow-' + [guid]::NewGuid().ToString('N'))
[void][System.IO.Directory]::CreateDirectory($directory)
try {
    $fixtureSource = Join-Path $directory 'fixture-source'
    [void][System.IO.Directory]::CreateDirectory($fixtureSource)
    $marker = 'SYNTHETIC-TAR-DATA-ONLY'
    [System.IO.File]::WriteAllText((Join-Path $fixtureSource 'payload.txt'), $marker)
    $tarPath = Join-Path $directory 'fixture.tar'
    & tar.exe -cf $tarPath -C $fixtureSource .
    if ($LASTEXITCODE -ne 0) { throw 'Could not create synthetic tar fixture.' }

    $backupDir = Join-Path $directory 'backups'
    [void][System.IO.Directory]::CreateDirectory($backupDir)
    $archivePath = Join-Path $backupDir 'darim-fixture.darim'
    $statusPath = Join-Path $backupDir 'status.json'
    $info = New-FixtureProcessInfo $tarPath 0
    $null = Invoke-EncryptedBackupProcess -ProcessStartInfo $info -ArchivePath $archivePath -StatusPath $statusPath -RemoteScript '# synthetic fixture'

    Assert-That (Test-DarimArchive -Path $archivePath) 'fixture tar archive did not authenticate'
    $encrypted = [System.IO.File]::ReadAllBytes($archivePath)
    $markerBytes = [System.Text.Encoding]::UTF8.GetBytes($marker)
    for ($i = 0; $i -le $encrypted.Length - $markerBytes.Length; $i++) {
        $same = $true
        for ($j = 0; $j -lt $markerBytes.Length; $j++) {
            if ($encrypted[$i + $j] -ne $markerBytes[$j]) { $same = $false; break }
        }
        if ($same) { throw 'ASSERTION FAILED: synthetic plaintext appeared in encrypted archive' }
    }
    Assert-That (Test-Path $statusPath) 'successful source did not update status'
    $status = Get-Content -Raw $statusPath | ConvertFrom-Json
    Assert-That ($status.archive -eq 'darim-fixture.darim') 'status contains wrong archive name'
    Assert-That (-not (Test-Path ($archivePath + '.partial'))) 'partial archive was left behind'

    $failedArchive = Join-Path $backupDir 'darim-failed.darim'
    $oldStatus = [System.IO.File]::ReadAllText($statusPath)
    $failedInfo = New-FixtureProcessInfo $tarPath 9
    try {
        $null = Invoke-EncryptedBackupProcess -ProcessStartInfo $failedInfo -ArchivePath $failedArchive -StatusPath $statusPath -RemoteScript '# synthetic failure'
        throw 'ASSERTION FAILED: failing source process was accepted'
    } catch {
        if ($_.Exception.Message -match 'ASSERTION FAILED') { throw }
    }
    Assert-That (-not (Test-Path $failedArchive)) 'failed source published an archive'
    Assert-That (-not (Test-Path ($failedArchive + '.partial'))) 'failed source left a partial archive'
    Assert-That ([System.IO.File]::ReadAllText($statusPath) -ceq $oldStatus) 'failed source changed success status'

    $oldArchive = Join-Path $backupDir 'darim-old.darim'
    Copy-Item -LiteralPath $archivePath -Destination $oldArchive
    [System.IO.File]::SetLastWriteTimeUtc($oldArchive, [DateTime]::UtcNow.AddDays(-30))
    $untrusted = Join-Path $backupDir 'darim-old-invalid.darim'
    [System.IO.File]::WriteAllText($untrusted, 'do not remove')
    Remove-DarimBackupRetention -Directory $backupDir -KeepDays 14 -NewestPath $archivePath
    Assert-That (-not (Test-Path $oldArchive)) 'retention kept an expired authenticated archive'
    Assert-That (Test-Path $untrusted) 'retention removed an unauthenticated file'

    $schedule = New-DarimBackupScheduleDefinition -ScriptPath 'C:\synthetic\darim-backup.ps1' `
        -BackupDirectory 'C:\synthetic\backups' -KeepDays 17 -ExecutablePath 'C:\Windows\pwsh.exe' `
        -UserId 'SYNTHETIC\fixture-user' -ServiceUuid '6o4qlqy50orrzxavuw0c7rp5'
    Assert-That ($schedule.TaskName -eq 'Darim Windows Backup') 'schedule has unexpected task name'
    Assert-That ($schedule.Settings.StartWhenAvailable) 'schedule does not catch up when Windows becomes available'
    Assert-That ($schedule.Settings.MultipleInstances -eq 'IgnoreNew') 'schedule allows overlapping runs'
    Assert-That ($schedule.Principal.LogonType -eq 'Interactive') 'schedule can run without interactive user session'
    Assert-That ($schedule.Triggers.Count -eq 2) 'schedule needs logon and daily triggers'
    Assert-That ($schedule.Action.Execute -eq 'C:\Windows\pwsh.exe') 'schedule uses unexpected executable'
    Assert-That ($schedule.Action.Argument -match '-KeepDays 17') 'schedule dropped retention setting'
    Assert-That ($schedule.Action.Argument -match '-ServiceUuid 6o4qlqy50orrzxavuw0c7rp5') 'schedule dropped service UUID'

    $sshStart = New-DarimBackupSshProcessStartInfo -SshPath 'C:\Windows\System32\OpenSSH\ssh.exe' `
        -ServiceUuid '6o4qlqy50orrzxavuw0c7rp5'
    Assert-That ($sshStart.Arguments -match '-oBatchMode=yes') 'SSH could prompt for interactive credentials'
    Assert-That ($sshStart.Arguments -match '-oConnectTimeout=10') 'SSH connection timeout is not bounded'
    Assert-That ($sshStart.Arguments -match '-oServerAliveInterval=15') 'SSH keepalive interval is missing'
    Assert-That ($sshStart.Arguments -match '-oServerAliveCountMax=3') 'SSH keepalive failure count is missing'

    Write-Output 'Backup workflow synthetic tests passed.'
} finally {
    Remove-Item -LiteralPath $directory -Recurse -Force
}
