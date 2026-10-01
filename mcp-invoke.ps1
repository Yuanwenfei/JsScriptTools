# Raw MCP client for the Emulate3D web server (streamable HTTP + SSE).
# Usage:
#   powershell -File mcp-invoke.ps1 -Tool qlp_diag -ArgsJson '{"since":"all"}'
#   powershell -File mcp-invoke.ps1 -Tool modify_source_within_project -ArgsFile payload.json
# Returns the JSON-RPC result as text (SSE "data:" frames are unwrapped).

param(
    [string]$Tool,
    [string]$ArgsJson = '{}',
    [string]$ArgsFile,
    [int]$Port = 9081,
    [string]$Uri = '',
    [switch]$DumpRaw,
    [switch]$ListTools
)

$ErrorActionPreference = 'Stop'
if (-not $Uri) { $Uri = "http://localhost:$Port/mcp" }

function Send-Mcp {
    param([hashtable]$Message)
    $json = $Message | ConvertTo-Json -Depth 30 -Compress
    $bytes = [System.Text.Encoding]::UTF8.GetBytes($json)
    $req = [System.Net.HttpWebRequest]::Create($Uri)
    $req.Method = 'POST'
    $req.ContentType = 'application/json'
    $req.Accept = 'application/json, text/event-stream'
    $req.Timeout = 600000
    $req.ReadWriteTimeout = 600000
    $req.ContentLength = $bytes.Length
    $stream = $req.GetRequestStream()
    $stream.Write($bytes, 0, $bytes.Length)
    $stream.Close()
    $resp = $req.GetResponse()
    $reader = New-Object System.IO.StreamReader($resp.GetResponseStream(), [System.Text.Encoding]::UTF8)
    $text = $reader.ReadToEnd()
    $reader.Close()
    $resp.Close()
    return $text
}

function Unwrap-Sse {
    param([string]$Text)
    $sb = New-Object System.Text.StringBuilder
    foreach ($line in $Text -split "`n") {
        $t = $line.TrimEnd("`r")
        if ($t.StartsWith('data:')) { [void]$sb.AppendLine($t.Substring(5).TrimStart()) }
    }
    $out = $sb.ToString().Trim()
    if ($out.Length -eq 0) { return $Text.Trim() }
    return $out
}

# initialize (stateless server: no session id is returned, so no follow-up notifications are needed)
Send-Mcp @{ jsonrpc = '2.0'; id = 1; method = 'initialize'; params = @{ protocolVersion = '2025-03-26'; capabilities = @{}; clientInfo = @{ name = 'qlp-push'; version = '1.0' } } } | Out-Null

if ($ListTools) {
    $raw = Send-Mcp @{ jsonrpc = '2.0'; id = 3; method = 'tools/list'; params = @{} }
    $obj = (Unwrap-Sse $raw) | ConvertFrom-Json
    foreach ($t in $obj.result.tools) { Write-Output $t.name }
    if ($obj.result.nextCursor) { Write-Output ('... nextCursor=' + $obj.result.nextCursor) }
    exit 0
}

if (-not $Tool) { Write-Output 'Need -Tool <name> (or -ListTools).'; exit 2 }

if ($ArgsFile) { $ArgsJson = [System.IO.File]::ReadAllText($ArgsFile, [System.Text.Encoding]::UTF8) }
$arguments = $ArgsJson | ConvertFrom-Json

$payload = @{
    jsonrpc = '2.0'
    id      = 2
    method  = 'tools/call'
    params  = @{ name = $Tool; arguments = $arguments }
}

$raw = Send-Mcp $payload
if ($DumpRaw) { Write-Output $raw; exit 0 }

$unwrapped = Unwrap-Sse $raw
try {
    $obj = $unwrapped | ConvertFrom-Json
    if ($obj.error) { Write-Output ('ERROR: ' + ($obj.error | ConvertTo-Json -Depth 6 -Compress)); exit 1 }
    foreach ($item in $obj.result.content) { if ($item.text) { Write-Output $item.text } }
    if ($obj.result.isError) { exit 1 }
} catch {
    Write-Output $unwrapped
}
