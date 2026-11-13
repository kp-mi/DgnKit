<#
.SYNOPSIS
  Downloads the public DGN test corpus described in testdata/corpus.json into testdata/corpus/<id>/.
.DESCRIPTION
  - "files"       : downloaded as-is.
  - "archives"    : zip (or self-extracting .exe zip — NEVER executed) extracted with System.IO.Compression.
  - "crawl"       : IIS directory listing crawled for files matching "pattern" (e.g. WSDOT contracts).
  - "pdf"/"pdfArchives": reference plot PDFs, stored under <id>/pdf/.
  Already-downloaded files are skipped, so the script is resumable.
.EXAMPLE
  powershell -ExecutionPolicy Bypass -File tools/fetch-corpus.ps1 [-Only txdot-ec293,wsdot-XE3736]
#>
param(
    [string]$Manifest = (Join-Path $PSScriptRoot "..\testdata\corpus.json"),
    [string]$OutDir = (Join-Path $PSScriptRoot "..\testdata\corpus"),
    [string[]]$Only = @()
)
$ErrorActionPreference = "Stop"
[Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12
Add-Type -AssemblyName System.IO.Compression, System.IO.Compression.FileSystem
$ProgressPreference = "SilentlyContinue"

function Get-SafeName([string]$url) {
    $name = [Uri]::UnescapeDataString(([Uri]$url).Segments[-1])
    foreach ($c in [IO.Path]::GetInvalidFileNameChars()) { $name = $name.Replace($c, '_') }
    return $name
}

function Save-Url([string]$url, [string]$dir) {
    New-Item -ItemType Directory -Force -Path $dir | Out-Null
    $dest = Join-Path $dir (Get-SafeName $url)
    if ((Test-Path $dest) -and (Get-Item $dest).Length -gt 0) { return $dest }
    Write-Host "  GET $url"
    $tmp = "$dest.part"
    for ($i = 1; $i -le 3; $i++) {
        try {
            Invoke-WebRequest -Uri ($url -replace ' ', '%20') -OutFile $tmp -UseBasicParsing -TimeoutSec 600 -UserAgent "DgnKit-corpus-fetch/1.0"
            Move-Item -Force $tmp $dest
            return $dest
        } catch {
            Write-Warning "  attempt $i failed: $($_.Exception.Message)"
            Start-Sleep -Seconds (3 * $i)
        }
    }
    throw "Failed to download $url"
}

function Expand-ZipSafe([string]$zip, [string]$dir) {
    New-Item -ItemType Directory -Force -Path $dir | Out-Null
    $fs = [IO.File]::OpenRead($zip)
    try {
        $za = New-Object IO.Compression.ZipArchive($fs, [IO.Compression.ZipArchiveMode]::Read)
        foreach ($e in $za.Entries) {
            if ([string]::IsNullOrEmpty($e.Name)) { continue }
            # Flatten paths, block traversal.
            $name = $e.FullName -replace '[\\/]', '__'
            foreach ($c in [IO.Path]::GetInvalidFileNameChars()) { $name = $name.Replace($c, '_') }
            $dest = Join-Path $dir $name
            if (Test-Path $dest) { continue }
            $in = $e.Open(); $out = [IO.File]::Create($dest)
            try { $in.CopyTo($out) } finally { $out.Dispose(); $in.Dispose() }
        }
    } finally { $fs.Dispose() }
}

function Get-IisListing([string]$url, [int]$depth, [string]$pattern, [System.Collections.Generic.List[string]]$results) {
    try { $r = Invoke-WebRequest -Uri $url -UseBasicParsing -TimeoutSec 120 -UserAgent "DgnKit-corpus-fetch/1.0" }
    catch { Write-Warning "  listing failed: $url ($($_.Exception.Message))"; return }
    $rx = '(&lt;dir&gt;|\d+)\s+<A HREF="([^"]+)">([^<]+)</A>'
    foreach ($m in [regex]::Matches($r.Content, $rx)) {
        $href = [Net.WebUtility]::HtmlDecode($m.Groups[2].Value)
        $abs = (New-Object Uri([Uri]$url, $href)).AbsoluteUri
        if ($m.Groups[1].Value -eq '&lt;dir&gt;') {
            if ($depth -gt 0 -and $abs.Length -gt $url.Length) { Get-IisListing $abs ($depth - 1) $pattern $results }
        } elseif ([Uri]::UnescapeDataString($abs) -match $pattern) { $results.Add($abs) }
    }
}

$Only = @($Only | ForEach-Object { $_ -split ',' } | Where-Object { $_ })
$m = Get-Content $Manifest -Raw | ConvertFrom-Json
foreach ($it in $m.items) {
    if ($Only.Count -gt 0 -and -not ($Only -contains $it.id)) { continue }
    Write-Host "== $($it.id): $($it.source)"
    $dir = Join-Path $OutDir $it.id
    try {
        foreach ($u in @($it.files)) { if ($u) { Save-Url $u $dir | Out-Null } }
        foreach ($u in @($it.archives)) {
            if ($u) { $z = Save-Url $u (Join-Path $dir "_download"); Expand-ZipSafe $z $dir }
        }
        if ($it.crawl) {
            $urls = New-Object System.Collections.Generic.List[string]
            Get-IisListing $it.crawl.base ([int]$it.crawl.depth) $it.crawl.pattern $urls
            Write-Host "  crawl found $($urls.Count) file(s)"
            foreach ($u in $urls) { Save-Url $u $dir | Out-Null }
        }
        foreach ($u in @($it.pdf)) { if ($u) { Save-Url $u (Join-Path $dir "pdf") | Out-Null } }
        foreach ($u in @($it.pdfArchives)) {
            if ($u) { $z = Save-Url $u (Join-Path $dir "_download"); Expand-ZipSafe $z (Join-Path $dir "pdf") }
        }
    } catch { Write-Warning "  item $($it.id) incomplete: $($_.Exception.Message)" }
}
Write-Host "Done."
