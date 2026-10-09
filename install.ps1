param([Parameter(Mandatory=$true)][string]$GameDirectory)
$ErrorActionPreference = 'Stop'
$game = (Resolve-Path -LiteralPath $GameDirectory).Path.TrimEnd('\')
if (-not (Test-Path -LiteralPath (Join-Path $game 'fft_enhanced.exe') -PathType Leaf)) {
    throw 'Select the game installation containing fft_enhanced.exe.'
}
if (Get-Process -Name fft_enhanced,fft_classic,FFTModLoader -ErrorAction SilentlyContinue) {
    throw 'Close the game and FFTModLoader before installing.'
}
$source = [IO.Path]::GetFullPath($PSScriptRoot).TrimEnd('\')
if ($source -eq $game -or $source.StartsWith($game + '\', [StringComparison]::OrdinalIgnoreCase)) {
    throw 'Extract the release outside the game directory before running this installer.'
}
$project = Get-Content -LiteralPath (Join-Path $source 'PROJECT.json') -Raw | ConvertFrom-Json
if (Test-Path -LiteralPath (Join-Path $source 'SHA256SUMS.json')) {
    $manifest = Get-Content -LiteralPath (Join-Path $source 'SHA256SUMS.json') -Raw | ConvertFrom-Json
    foreach ($entry in $manifest.PSObject.Properties) {
        $inputFile = [IO.Path]::GetFullPath((Join-Path $source $entry.Name))
        if (-not $inputFile.StartsWith($source + '\', [StringComparison]::OrdinalIgnoreCase)) { throw 'Unsafe manifest path.' }
        if (-not (Test-Path -LiteralPath $inputFile -PathType Leaf) -or
            (Get-FileHash -LiteralPath $inputFile -Algorithm SHA256).Hash -ne $entry.Value) {
            throw ('Package integrity check failed: ' + $entry.Name)
        }
    }
}
$backup = Join-Path $game ('FFTModLoader.Backup\' + $project.Name + '-install-' + (Get-Date -Format 'yyyyMMdd-HHmmss-fff'))
if (Test-Path -LiteralPath $backup) { throw 'Backup directory already exists.' }
$backupFull = [IO.Path]::GetFullPath($backup)
if (-not $backupFull.StartsWith($game + '\FFTModLoader.Backup\', [StringComparison]::OrdinalIgnoreCase)) {
    throw 'Unsafe backup path.'
}
$files = Get-ChildItem -LiteralPath $source -Recurse -File | Where-Object {
    $relative = $_.FullName.Substring($source.Length + 1)
    $relative.StartsWith('Mods\') -or $relative.StartsWith('FFTModLoader.Runtime\') -or
    $_.DirectoryName -eq $source -and ($_.Extension -eq '.dll' -or $_.Name -eq 'FFTModLoader.exe' -or $_.Name -eq 'FFTModLoader.config.json')
}
foreach ($file in $files) {
    $relative = $file.FullName.Substring($source.Length + 1)
    if ($relative -match '(^|\\)(ExpandedSaves|Logs|ResolvedMods|Apps)(\\|$)') { throw 'Unexpected state folder.' }
    $target = [IO.Path]::GetFullPath((Join-Path $game $relative))
    if (-not $target.StartsWith($game + '\', [StringComparison]::OrdinalIgnoreCase)) { throw 'Unsafe target path.' }
    if (Test-Path -LiteralPath $target) {
        $old = Join-Path $backup $relative
        New-Item -ItemType Directory -Path (Split-Path $old -Parent) -Force | Out-Null
        Copy-Item -LiteralPath $target -Destination $old
    }
    New-Item -ItemType Directory -Path (Split-Path $target -Parent) -Force | Out-Null
    Copy-Item -LiteralPath $file.FullName -Destination $target -Force
}
Write-Host ("Installed {0} by ZeroDS. Backup: {1}" -f $project.Name, $backup)
Write-Host 'Saves, sidecars and unrelated mods were not modified.'
