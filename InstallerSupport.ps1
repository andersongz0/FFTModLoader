function Get-FFTGameDirectory {
    param([string]$Path)
    if ([string]::IsNullOrWhiteSpace($Path)) { return }
    try {
    $pathValue = $Path.Trim().Trim('"')
    if (Test-Path -LiteralPath $pathValue -PathType Leaf) {
        if ([IO.Path]::GetFileName($pathValue) -ine 'FFT_enhanced.exe') { return }
        $pathValue = Split-Path $pathValue -Parent
    }
    if (Test-Path -LiteralPath (Join-Path $pathValue 'FFT_enhanced.exe') -PathType Leaf) {
        return (Resolve-Path -LiteralPath $pathValue).Path.TrimEnd('\')
    }
    } catch { return }
}

function Get-FFTSteamRoots {
    $roots = @(
        (Join-Path ${env:ProgramFiles(x86)} 'Steam'),
        (Join-Path $env:ProgramFiles 'Steam')
    )
    foreach ($registryPath in @('HKCU:\Software\Valve\Steam','HKLM:\SOFTWARE\WOW6432Node\Valve\Steam','HKLM:\SOFTWARE\Valve\Steam')) {
        $entry = Get-ItemProperty -LiteralPath $registryPath -ErrorAction SilentlyContinue
        if ($entry) { $roots += $entry.SteamPath; $roots += $entry.InstallPath }
    }
    $roots | Where-Object { $_ -and (Test-Path -LiteralPath $_ -PathType Container) } | Select-Object -Unique
}

function Find-FFTGameDirectories {
    param([string[]]$SteamRoots = @(Get-FFTSteamRoots))
    $libraries = @($SteamRoots)
    foreach ($root in $SteamRoots) {
        $vdf = Join-Path $root 'steamapps\libraryfolders.vdf'
        if (Test-Path -LiteralPath $vdf -PathType Leaf) {
            $text = Get-Content -LiteralPath $vdf -Raw
            # Current and older Steam library formats. Do not crawl entire disks.
            foreach ($match in [regex]::Matches($text, '"(?:path|\d+)"\s+"((?:[^"\\]|\\.)*)"')) {
                $value = $match.Groups[1].Value.Replace('\\','\')
                if ([IO.Path]::IsPathRooted($value)) { $libraries += $value }
            }
        }
    }
    $found = @()
    foreach ($library in ($libraries | Where-Object { $_ } | Select-Object -Unique)) {
        $common = Join-Path $library 'steamapps\common'
        if (-not (Test-Path -LiteralPath $common -PathType Container)) { continue }
        foreach ($directory in Get-ChildItem -LiteralPath $common -Directory -ErrorAction SilentlyContinue) {
            $game = Get-FFTGameDirectory -Path $directory.FullName
            if ($game) { $found += $game }
        }
    }
    $found | Sort-Object -Unique
}

function Select-FFTGameDirectory {
    param([string[]]$Candidates, [bool]$English = $false, [scriptblock]$ReadInput = { param($Prompt) Read-Host $Prompt })
    if ($Candidates.Count -eq 1) { return Get-FFTGameDirectory -Path $Candidates[0] }
    if ($Candidates.Count -gt 1) {
        Write-Host $(if ($English) { 'Several game installations found:' } else { 'Mais de uma instalacao encontrada:' })
        for ($index = 0; $index -lt $Candidates.Count; $index++) { Write-Host ('{0} - {1}' -f ($index + 1), $Candidates[$index]) }
        $choice = & $ReadInput $(if ($English) { 'Select a number, or press Enter to choose another folder' } else { 'Escolha um numero ou Enter para outra pasta' })
        $number = 0
        if ([int]::TryParse($choice, [ref]$number) -and $number -ge 1 -and $number -le $Candidates.Count) {
            return Get-FFTGameDirectory -Path $Candidates[$number - 1]
        }
    }
    while ($true) {
        $pathValue = & $ReadInput $(if ($English) { 'Enter the path to FFT_enhanced.exe or its folder (Enter cancels)' } else { 'Informe o caminho de FFT_enhanced.exe ou sua pasta (Enter cancela)' })
        if ([string]::IsNullOrWhiteSpace($pathValue)) { return }
        $game = Get-FFTGameDirectory -Path $pathValue
        if ($game) { return $game }
        Write-Host $(if ($English) { 'FFT_enhanced.exe not found there.' } else { 'FFT_enhanced.exe nao encontrado nessa pasta.' }) -ForegroundColor Yellow
    }
}

function Confirm-FFTInstallation {
    param([string]$GameDirectory, [string]$ProjectName, [bool]$English = $false, [scriptblock]$ReadInput = { param($Prompt) Read-Host $Prompt })
    Write-Host ('{0}: {1}' -f $ProjectName, $GameDirectory)
    $answer = & $ReadInput $(if ($English) { 'Install in this folder? Type YES to confirm; anything else cancels' } else { 'Instalar nesta pasta? Digite SIM para confirmar; outra resposta cancela' })
    return ($answer -and $answer.Trim() -ieq $(if ($English) { 'YES' } else { 'SIM' }))
}

function New-FFTInstallCommand {
    param([string]$InstallerPath, [string]$GameDirectory, [string]$OutputPath)
    $scriptLiteral = "'" + $InstallerPath.Replace("'", "''") + "'"
    $gameLiteral = "'" + $GameDirectory.Replace("'", "''") + "'"
    $outputLiteral = "'" + $OutputPath.Replace("'", "''") + "'"
    # Capture results from the hidden elevated window in a generated temp file.
    $command = "`$ErrorActionPreference='Stop'; try { & $scriptLiteral -GameDirectory $gameLiteral *> $outputLiteral; exit 0 } catch { `$_ | Out-String | Set-Content -LiteralPath $outputLiteral; exit 1 }"
    [Convert]::ToBase64String([Text.Encoding]::Unicode.GetBytes($command))
}
