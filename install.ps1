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
if (-not (Test-Path -LiteralPath (Join-Path $source 'SHA256SUMS.json') -PathType Leaf)) {
    throw 'Missing package integrity manifest. Download and extract the release ZIP.'
}
$manifest = Get-Content -LiteralPath (Join-Path $source 'SHA256SUMS.json') -Raw | ConvertFrom-Json
{
    foreach ($entry in $manifest.PSObject.Properties) {
        $inputFile = [IO.Path]::GetFullPath((Join-Path $source $entry.Name))
        if (-not $inputFile.StartsWith($source + '\', [StringComparison]::OrdinalIgnoreCase)) { throw 'Unsafe manifest path.' }
        if (-not (Test-Path -LiteralPath $inputFile -PathType Leaf) -or
            (Get-FileHash -LiteralPath $inputFile -Algorithm SHA256).Hash -ne $entry.Value) {
            throw ('Package integrity check failed: ' + $entry.Name)
        }
    }
}.Invoke()
$backup = Join-Path $game ('FFTModLoader.Backup\' + $project.Name + '-install-' + (Get-Date -Format 'yyyyMMdd-HHmmss-fff'))
if (Test-Path -LiteralPath $backup) { throw 'Backup directory already exists.' }
$backupFull = [IO.Path]::GetFullPath($backup)
if (-not $backupFull.StartsWith($game + '\FFTModLoader.Backup\', [StringComparison]::OrdinalIgnoreCase)) {
    throw 'Unsafe backup path.'
}
$files = Get-ChildItem -LiteralPath $source -Recurse -File | Where-Object {
    $relative = $_.FullName.Substring($source.Length + 1)
    $relative.StartsWith('Mods\') -or $relative.StartsWith('FFTModLoader.Runtime\') -or
    ($_.DirectoryName -eq $source -and ($_.Extension -eq '.dll' -or $_.Name -eq 'FFTModLoader.exe' -or $_.Name -eq 'FFTModLoader.config.json'))
}
foreach ($file in $files) {
    $relative = $file.FullName.Substring($source.Length + 1).Replace('\','/')
    if (-not $manifest.PSObject.Properties[$relative]) { throw ('Unverified package file: ' + $relative) }
}
if ($project.Name -in @('GenericKnights','ReworkedChemist')) {
    if (-not (Test-Path -LiteralPath (Join-Path $game 'FFTModLoader.exe') -PathType Leaf)) { throw 'Install the complete FFTModLoader package first.' }
    $required = @{
        'FFTModLoader.Runtime\InternalMods\JobExpansion\ModConfig.json' = 'fftmodloader.jobexpansion'
        'Mods\fftivc.utility.modloader\ModConfig.json' = 'fftivc.utility.modloader'
        'Mods\reloaded.sharedlib.hooks\ModConfig.json' = 'reloaded.sharedlib.hooks'
        'Mods\Reloaded.Memory.SigScan.ReloadedII\ModConfig.json' = 'Reloaded.Memory.SigScan.ReloadedII'
        'Mods\ffttic.jobs.genericjobs\ModConfig.json' = 'ffttic.jobs.genericjobs'
    }
    foreach ($dependency in $required.GetEnumerator()) {
        $config = Join-Path $game $dependency.Key
        if (-not (Test-Path -LiteralPath $config -PathType Leaf) -or
            (Get-Content -LiteralPath $config -Raw | ConvertFrom-Json).ModId -ne $dependency.Value) {
            throw ('Install the complete FFTModLoader package first. Missing dependency: ' + $dependency.Value)
        }
    }
}
if ($project.Name -eq 'ReworkedChemist') {
    $frameworkConfig = Join-Path $game 'FFTModLoader.Runtime\InternalMods\ContentExpansion\ModConfig.json'
    if (-not (Test-Path -LiteralPath $frameworkConfig -PathType Leaf) -or
        (Get-Content -LiteralPath $frameworkConfig -Raw | ConvertFrom-Json).ModId -ne 'fftmodloader.contentexpansion') {
        throw 'Install FFTModLoader 0.11.7 or newer before Reworked Chemist.'
    }
    # Move only an explicitly identified legacy mod, never saves or unrelated folders.
    $legacy = [IO.Path]::GetFullPath((Join-Path $game 'Mods\Reworked Chemist - Venom Test'))
    if (Test-Path -LiteralPath $legacy) {
        $legacyConfig = Join-Path $legacy 'ModConfig.json'
        if (-not (Test-Path -LiteralPath $legacyConfig -PathType Leaf) -or
            (Get-Content -LiteralPath $legacyConfig -Raw | ConvertFrom-Json).ModId -notin @('ffttic.tests.reworkedchemist.venom','ffttic.jobs.reworkedchemist')) {
            throw 'Legacy folder identity could not be verified; no files moved.'
        }
        $legacyBackup = [IO.Path]::GetFullPath((Join-Path $backup 'legacy\Reworked Chemist - Venom Test'))
        if (-not $legacy.StartsWith($game + '\Mods\', [StringComparison]::OrdinalIgnoreCase) -or
            -not $legacyBackup.StartsWith($backupFull + '\', [StringComparison]::OrdinalIgnoreCase)) { throw 'Unsafe migration paths.' }
        New-Item -ItemType Directory -Path (Split-Path $legacyBackup -Parent) -Force | Out-Null
        Move-Item -LiteralPath $legacy -Destination $legacyBackup
        Write-Host ('Legacy mod preserved in backup: ' + $legacyBackup)
    }
    $current = [IO.Path]::GetFullPath((Join-Path $game 'Mods\Reworked Chemist'))
    $currentConfig = Join-Path $current 'ModConfig.json'
    if (Test-Path -LiteralPath $current) {
        if (-not (Test-Path -LiteralPath $currentConfig -PathType Leaf) -or
            (Get-Content -LiteralPath $currentConfig -Raw | ConvertFrom-Json).ModId -notin @('ffttic.tests.reworkedchemist.venom','ffttic.jobs.reworkedchemist')) {
            throw 'Reworked Chemist folder identity could not be verified; no files overwritten.'
        }
        # Retire only the old consumer assembly; the save namespace is unchanged.
        foreach ($name in @('FFTModLoader.ReworkedChemist.VenomTest.dll','FFTModLoader.ReworkedChemist.VenomTest.deps.json')) {
            $oldFile = [IO.Path]::GetFullPath((Join-Path $current $name))
            $oldBackup = [IO.Path]::GetFullPath((Join-Path $backup ('legacy-consumer\' + $name)))
            if (-not $oldFile.StartsWith($game + '\Mods\Reworked Chemist\', [StringComparison]::OrdinalIgnoreCase) -or
                -not $oldBackup.StartsWith($backupFull + '\', [StringComparison]::OrdinalIgnoreCase)) { throw 'Unsafe consumer migration path.' }
            if (Test-Path -LiteralPath $oldFile -PathType Leaf) {
                New-Item -ItemType Directory -Path (Split-Path $oldBackup -Parent) -Force | Out-Null
                Move-Item -LiteralPath $oldFile -Destination $oldBackup
            }
        }
    }
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
