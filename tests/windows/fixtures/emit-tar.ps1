param(
    [Parameter(Mandatory)][string]$TarPath,
    [int]$ExitCode = 0
)

$ErrorActionPreference = 'Stop'
$bytes = [System.IO.File]::ReadAllBytes($TarPath)
$stdout = [Console]::OpenStandardOutput()
$stdout.Write($bytes, 0, $bytes.Length)
$stdout.Flush()
exit $ExitCode
