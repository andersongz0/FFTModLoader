param([string]$RelayExecutable = (Join-Path $PSScriptRoot '..\build\Release\RuntimeLogRelayTests.exe'))
$ErrorActionPreference='Stop'
$fixture=Join-Path ([IO.Path]::GetTempPath()) ('fft-console-live-'+[Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $fixture | Out-Null
$channel=Join-Path $fixture 'Content-session-live.txt'
$output=Join-Path $fixture 'reader-output.txt'
$errorOutput=Join-Path $fixture 'reader-errors.txt'
$writer=$null
$reader=$null
try {
    # Match SessionContentLog: no FileStream.Flush(true)/FlushFileBuffers, and
    # the writer remains open until the other process has consumed all lines.
    $file=[IO.FileStream]::new($channel,[IO.FileMode]::CreateNew,[IO.FileAccess]::Write,[IO.FileShare]::ReadWrite)
    $writer=[IO.StreamWriter]::new($file,[Text.UTF8Encoding]::new($false))
    $writer.AutoFlush=$true
    $reader=Start-Process -FilePath $RelayExecutable -ArgumentList @('--watch',('"'+$channel+'"'),'4002') -WindowStyle Hidden -PassThru -RedirectStandardOutput $output -RedirectStandardError $errorOutput
    $writer.WriteLine('[RuntimeSession] Live content channel ready.')
    for($index=0;$index -lt 4000;$index++) { $writer.WriteLine('[VisualExpansion] SPR file='+$index+' served') }
    $writer.Write('[BestialWarrior] [Form] Wolf')
    $writer.Flush()
    if($reader.WaitForExit(100)) { throw 'Reader completed with an incomplete final line.' }
    $writer.WriteLine(' active')
    if(-not $reader.WaitForExit(15000)) { throw 'Reader timed out while the .NET writer remained open.' }
    if($reader.ExitCode -ne 0) { throw ('Reader failed: '+(Get-Content -LiteralPath $errorOutput -Raw)) }
    if(-not (Get-Content -LiteralPath $output -Raw).Contains('4002 complete messages')) { throw 'Reader did not confirm all live messages.' }
    Write-Output 'PASS: production C++ reader receives .NET AutoFlush messages across processes while writer is open, without disk/metadata flush; partial lines and a >64KB burst preserved.'
} finally {
    if($reader -and -not $reader.HasExited) { $reader.Kill(); $reader.WaitForExit() }
    if($writer) { $writer.Dispose() }
    # Delete only explicitly named disposable fixture files; no recursive move.
    foreach($name in @($channel,$output,$errorOutput)) { if(Test-Path -LiteralPath $name) { Remove-Item -LiteralPath $name } }
    Remove-Item -LiteralPath $fixture
}
