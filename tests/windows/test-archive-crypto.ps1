$ErrorActionPreference = 'Stop'
$module = Join-Path $PSScriptRoot '../../ops/windows-backup/ArchiveCrypto.psm1'
Import-Module $module -Force

function Assert-That([bool]$condition, [string]$message) {
    if (-not $condition) { throw "ASSERTION FAILED: $message" }
}

function Assert-Throws([scriptblock]$action, [string]$message) {
    try { & $action } catch { return }
    throw "ASSERTION FAILED: expected failure: $message"
}

function Assert-ByteArraysEqual([byte[]]$expected, [byte[]]$actual, [string]$message) {
    if ($expected.Length -ne $actual.Length) { throw "ASSERTION FAILED: $message (length)" }
    for ($i = 0; $i -lt $expected.Length; $i++) {
        if ($expected[$i] -ne $actual[$i]) { throw "ASSERTION FAILED: $message (byte $i)" }
    }
}

function Contains-Bytes([byte[]]$source, [byte[]]$needle) {
    for ($i = 0; $i -le $source.Length - $needle.Length; $i++) {
        $found = $true
        for ($j = 0; $j -lt $needle.Length; $j++) {
            if ($source[$i + $j] -ne $needle[$j]) { $found = $false; break }
        }
        if ($found) { return $true }
    }
    return $false
}

$directory = Join-Path ([System.IO.Path]::GetTempPath()) ('darim-crypto-test-' + [guid]::NewGuid().ToString('N'))
[void][System.IO.Directory]::CreateDirectory($directory)
try {
    $marker = [System.Text.Encoding]::UTF8.GetBytes('SYNTHETIC-PLAINTEXT-MUST-NOT-APPEAR-IN-ARCHIVE')
    $payload = New-Object byte[] (2MB + $marker.Length)
    $rng = [System.Security.Cryptography.RandomNumberGenerator]::Create()
    try { $rng.GetBytes($payload) } finally { $rng.Dispose() }
    [System.Buffer]::BlockCopy($marker, 0, $payload, $payload.Length - $marker.Length, $marker.Length)

    $archive = Join-Path $directory 'fixture.darim'
    $sourceStream = [System.IO.MemoryStream]::new($payload, $false)
    Protect-DarimArchive -SourceStream $sourceStream -Path $archive
    $sourceStream.Dispose()
    Assert-That (Test-DarimArchive -Path $archive) 'fresh archive did not verify'

    $encrypted = [System.IO.File]::ReadAllBytes($archive)
    Assert-That (-not (Contains-Bytes $encrypted $marker)) 'plaintext marker found in archive bytes'
    Assert-That (-not (Test-Path ($archive + '.partial'))) 'partial file remained after publication'

    $restored = [System.IO.MemoryStream]::new()
    Unprotect-DarimArchive -Path $archive -Output $restored
    Assert-ByteArraysEqual $payload $restored.ToArray() 'multi-chunk round trip changed bytes'
    $restored.Dispose()

    $tamperedPath = Join-Path $directory 'tampered.darim'
    $tampered = [System.IO.File]::ReadAllBytes($archive)
    $tampered[$tampered.Length - 33] = $tampered[$tampered.Length - 33] -bxor 0x20
    [System.IO.File]::WriteAllBytes($tamperedPath, $tampered)
    Assert-That (-not (Test-DarimArchive -Path $tamperedPath)) 'modified ciphertext passed authentication'
    $empty = [System.IO.MemoryStream]::new()
    Assert-Throws { Unprotect-DarimArchive -Path $tamperedPath -Output $empty } 'tampered archive was decrypted'
    Assert-That ($empty.Length -eq 0) 'tampered archive wrote plaintext before authentication'
    $empty.Dispose()

    $truncatedPath = Join-Path $directory 'truncated.darim'
    [System.IO.File]::WriteAllBytes($truncatedPath, $encrypted[0..($encrypted.Length - 12)])
    Assert-That (-not (Test-DarimArchive -Path $truncatedPath)) 'truncated archive passed authentication'

    $password = ConvertTo-SecureString 'synthetic recovery password' -AsPlainText -Force
    $recoveryPath = Join-Path $directory 'fixture.recovery'
    Export-DarimRecoveryKey -ArchivePath $archive -Destination $recoveryPath -Password $password
    $portableKey = Import-DarimRecoveryKey -Path $recoveryPath -Password $password
    $portableOutput = [System.IO.MemoryStream]::new()
    Unprotect-DarimArchive -Path $archive -Output $portableOutput -KeyBundle $portableKey
    Assert-ByteArraysEqual $payload $portableOutput.ToArray() 'portable key did not restore archive'
    $portableOutput.Dispose()
    $wrongPassword = ConvertTo-SecureString 'wrong synthetic password' -AsPlainText -Force
    Assert-Throws { Import-DarimRecoveryKey -Path $recoveryPath -Password $wrongPassword } 'wrong recovery password was accepted'

    $oversizedPath = Join-Path $directory 'oversized.darim'
    $oversized = New-Object byte[] (32MB + 1)
    $oversizedStream = [System.IO.MemoryStream]::new($oversized, $false)
    try {
        Protect-DarimArchive -SourceStream $oversizedStream -Path $oversizedPath
        throw 'ASSERTION FAILED: payload above 32 MiB was accepted'
    } catch {
        if ($_.Exception.Message -notmatch '32 MiB') { throw }
    } finally { $oversizedStream.Dispose() }
    Assert-That (-not (Test-Path $oversizedPath)) 'oversized payload left an archive'
    Assert-That (-not (Test-Path ($oversizedPath + '.partial'))) 'oversized payload left a partial file'

    Write-Output 'Archive crypto synthetic tests passed.'
} finally {
    Remove-Item -LiteralPath $directory -Recurse -Force
}
