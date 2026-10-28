<#
.SYNOPSIS
    Writes your own Microsoft Store identity into Package.appxmanifest.

.DESCRIPTION
    The repository ships with the ORIGINAL Lively identity. You cannot submit it
    to the Store under that identity - the name is owned by the original author.

    Get these four values from Partner Center:
      Product identity -> Package/Identity/Name        -> -IdentityName
      Product identity -> Package/Identity/Publisher   -> -Publisher
      Product identity -> Package/Properties/DisplayName        -> -DisplayName
      Product identity -> Package/Properties/PublisherDisplayName -> -PublisherDisplayName

    The original manifest is backed up to Package.appxmanifest.orig the first
    time this script runs.

.EXAMPLE
    .\set-store-identity.ps1 `
        -IdentityName '12345YourName.YourApp' `
        -Publisher 'CN=00000000-0000-0000-0000-000000000000' `
        -DisplayName 'Your App Name' `
        -PublisherDisplayName 'Your Name'
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][string]$IdentityName,
    [Parameter(Mandatory = $true)][string]$Publisher,
    [Parameter(Mandatory = $true)][string]$DisplayName,
    [Parameter(Mandatory = $true)][string]$PublisherDisplayName,

    # Optional. Must be four parts, e.g. 1.0.165.0, and must increase on every submission.
    [string]$Version,

    # Optional. Shown in the Store listing and on the tile.
    [string]$Description
)

$ErrorActionPreference = 'Stop'

if ($Version -and $Version -notmatch '^\d+\.\d+\.\d+\.\d+$') {
    throw "Version must look like 1.0.0.0 (got '$Version')"
}

$repo     = Split-Path -Parent $PSScriptRoot
$manifest = Join-Path $repo 'src\Lively\Lively.UI.WinUI\Package.appxmanifest'
$backup   = "$manifest.orig"

if (-not (Test-Path $manifest)) { throw "Manifest not found: $manifest" }

if (-not (Test-Path $backup)) {
    Copy-Item $manifest $backup
    Write-Host "Backed up original manifest to $backup"
}

$text = Get-Content $manifest -Raw
$before = $text

function Set-Attribute {
    param(
        [string]$InputText,
        [string]$ElementPattern,
        [string]$Attribute,
        [string]$Value
    )
    $m = [regex]::Match($InputText, $ElementPattern)
    if (-not $m.Success) { throw "Could not find element matching $ElementPattern" }
    $element = $m.Value
    $updated = [regex]::Replace(
        $element,
        "(\s$Attribute\s*=\s*"")[^""]*("")",
        { param($match) $match.Groups[1].Value + $Value + $match.Groups[2].Value },
        1
    )
    if ($updated -eq $element) { throw "Attribute '$Attribute' not found on $ElementPattern" }
    return $InputText.Replace($element, $updated)
}

function Set-ElementText {
    param(
        [string]$InputText,
        [string]$Pattern,
        [string]$Value
    )
    if (-not [regex]::IsMatch($InputText, $Pattern)) {
        throw "Could not find element matching $Pattern"
    }
    return [regex]::Replace($InputText, $Pattern, { param($m) $m.Groups[1].Value + $Value + $m.Groups[2].Value }, 1)
}

# <Identity Name="..." Publisher="..." Version="..." />
$text = Set-Attribute $text '(?s)<Identity\b[^>]*/>' 'Name' $IdentityName
$text = Set-Attribute $text '(?s)<Identity\b[^>]*/>' 'Publisher' $Publisher
if ($Version) {
    $text = Set-Attribute $text '(?s)<Identity\b[^>]*/>' 'Version' $Version
}

# <Properties> <DisplayName>..</DisplayName> <PublisherDisplayName>..</...> </Properties>
$text = Set-ElementText $text '(?s)(<Properties>.*?<DisplayName>)[^<]*(</DisplayName>)' $DisplayName
$text = Set-ElementText $text '(?s)(<Properties>.*?<PublisherDisplayName>)[^<]*(</PublisherDisplayName>)' $PublisherDisplayName

# <uap:VisualElements DisplayName="..." ...>
$text = Set-Attribute $text '(?s)<uap:VisualElements\b[^>]*>' 'DisplayName' $DisplayName
if ($Description) {
    $text = Set-Attribute $text '(?s)<uap:VisualElements\b[^>]*>' 'Description' $Description
}

# <desktop:StartupTask ... DisplayName="..." />
$text = Set-Attribute $text '(?s)<desktop:StartupTask\b[^>]*/>' 'DisplayName' $DisplayName

if ($text -eq $before) {
    Write-Host 'Nothing changed.' -ForegroundColor Yellow
}
else {
    # Write without a BOM; MakeAppx is happier without one.
    $utf8 = New-Object System.Text.UTF8Encoding($false)
    [System.IO.File]::WriteAllText($manifest, $text, $utf8)
    Write-Host ''
    Write-Host 'Manifest updated:' -ForegroundColor Green
    Write-Host "  Name                  $IdentityName"
    Write-Host "  Publisher             $Publisher"
    Write-Host "  DisplayName           $DisplayName"
    Write-Host "  PublisherDisplayName  $PublisherDisplayName"
    if ($Version) { Write-Host "  Version               $Version" }
    if ($Description) { Write-Host "  Description           $Description" }
}

# Show the result so mistakes are obvious
Write-Host ''
Write-Host 'Current identity block:'
Select-String -Path $manifest -Pattern '<Identity|<DisplayName>|<PublisherDisplayName>|<uap:VisualElements' |
    ForEach-Object { '  ' + $_.Line.Trim() }
