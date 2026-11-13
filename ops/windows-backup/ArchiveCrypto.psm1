$script:ArchiveMagic = [System.Text.Encoding]::ASCII.GetBytes('DARIMBAK')
$script:RecoveryMagic = [System.Text.Encoding]::ASCII.GetBytes('DARIMKEY')
$script:ArchiveEntropy = [System.Text.Encoding]::UTF8.GetBytes('Darim Windows backup key v1')
$script:MaximumPayloadBytes = 32MB
$script:RecoveryIterations = 600000

Add-Type -AssemblyName System.Security

function New-RandomBytes([int]$Length) {
    $bytes = [byte[]]::new($Length)
    $random = [System.Security.Cryptography.RandomNumberGenerator]::Create()
    try { $random.GetBytes($bytes) } finally { $random.Dispose() }
    return ,$bytes
}

function Get-PasswordBytes([System.Security.SecureString]$Password) {
    if ($null -eq $Password -or $Password.Length -lt 12) {
        throw 'Recovery password must contain at least 12 characters.'
    }
    $pointer = [System.Runtime.InteropServices.Marshal]::SecureStringToGlobalAllocUnicode($Password)
    $characters = [char[]]::new($Password.Length)
    try {
        [System.Runtime.InteropServices.Marshal]::Copy($pointer, $characters, 0, $characters.Length)
        return ,([System.Text.Encoding]::UTF8.GetBytes($characters))
    } finally {
        [System.Array]::Clear($characters, 0, $characters.Length)
        [System.Runtime.InteropServices.Marshal]::ZeroFreeGlobalAllocUnicode($pointer)
    }
}

function Get-Pbkdf2Bytes([byte[]]$PasswordBytes, [byte[]]$Salt, [int]$Iterations) {
    $deriver = [System.Security.Cryptography.Rfc2898DeriveBytes]::new(
        $PasswordBytes, $Salt, $Iterations, [System.Security.Cryptography.HashAlgorithmName]::SHA256)
    try { return ,$deriver.GetBytes(64) } finally { $deriver.Dispose() }
}

function Get-AesCipher([byte[]]$InputBytes, [byte[]]$Key, [byte[]]$Iv, [bool]$Decrypt) {
    $aes = [System.Security.Cryptography.Aes]::Create()
    $transform = $null
    try {
        $aes.Mode = [System.Security.Cryptography.CipherMode]::CBC
        $aes.Padding = [System.Security.Cryptography.PaddingMode]::PKCS7
        $aes.Key = $Key
        $aes.IV = $Iv
        if ($Decrypt) { $transform = $aes.CreateDecryptor() } else { $transform = $aes.CreateEncryptor() }
        return ,$transform.TransformFinalBlock($InputBytes, 0, $InputBytes.Length)
    } finally {
        if ($null -ne $transform) { $transform.Dispose() }
        $aes.Dispose()
    }
}

function Get-AuthenticatedBytes([byte[]]$Header, [byte[]]$Ciphertext, [byte[]]$Key) {
    $hmac = [System.Security.Cryptography.HMACSHA256]::new($Key)
    $empty = [byte[]]::new(0)
    try {
        [void]$hmac.TransformBlock($Header, 0, $Header.Length, $Header, 0)
        [void]$hmac.TransformBlock($Ciphertext, 0, $Ciphertext.Length, $Ciphertext, 0)
        [void]$hmac.TransformFinalBlock($empty, 0, 0)
        return ,$hmac.Hash
    } finally { $hmac.Dispose() }
}

function Test-EqualBytes([byte[]]$Left, [byte[]]$Right) {
    if ($Left.Length -ne $Right.Length) { return $false }
    $difference = 0
    for ($i = 0; $i -lt $Left.Length; $i++) { $difference = $difference -bor ($Left[$i] -bxor $Right[$i]) }
    return ($difference -eq 0)
}

function Write-AtomicCiphertext([string]$Path, [byte[]]$Bytes) {
    $fullPath = [System.IO.Path]::GetFullPath($Path)
    $directory = [System.IO.Path]::GetDirectoryName($fullPath)
    if (-not [System.IO.Directory]::Exists($directory)) { throw 'Backup directory does not exist.' }
    if ([System.IO.File]::Exists($fullPath)) { throw 'Backup destination already exists.' }
    $partial = $fullPath + '.partial'
    if ([System.IO.File]::Exists($partial)) { throw 'A partial backup already exists at the destination.' }
    try {
        $file = [System.IO.FileStream]::new(
            $partial, [System.IO.FileMode]::CreateNew, [System.IO.FileAccess]::Write, [System.IO.FileShare]::None)
        try {
            $file.Write($Bytes, 0, $Bytes.Length)
            $file.Flush($true)
        } finally { $file.Dispose() }
        [System.IO.File]::Move($partial, $fullPath)
    } catch {
        if ([System.IO.File]::Exists($partial)) { [System.IO.File]::Delete($partial) }
        throw
    }
}

