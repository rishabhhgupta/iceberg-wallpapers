# Fails when an API key has found its way into a tracked file.
#
# Run this before every commit:
#     pwsh -File scripts/check-secrets.ps1
#
# The real keys live in secrets/ApiKeys.local.props, which git ignores. This
# script reads that file and refuses to pass if any of its values appear
# anywhere git would publish.

$ErrorActionPreference = "Stop"

$root = Split-Path -Parent $PSScriptRoot
$local = Join-Path $root "secrets/ApiKeys.local.props"

$patterns = @()

if (Test-Path $local) {
    [xml]$xml = Get-Content $local
    foreach ($group in $xml.Project.PropertyGroup) {
        foreach ($node in $group.ChildNodes) {
            $value = $node.InnerText
            if ($value -and $value.Trim().Length -ge 16) {
                $patterns += [regex]::Escape($value.Trim())
            }
        }
    }
}

# Shapes that look like a key even when the local file is absent (CI).
$patterns += '\b\d{6,8}-[0-9a-f]{32}\b'
$patterns += '(?i)(api[_-]?key|client_id)\s*[=:]\s*["''][A-Za-z0-9_\-]{20,}'

$tracked = git -C $root ls-files
$hits = @()

foreach ($file in $tracked) {
    if ($file -like "secrets/*") { continue }
    if ($file -eq "scripts/check-secrets.ps1") { continue }

    $path = Join-Path $root $file
    if (-not (Test-Path $path)) { continue }
    if ((Get-Item $path).Length -gt 2MB) { continue }

    $text = Get-Content $path -Raw -ErrorAction SilentlyContinue
    if ($null -eq $text) { continue }

    foreach ($pattern in $patterns) {
        if ($text -match $pattern) {
            $hits += "$file contains something that looks like an API key"
            break
        }
    }
}

if ($hits.Count -gt 0) {
    Write-Host "API key check FAILED:" -ForegroundColor Red
    $hits | ForEach-Object { Write-Host "  $_" -ForegroundColor Red }
    exit 1
}

Write-Host "No API keys found in tracked files." -ForegroundColor Green
