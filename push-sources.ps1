# Push workspace .cs files into the Emulate3D C# project over raw MCP.
# Usage:
#   powershell -File push-sources.ps1 -Files JsScriptTools.cs,JsSnapshot.cs
#   powershell -File push-sources.ps1 -Project JsScriptTools -Files (Get-ChildItem *.cs | % Name)
# Builds the JSON-RPC payload with ConvertTo-Json from the files on disk, so escaping is exact
# and there is no hand-written JSON / output length limit.

param(
    [string]$Project = 'JsScriptTools',
    [Parameter(Mandatory = $true)][string[]]$Files,
    [int]$Type = 0,
    [int]$Port = 9081,
    [switch]$List
)

$ErrorActionPreference = 'Stop'
$dir = $PSScriptRoot

# Tolerate a single comma separated string ("a.cs,b.cs") as well as a real array.
$Files = @($Files | ForEach-Object { $_ -split ',' } | ForEach-Object { $_.Trim() } | Where-Object { $_ })

if ($List) {
    foreach ($f in $Files) { Write-Output ($f + '  ->  ' + (Join-Path $dir $f)) }
    exit 0
}

$missing = @()
foreach ($f in $Files) { if (-not (Test-Path (Join-Path $dir $f))) { $missing += $f } }
if ($missing.Count -gt 0) { Write-Output ('Missing: ' + ($missing -join ', ')); exit 2 }

$sources = @()
foreach ($f in $Files) {
    $sources += @{ Name = $f; Source = [System.IO.File]::ReadAllText((Join-Path $dir $f), [System.Text.Encoding]::UTF8) }
}

$args = @{ script = @{ Name = $Project; Type = $Type; Sources = $sources } }
$argsJson = $args | ConvertTo-Json -Depth 20 -Compress
$tmp = Join-Path $env:TEMP ('js-push-' + [Guid]::NewGuid().ToString('n') + '.json')
[System.IO.File]::WriteAllText($tmp, $argsJson, [System.Text.Encoding]::UTF8)

$total = 0
foreach ($s in $sources) { $total += $s.Source.Length }
Write-Output ('Pushing ' + $sources.Count + ' file(s), ' + $total + ' chars, to project ' + $Project + ' ...')

try {
    & (Join-Path $dir 'mcp-invoke.ps1') -Tool 'modify_source_within_project' -ArgsFile $tmp -Port $Port
    $code = $LASTEXITCODE
} finally {
    Remove-Item $tmp -ErrorAction SilentlyContinue
}
if ($code -ne $null -and $code -ne 0) { exit $code }