function Protect-DarimArchive {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)][System.IO.Stream]$SourceStream,
        [Parameter(Mandatory)][string]$Path
    )
    $memory = [System.IO.MemoryStream]::new()
    $buffer = [byte[]]::new(1MB)
    $payload = $null
    $bundle = $null
    $ciphertext = $null
    $archive = $null
    try {
        while (($read = $SourceStream.Read($buffer, 0, $buffer.Length)) -gt 0) {
            if (($memory.Length + $read) -gt $script:MaximumPayloadBytes) {
                throw 'Backup payload exceeds the 32 MiB limit; no archive was written.'
            }
            $memory.Write($buffer, 0, $read)
        }
        $payload = $memory.ToArray()
        $payloadLength = [int]$memory.Length
        $bundle = New-RandomBytes 64
        $aesKey = [byte[]]::new(32)
        $macKey = [byte[]]::new(32)
        [System.Buffer]::BlockCopy($bundle, 0, $aesKey, 0, 32)
        [System.Buffer]::BlockCopy($bundle, 32, $macKey, 0, 32)
        $wrapped = [System.Security.Cryptography.ProtectedData]::Protect(
            $bundle, $script:ArchiveEntropy, [System.Security.Cryptography.DataProtectionScope]::CurrentUser)
        $iv = New-RandomBytes 16
        $ciphertext = Get-AesCipher $payload $aesKey $iv $false

        $headerStream = [System.IO.MemoryStream]::new()
        $writer = [System.IO.BinaryWriter]::new($headerStream)
        try {
            $writer.Write($script:ArchiveMagic)
            $writer.Write([byte]1)
            $writer.Write([int]$wrapped.Length)
            $writer.Write($wrapped)
            $writer.Write($iv)
            $writer.Write([int]$ciphertext.Length)
            $writer.Flush()
            $header = $headerStream.ToArray()
        } finally {
            $writer.Dispose()
            $headerStream.Dispose()
        }
        $mac = Get-AuthenticatedBytes $header $ciphertext $macKey
        $archiveStream = [System.IO.MemoryStream]::new()
        try {
            $archiveStream.Write($header, 0, $header.Length)
            $archiveStream.Write($ciphertext, 0, $ciphertext.Length)
            $archiveStream.Write($mac, 0, $mac.Length)
            $archive = $archiveStream.ToArray()
        } finally { $archiveStream.Dispose() }
        Write-AtomicCiphertext $Path $archive
        return [System.IO.FileInfo]::new([System.IO.Path]::GetFullPath($Path)).Length
    } finally {
        $memory.Dispose()
        [System.Array]::Clear($buffer, 0, $buffer.Length)
        foreach ($secret in @($payload, $bundle, $aesKey, $macKey, $ciphertext, $archive)) {
            if ($null -ne $secret -and $secret -is [byte[]]) { [System.Array]::Clear($secret, 0, $secret.Length) }
        }
    }
}

function Read-DarimArchive([string]$Path) {
    $file = [System.IO.FileInfo]::new([System.IO.Path]::GetFullPath($Path))
    if (-not $file.Exists -or $file.Length -lt 80 -or $file.Length -gt ($script:MaximumPayloadBytes + 65536)) {
        throw 'Backup archive is missing or outside the supported size range.'
    }
    $bytes = [System.IO.File]::ReadAllBytes($file.FullName)
    $stream = [System.IO.MemoryStream]::new($bytes, $false)
    $reader = [System.IO.BinaryReader]::new($stream)
    try {
        $magic = $reader.ReadBytes(8)
        if (-not (Test-EqualBytes $magic $script:ArchiveMagic) -or $reader.ReadByte() -ne 1) {
            throw 'Backup archive format is unsupported.'
        }
        $wrappedLength = $reader.ReadInt32()
        if ($wrappedLength -lt 1 -or $wrappedLength -gt 8192) { throw 'Backup archive header is invalid.' }
        $wrapped = $reader.ReadBytes($wrappedLength)
        $iv = $reader.ReadBytes(16)
        $cipherLength = $reader.ReadInt32()
        if ($wrapped.Length -ne $wrappedLength -or $iv.Length -ne 16 -or
            $cipherLength -lt 16 -or $cipherLength -gt ($script:MaximumPayloadBytes + 16) -or
            ($cipherLength % 16) -ne 0) {
            throw 'Backup archive header is invalid.'
        }
        $headerLength = [int]$stream.Position
        if (($headerLength + $cipherLength + 32) -ne $bytes.Length) { throw 'Backup archive is truncated or has trailing data.' }
        $ciphertext = $reader.ReadBytes($cipherLength)
        $mac = $reader.ReadBytes(32)
        if ($ciphertext.Length -ne $cipherLength -or $mac.Length -ne 32) { throw 'Backup archive is truncated.' }
        $header = [byte[]]::new($headerLength)
        [System.Buffer]::BlockCopy($bytes, 0, $header, 0, $headerLength)
        return [pscustomobject]@{ WrappedKey = $wrapped; IV = $iv; Header = $header; Ciphertext = $ciphertext; Mac = $mac }
    } finally {
        $reader.Dispose()
        $stream.Dispose()
        [System.Array]::Clear($bytes, 0, $bytes.Length)
    }
}

