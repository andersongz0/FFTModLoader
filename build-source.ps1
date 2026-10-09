param([string]$Configuration = 'Release')
$ErrorActionPreference = 'Stop'
Set-Location -LiteralPath $PSScriptRoot
& cmake -S . -B build -G 'Visual Studio 17 2022' -A x64
if ($LASTEXITCODE -ne 0) { throw 'CMake configuration failed.' }
& cmake --build build --config $Configuration --target FFTModLoader RuntimeLogRelayTests --parallel
if ($LASTEXITCODE -ne 0) { throw 'Launcher build failed.' }
& (Join-Path $PSScriptRoot "build/$Configuration/RuntimeLogRelayTests.exe")
if ($LASTEXITCODE -ne 0) { throw 'Console regression tests failed.' }
& dotnet build src/JobExpansionRuntime/GenericJobs.csproj -c $Configuration --nologo
if ($LASTEXITCODE -ne 0) { throw 'JobExpansion build failed.' }
& dotnet build src/ContentExpansionModule/ContentExpansionModule.csproj -c $Configuration --nologo
if ($LASTEXITCODE -ne 0) { throw 'ContentExpansion build failed.' }
