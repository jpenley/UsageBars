<#
    probe-usage.ps1 — one-shot read of your Claude org usage/spend/credit.

    Run it yourself:
        pwsh -NoProfile -File tools\probe-usage.ps1

    What it does, all locally and read-only:
      1. Reads the DPAPI-wrapped AES key from Claude Desktop's "Local State".
      2. Decrypts the claude.ai session cookies from the local cookie store
         (Chromium v10/v20 AES-GCM). The cookie VALUE is never printed.
      3. Calls three org endpoints and prints the money/limit/reset fields.

    Nothing is uploaded anywhere except the normal HTTPS call to claude.ai that
    Claude Desktop itself already makes. Close Claude Desktop first if the cookie
    DB is locked (the script copies it, which usually avoids that).
#>
[CmdletBinding()]
param(
    # Auto-detected by default (see Find-ClaudeDir); override only if needed.
    [string]$ClaudeDir,
    [string]$Base = 'https://claude.ai',
    # Print the raw JSON payloads too (still no cookie). Off by default so output stays tidy.
    [switch]$Raw
)

$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Security

function Find-ClaudeDir {
    # The Store/MSIX install keeps the live cookie store under the package's
    # LocalCache, NOT under %APPDATA%\Claude. Check both, newest Local State wins.
    $candidates = @()
    $packages = Join-Path $env:LOCALAPPDATA 'Packages'
    if (Test-Path $packages) {
        Get-ChildItem $packages -Directory -Filter 'Claude_*' -ErrorAction SilentlyContinue | ForEach-Object {
            $candidates += Join-Path $_.FullName 'LocalCache\Roaming\Claude'
        }
    }
    $candidates += Join-Path $env:APPDATA 'Claude'

    $candidates |
        Where-Object { (Test-Path (Join-Path $_ 'Local State')) -and (Test-Path (Join-Path $_ 'Network\Cookies')) } |
        Sort-Object { (Get-Item (Join-Path $_ 'Local State')).LastWriteTimeUtc } -Descending |
        Select-Object -First 1
}

if (-not $ClaudeDir) { $ClaudeDir = Find-ClaudeDir }
if (-not $ClaudeDir) {
    throw "Could not find a Claude data dir with Local State + Network\Cookies (checked the MSIX package path and %APPDATA%\Claude). Pass -ClaudeDir explicitly."
}
Write-Host "Claude data dir: $ClaudeDir"

function Read-DpapiKey {
    param([string]$localStatePath)
    $json = Get-Content -Raw -LiteralPath $localStatePath | ConvertFrom-Json
    $b64 = $json.os_crypt.encrypted_key
    if (-not $b64) { throw "no os_crypt.encrypted_key in Local State" }
    $blob = [Convert]::FromBase64String($b64)
    # Strip the 5-byte "DPAPI" prefix, then unprotect under the current user.
    $dpapi = $blob[5..($blob.Length - 1)]
    [System.Security.Cryptography.ProtectedData]::Unprotect(
        $dpapi, $null, [System.Security.Cryptography.DataProtectionScope]::CurrentUser)
}

function Unprotect-Value {
    param([byte[]]$enc, [byte[]]$aesKey)

    # Legacy DPAPI-only cookies (no v10/v20 prefix).
    $prefix = [System.Text.Encoding]::ASCII.GetString($enc[0..2])
    if ($prefix -ne 'v10' -and $prefix -ne 'v20') {
        $plain = [System.Security.Cryptography.ProtectedData]::Unprotect(
            $enc, $null, [System.Security.Cryptography.DataProtectionScope]::CurrentUser)
        return [System.Text.Encoding]::UTF8.GetString($plain)
    }

    # AES-256-GCM: [3-byte tag][12-byte nonce][ciphertext][16-byte auth tag]
    $nonce  = $enc[3..14]
    $tag    = $enc[($enc.Length - 16)..($enc.Length - 1)]
    $cipher = $enc[15..($enc.Length - 17)]
    $plain  = New-Object byte[] $cipher.Length
    $gcm = [System.Security.Cryptography.AesGcm]::new($aesKey)
    try { $gcm.Decrypt($nonce, $cipher, $tag, $plain) } finally { $gcm.Dispose() }

    $text = [System.Text.Encoding]::UTF8.GetString($plain)
    # v20 (app-bound) prepends a 32-byte header before the real value.
    if ($prefix -eq 'v20' -and $plain.Length -gt 32) {
        $text = [System.Text.Encoding]::UTF8.GetString($plain[32..($plain.Length - 1)])
    }
    return $text
}

$localState = Join-Path $ClaudeDir 'Local State'
$cookiesDb  = Join-Path $ClaudeDir 'Network\Cookies'
if (-not (Test-Path $localState)) { throw "Local State not found at $localState" }
if (-not (Test-Path $cookiesDb))  { throw "Cookies DB not found at $cookiesDb" }

$aesKey = Read-DpapiKey $localState

# Copy the DB so a running Claude Desktop doesn't block us.
$tmp = Join-Path ([IO.Path]::GetTempPath()) ("cookies-" + [Guid]::NewGuid().ToString('N') + ".db")
Copy-Item -LiteralPath $cookiesDb -Destination $tmp -Force