function Get-DarimArchiveKey([string]$Path) {
    $parts = Read-DarimArchive $Path
    $bundle = [System.Security.Cryptography.ProtectedData]::Unprotect(
        $parts.WrappedKey, $script:ArchiveEntropy, [System.Security.Cryptography.DataProtectionScope]::CurrentUser)
    if ($bundle.Length -ne 64) {
        [System.Array]::Clear($bundle, 0, $bundle.Length)
        throw 'Backup archive key is invalid.'
    }
    return ,$bundle
}

function Test-DarimArchive {
    [CmdletBinding()]
    param([Parameter(Mandatory)][string]$Path, [byte[]]$KeyBundle)
    $computed = $null
    try {
        $parts = Read-DarimArchive $Path
        $bundle = $KeyBundle
        if ($null -eq $bundle) { $bundle = Get-DarimArchiveKey $Path }
        if ($bundle.Length -ne 64) { return $false }
        $macKey = [byte[]]::new(32)
        [System.Buffer]::BlockCopy($bundle, 32, $macKey, 0, 32)
        $computed = Get-AuthenticatedBytes $parts.Header $parts.Ciphertext $macKey
        return (Test-EqualBytes $computed $parts.Mac)
    } catch { return $false } finally {
        if ($null -ne $computed) { [System.Array]::Clear($computed, 0, $computed.Length) }
        if ($null -ne $macKey) { [System.Array]::Clear($macKey, 0, $macKey.Length) }
    }
}

function Unprotect-DarimArchive {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)][string]$Path,
        [Parameter(Mandatory)][System.IO.Stream]$Output,
        [byte[]]$KeyBundle
    )
    $parts = Read-DarimArchive $Path
    $ownedBundle = $false
    $plaintext = $null
    if ($null -eq $KeyBundle) { $KeyBundle = Get-DarimArchiveKey $Path; $ownedBundle = $true }
    $macKey = [byte[]]::new(32)
    $aesKey = [byte[]]::new(32)
    $expectedMac = $null
    try {
        if ($KeyBundle.Length -ne 64) { throw 'Backup key is invalid.' }
        [System.Buffer]::BlockCopy($KeyBundle, 0, $aesKey, 0, 32)
        [System.Buffer]::BlockCopy($KeyBundle, 32, $macKey, 0, 32)
        $expectedMac = Get-AuthenticatedBytes $parts.Header $parts.Ciphertext $macKey
        if (-not (Test-EqualBytes $expectedMac $parts.Mac)) { throw 'Backup archive authentication failed.' }
        $plaintext = Get-AesCipher $parts.Ciphertext $aesKey $parts.IV $true
        if ($plaintext.Length -gt $script:MaximumPayloadBytes) { throw 'Backup payload exceeds the 32 MiB limit.' }
        $Output.Write($plaintext, 0, $plaintext.Length)
    } finally {
        if ($ownedBundle) { [System.Array]::Clear($KeyBundle, 0, $KeyBundle.Length) }
        foreach ($secret in @($plaintext, $aesKey, $macKey, $expectedMac, $parts.Ciphertext)) {
            if ($null -ne $secret -and $secret -is [byte[]]) { [System.Array]::Clear($secret, 0, $secret.Length) }
        }
    }
}

