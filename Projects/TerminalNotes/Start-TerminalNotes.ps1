param([string]$Workspace)
$applicationPath = Join-Path $PSScriptRoot 'artifacts/win-x64-clean-ui/TerminalNotes.exe'
if (-not (Test-Path -LiteralPath $applicationPath)) {
    throw '먼저 README의 dotnet publish 명령으로 실행 파일을 빌드해주세요.'
}
if ($Workspace) { & $applicationPath --workspace $Workspace }
else { & $applicationPath }
