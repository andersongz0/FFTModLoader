param()
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'InstallerSupport.ps1')

try {
    $project = Get-Content -LiteralPath (Join-Path $PSScriptRoot 'PROJECT.json') -Raw | ConvertFrom-Json
    Write-Host ('=== {0} {1} - ZeroDS ===' -f $project.Name, $project.Version)
    Write-Host '1 - Portugues / 2 - English'
    $english = (Read-Host 'Idioma / Language') -eq '2'
    $paths = @(Find-FFTGameDirectories)
    $game = Select-FFTGameDirectory -Candidates $paths -English $english
    if (-not $game) { Write-Host $(if ($english) { 'Installation cancelled.' } else { 'Instalacao cancelada.' }); exit 0 }
    if (-not (Confirm-FFTInstallation -GameDirectory $game -ProjectName $project.Name -English $english)) {
        Write-Host $(if ($english) { 'Installation cancelled. No files changed.' } else { 'Instalacao cancelada. Nenhum arquivo alterado.' })
        exit 0
    }
    if (Get-Process -Name fft_enhanced,fft_classic,FFTModLoader -ErrorAction SilentlyContinue) {
        throw $(if ($english) { 'Close the game and any running loader, then try again.' } else { 'Feche o jogo e o loader, se estiver aberto, e tente novamente.' })
    }
    $core = Join-Path $PSScriptRoot 'install.ps1'
    $principal = New-Object Security.Principal.WindowsPrincipal([Security.Principal.WindowsIdentity]::GetCurrent())
    if ($principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
        & $core -GameDirectory $game
    } else {
        Write-Host $(if ($english) { 'Windows will request permission to install in the selected folder.' } else { 'O Windows pedira permissao para instalar na pasta escolhida.' })
        # Encode literal paths rather than interpolating user input into shell arguments.
        $resultFile = (New-TemporaryFile).FullName
        try {
            $command = New-FFTInstallCommand -InstallerPath $core -GameDirectory $game -OutputPath $resultFile
            $child = Start-Process -FilePath "$PSHOME\powershell.exe" -ArgumentList @('-NoProfile','-ExecutionPolicy','Bypass','-EncodedCommand',$command) -Verb RunAs -WindowStyle Hidden -Wait -PassThru
            if (Test-Path -LiteralPath $resultFile) { Get-Content -LiteralPath $resultFile | ForEach-Object { Write-Host $_ } }
            if ($child.ExitCode -ne 0) { throw ('Installation failed. Exit code: ' + $child.ExitCode) }
        } finally {
            Remove-Item -LiteralPath $resultFile -ErrorAction SilentlyContinue
        }
    }
    Write-Host $(if ($english) { 'Installation completed. Start FFTModLoader.exe from the game folder.' } else { 'Instalacao concluida. Abra FFTModLoader.exe na pasta do jogo.' }) -ForegroundColor Green
} catch {
    Write-Host $_.Exception.Message -ForegroundColor Red
    Write-Host 'Instalacao nao concluida / Installation not completed.'
    exit 1
}