function Export-DarimRecoveryKey {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)][string]$ArchivePath,
        [Parameter(Mandatory)][string]$Destination,
        [Parameter(Mandatory)][System.Security.SecureString]$Password
    )
    if (-not (Test-DarimArchive $ArchivePath)) { throw 'Backup archive did not pass authentication.' }
    $bundle = Get-DarimArchiveKey $ArchivePath
    $passwordBytes = Get-PasswordBytes $Password
    $derived = $null
    $ciphertext = $null
    $recovery = $null
    try {
        $salt = New-RandomBytes 16
        $iv = New-RandomBytes 16
        $derived = Get-Pbkdf2Bytes $passwordBytes $salt $script:RecoveryIterations
        $aesKey = [byte[]]::new(32)
        $macKey = [byte[]]::new(32)
        [System.Buffer]::BlockCopy($derived, 0, $aesKey, 0, 32)
        [System.Buffer]::BlockCopy($derived, 32, $macKey, 0, 32)
        $ciphertext = Get-AesCipher $bundle $aesKey $iv $false
        $headerStream = [System.IO.MemoryStream]::new()
        $writer = [System.IO.BinaryWriter]::new($headerStream)
        try {
            $writer.Write($script:RecoveryMagic)
            $writer.Write([byte]1)
            $writer.Write([int]$script:RecoveryIterations)
            $writer.Write($salt)
            $writer.Write($iv)
            $writer.Write([int]$ciphertext.Length)
            $writer.Flush()
            $header = $headerStream.ToArray()
        } finally { $writer.Dispose(); $headerStream.Dispose() }
        $mac = Get-AuthenticatedBytes $header $ciphertext $macKey
        $out = [System.IO.MemoryStream]::new()
        try {
            $out.Write($header, 0, $header.Length)
            $out.Write($ciphertext, 0, $ciphertext.Length)
            $out.Write($mac, 0, $mac.Length)
            $recovery = $out.ToArray()
        } finally { $out.Dispose() }
        Write-AtomicCiphertext $Destination $recovery
    } finally {
        foreach ($secret in @($bundle, $passwordBytes, $derived, $aesKey, $macKey, $ciphertext, $recovery)) {
            if ($null -ne $secret -and $secret -is [byte[]]) { [System.Array]::Clear($secret, 0, $secret.Length) }
        }
    }
}

function Import-DarimRecoveryKey {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)][string]$Path,
        [Parameter(Mandatory)][System.Security.SecureString]$Password
    )
    $file = [System.IO.FileInfo]::new([System.IO.Path]::GetFullPath($Path))
    if (-not $file.Exists -or $file.Length -lt 100 -or $file.Length -gt 4096) {
        throw 'Recovery key file is missing or invalid.'
    }
    $bytes = [System.IO.File]::ReadAllBytes($file.FullName)
    $stream = [System.IO.MemoryStream]::new($bytes, $false)
    $reader = [System.IO.BinaryReader]::new($stream)
    $passwordBytes = $null
    $derived = $null
    $plaintext = $null
    $returnKey = $false
    try {
        if (-not (Test-EqualBytes $reader.ReadBytes(8) $script:RecoveryMagic) -or $reader.ReadByte() -ne 1) {
            throw 'Recovery key format is unsupported.'
        }
        $iterations = $reader.ReadInt32()
        if ($iterations -lt 100000 -or $iterations -gt 1200000) { throw 'Recovery key parameters are invalid.' }
        $salt = $reader.ReadBytes(16)
        $iv = $reader.ReadBytes(16)
        $cipherLength = $reader.ReadInt32()
        if ($salt.Length -ne 16 -or $iv.Length -ne 16 -or $cipherLength -ne 80 -or
            ($stream.Position + $cipherLength + 32) -ne $bytes.Length) {
            throw 'Recovery key file is truncated or invalid.'
        }
        $headerLength = [int]$stream.Position
        $ciphertext = $reader.ReadBytes($cipherLength)
        $mac = $reader.ReadBytes(32)
        $header = [byte[]]::new($headerLength)
        [System.Buffer]::BlockCopy($bytes, 0, $header, 0, $headerLength)
        $passwordBytes = Get-PasswordBytes $Password
        $derived = Get-Pbkdf2Bytes $passwordBytes $salt $iterations
        $aesKey = [byte[]]::new(32)
        $macKey = [byte[]]::new(32)
        [System.Buffer]::BlockCopy($derived, 0, $aesKey, 0, 32)
        [System.Buffer]::BlockCopy($derived, 32, $macKey, 0, 32)
        $expectedMac = Get-AuthenticatedBytes $header $ciphertext $macKey
        if (-not (Test-EqualBytes $expectedMac $mac)) { throw 'Recovery key authentication failed.' }
        $plaintext = Get-AesCipher $ciphertext $aesKey $iv $true
        if ($plaintext.Length -ne 64) { throw 'Recovery key contents are invalid.' }
        $returnKey = $true
        return ,$plaintext
    } finally {
        $reader.Dispose()
        $stream.Dispose()
        [System.Array]::Clear($bytes, 0, $bytes.Length)
        foreach ($secret in @($passwordBytes, $derived, $(if (-not $returnKey) { $plaintext }), $aesKey, $macKey, $expectedMac)) {
            if ($null -ne $secret -and $secret -is [byte[]]) { [System.Array]::Clear($secret, 0, $secret.Length) }
        }
    }
}

Export-ModuleMember -Function Protect-DarimArchive, Get-DarimArchiveKey, Test-DarimArchive,
    Unprotect-DarimArchive, Export-DarimRecoveryKey, Import-DarimRecoveryKey
