<#
.SYNOPSIS
Builds the Release server in place while MCP servers or inspected apps still have the old binaries loaded.

.DESCRIPTION
An MCP client configured with src\WpfVisualTreeMcp.Server\bin\Release\net8.0\WpfVisualTreeMcp.Server.exe
keeps those files loaded, so a plain Release build fails with MSB3021/MSB3027. Windows allows a loaded
image to be renamed: this script renames the locked binaries aside (*.stale-<timestamp>), builds, and
removes stale copies that nothing holds any more. No process is stopped.

Running servers keep the old build until the MCP client reconnects, and an application that already
loaded an Inspector keeps it until it restarts; wpf_attach reports that mismatch as inspector_compatibility.

.PARAMETER Configuration
Build configuration. Default: Release.

.PARAMETER CleanOnly
Only remove stale copies that are no longer in use; do not build.
#>
[CmdletBinding()]
param(
    [string]$Configuration = 'Release',
    [switch]$CleanOnly
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$output = Join-Path $root "src\WpfVisualTreeMcp.Server\bin\$Configuration\net8.0"

function Test-Locked([string]$path) {
    try {
        $stream = [IO.File]::Open($path, 'Open', 'ReadWrite', 'None')
        $stream.Close()
        return $false
    }
    catch [IO.IOException] { return $true }
    catch [UnauthorizedAccessException] { return $true }
}

function Remove-UnusedStale {
    if (-not (Test-Path $output)) { return }
    $removed = 0; $kept = 0
    foreach ($file in Get-ChildItem $output -Recurse -File | Where-Object { $_.Name -match '\.stale-\d+$' }) {
        if (Test-Locked $file.FullName) { $kept++ }
        else { Remove-Item -LiteralPath $file.FullName -Force; $removed++ }
    }
    Write-Host "Stale copies: removed $removed, still in use $kept."
}

if (-not $CleanOnly) {
    $renamed = 0
    if (Test-Path $output) {
        $stamp = Get-Date -Format 'yyyyMMddHHmmss'
        foreach ($file in Get-ChildItem $output -Recurse -File -Include *.dll, *.exe | Where-Object { $_.Name -notmatch '\.stale-\d+$' }) {
            if (Test-Locked $file.FullName) {
                Rename-Item -LiteralPath $file.FullName -NewName ($file.Name + ".stale-$stamp")
                $renamed++
            }
        }
    }
    Write-Host "Renamed $renamed loaded file(s) aside."

    & dotnet build -c $Configuration (Join-Path $root 'WpfVisualTreeMcp.sln') --nologo -v quiet
    if ($LASTEXITCODE -ne 0) { throw "Build failed with exit code $LASTEXITCODE." }

    $server = Join-Path $output 'WpfVisualTreeMcp.Server.dll'
    Write-Host ("Built {0} at {1:HH:mm:ss}." -f $server, (Get-Item $server).LastWriteTime)
    if ($renamed -gt 0) {
        Write-Host 'Running MCP servers still use the previous build: reconnect them. Restart inspected apps to load the new Inspector.'
    }
}

Remove-UnusedStale