# Pull cookies via System.Data.SQLite if present, else parse the file directly.
$rows = @()
try {
    $conn = New-Object System.Data.SQLite.SQLiteConnection("Data Source=$tmp;Read Only=True")
    $conn.Open()
    $cmd = $conn.CreateCommand()
    $cmd.CommandText = "select name, host_key, encrypted_value from cookies where host_key like '%claude.ai%'"
    $r = $cmd.ExecuteReader()
    while ($r.Read()) {
        $len = $r.GetBytes(2, 0, $null, 0, 0)
        $buf = New-Object byte[] $len
        [void]$r.GetBytes(2, 0, $buf, 0, $len)
        $rows += [pscustomobject]@{ name = $r.GetString(0); host = $r.GetString(1); enc = $buf }
    }
    $conn.Close()
} catch {
    Write-Host "System.Data.SQLite unavailable ($($_.Exception.Message)); trying sqlite3.exe..." -ForegroundColor Yellow
    $sqlite = (Get-Command sqlite3 -ErrorAction SilentlyContinue)
    if (-not $sqlite) {
        throw "No SQLite available. Install the 'sqlite3' CLI, or run the Python variant (probe-usage.py)."
    }
    $out = & sqlite3.exe $tmp "select name || '|' || quote(encrypted_value) from cookies where host_key like '%claude.ai%';"
    foreach ($line in $out) {
        $name, $hexlit = $line -split '\|', 2
        $hex = $hexlit.Trim() -replace "^X'","" -replace "'$",""
        $bytes = for ($i = 0; $i -lt $hex.Length; $i += 2) { [Convert]::ToByte($hex.Substring($i, 2), 16) }
        $rows += [pscustomobject]@{ name = $name; host = 'claude.ai'; enc = [byte[]]$bytes }
    }
}
Remove-Item -LiteralPath $tmp -Force -ErrorAction SilentlyContinue

$cookiePairs = @()
$haveSession = $false
foreach ($row in $rows) {
    try {
        $val = Unprotect-Value -enc $row.enc -aesKey $aesKey
        if ($val) {
            $cookiePairs += "$($row.name)=$val"
            if ($row.name -match 'sessionKey|session') { $haveSession = $true }
        }
    } catch {
        Write-Host "  (could not decrypt cookie '$($row.name)': $($_.Exception.Message))" -ForegroundColor DarkYellow
    }
}

Write-Host ("Decrypted {0} claude.ai cookie(s); session cookie present: {1}" -f $cookiePairs.Count, $haveSession)
if (-not $haveSession) { Write-Host "No session cookie found — are you signed in to Claude Desktop?" -ForegroundColor Yellow }

$cookieHeader = ($cookiePairs -join '; ')

# Discover the org id from the same place UsageBars found it, but let the API confirm.
$headers = @{
    Cookie          = $cookieHeader
    'User-Agent'    = 'UsageBars-Probe/1.0'
    'Accept'        = 'application/json'
    'anthropic-client-platform' = 'web_claude_ai'
}

function Get-Json {
    param([string]$url)
    try {
        return Invoke-RestMethod -Uri $url -Headers $headers -Method Get -TimeoutSec 20
    } catch {
        $code = try { $_.Exception.Response.StatusCode.value__ } catch { '?' }
        Write-Host "  GET $url -> HTTP $code" -ForegroundColor Red
        return $null
    }
}

# Find the org id.
$org = $null
$orgs = Get-Json "$Base/api/organizations"
if ($orgs) {
    $org = ($orgs | Where-Object { $_.uuid } | Select-Object -First 1).uuid
}
if (-not $org) { $org = '7c1cc965-1604-4980-8572-eb6072624100' }  # fallback: id seen locally
Write-Host "Org: $org`n"

$usage      = Get-Json "$Base/api/organizations/$org/usage"
$overage    = Get-Json "$Base/api/organizations/$org/overage_spend_limit"
$prepaid    = Get-Json "$Base/api/organizations/$org/prepaid_credit_balance"
$subStatus  = Get-Json "$Base/api/organizations/$org/subscription_status"

Write-Host "==================== SUMMARY ====================" -ForegroundColor Cyan

if ($usage) {
    if ($usage.extra_usage) {
        $eu = $usage.extra_usage
        Write-Host "extra_usage:"
        Write-Host ("  is_enabled    : {0}" -f $eu.is_enabled)
        Write-Host ("  used_credits  : {0}" -f $eu.used_credits)
        Write-Host ("  monthly_limit : {0}" -f $eu.monthly_limit)
        Write-Host ("  utilization   : {0}" -f $eu.utilization)
    } else {
        Write-Host "extra_usage: (absent)"
    }

    foreach ($w in 'five_hour','seven_day','seven_day_opus','seven_day_sonnet','seven_day_cowork') {
        if ($usage.$w) {
            Write-Host ("{0,-18}: util={1,-6} resets_at={2}" -f $w, $usage.$w.utilization, $usage.$w.resets_at)
        }
    }
    if ($usage.limits) {
        Write-Host "limits[]:"
        foreach ($l in $usage.limits) {
            Write-Host ("  kind={0} group={1} percent={2} resets_at={3}" -f $l.kind, $l.group, $l.percent, $l.resets_at)
        }
    }
}

if ($overage)   { Write-Host "`noverage_spend_limit:"; $overage   | Format-List | Out-String | Write-Host }
if ($prepaid)   { Write-Host "prepaid_credit_balance:"; $prepaid | Format-List | Out-String | Write-Host }
if ($subStatus) { Write-Host "subscription_status:"; $subStatus  | Format-List | Out-String | Write-Host }

if ($Raw) {
    Write-Host "==================== RAW ====================" -ForegroundColor DarkGray
    Write-Host "`n/usage:";                 $usage     | ConvertTo-Json -Depth 8
    Write-Host "`n/overage_spend_limit:";   $overage   | ConvertTo-Json -Depth 8
    Write-Host "`n/prepaid_credit_balance:";$prepaid   | ConvertTo-Json -Depth 8
}

Write-Host "`nDone. (Your cookie value was never printed.)" -ForegroundColor Green
