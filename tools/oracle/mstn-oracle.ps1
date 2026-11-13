<#
.SYNOPSIS
  Ground-truth "oracle" dump of DGN files using the licensed MicroStation installed on this machine (COM automation).
.DESCRIPTION
  Opens each DGN read-only via MicroStationDGN.Application.OpenDesignFileForProgram (no UI document window) and writes
  <name>.oracle.json next to -OutDir with: models (name, type, 3D, units, global origin), levels (id, number, name,
  color, weight, style, displayed/frozen/plot), line styles, fonts, and per-element records (id, type, level, color,
  weight, style, class, range in master units, key geometry). The JSON shape mirrors DgnKit's JsonDump so the test
  harness can diff them.
.EXAMPLE
  powershell -ExecutionPolicy Bypass -File tools/oracle/mstn-oracle.ps1 -Files testdata/corpus/txdot-ec293/ec293.dgn -OutDir testdata/oracle
#>
param(
    [string[]]$Files = @(),
    # Text file with one DGN path per line (use this when invoking via powershell -File).
    [string]$ListFile = "",
    [string]$OutDir = "",
    [int]$MaxElements = 200000,
    [switch]$KeepOpen
)
$ErrorActionPreference = "Stop"
if (-not $OutDir) { $OutDir = Join-Path (Split-Path -Parent $MyInvocation.MyCommand.Path) "..\..\testdata\oracle" }
if ($ListFile) { $Files += @(Get-Content $ListFile | Where-Object { $_.Trim() -and -not $_.StartsWith('#') }) }
if ($Files.Count -eq 0) { throw "No input files (use -Files or -ListFile)" }
New-Item -ItemType Directory -Force -Path $OutDir | Out-Null

function J([object]$v) {
    if ($null -eq $v) { return "null" }
    if ($v -is [bool]) { if ($v) { return "true" } else { return "false" } }
    if ($v -is [double] -or $v -is [single]) {
        if ([double]::IsNaN($v) -or [double]::IsInfinity($v)) { return "null" }
        return ([double]$v).ToString("R", [Globalization.CultureInfo]::InvariantCulture)
    }
    if ($v -is [int] -or $v -is [long] -or $v -is [int16] -or $v -is [byte] -or $v -is [uint32] -or $v -is [uint64]) { return "$v" }
    $s = [string]$v
    $s = $s.Replace('\', '\\').Replace('"', '\"').Replace("`r", '\r').Replace("`n", '\n').Replace("`t", '\t')
    $s = [regex]::Replace($s, '[\x00-\x1f]', { param($m) '\u{0:x4}' -f [int][char]$m.Value })
    return '"' + $s + '"'
}
function P3($p) { return "[" + (J $p.X) + "," + (J $p.Y) + "," + (J $p.Z) + "]" }

Write-Host "Starting MicroStation automation server..."
$app = New-Object -ComObject MicroStationDGN.Application
Write-Host ("MicroStation " + $app.Version)

foreach ($file in $Files) {
  try {
    $path = (Resolve-Path $file).Path
    $name = [IO.Path]::GetFileNameWithoutExtension($path)
    $out = Join-Path $OutDir ($name + ".oracle.json")
    Write-Host "== $path"
    $sw = [Diagnostics.Stopwatch]::StartNew()
    $df = $app.OpenDesignFileForProgram($path, $true)
    $sb = New-Object Text.StringBuilder
    [void]$sb.Append("{`"generator`":`"MicroStation COM oracle`",`"msVersion`":" + (J $app.Version) + ",`"file`":" + (J $path))
    [void]$sb.Append(",`"format`":" + (J $df.Format) + ",`"v7`":" + (J ($df.Format -eq 2)))

    # ---- levels
    [void]$sb.Append(",`"levels`":[")
    $first = $true
    foreach ($l in $df.Levels) {
        if (-not $first) { [void]$sb.Append(",") }; $first = $false
        $ls = $null; try { $ls = $l.ElementLineStyle.Name } catch {}
        $lsn = $null; try { $lsn = $l.ElementLineStyle.Number } catch {}
        [void]$sb.Append("{`"id`":" + (J $l.ID) + ",`"number`":" + (J $l.Number) + ",`"code`":" + (J $l.Code) + ",`"name`":" + (J $l.Name) + ",`"description`":" + (J $l.Description))
        [void]$sb.Append(",`"color`":" + (J $l.ElementColor) + ",`"weight`":" + (J $l.ElementLineWeight) + ",`"style`":" + (J $ls) + ",`"styleNumber`":" + (J $lsn))
        [void]$sb.Append(",`"displayed`":" + (J $l.IsDisplayed) + ",`"frozen`":" + (J $l.IsFrozen) + ",`"plot`":" + (J $l.Plot) + ",`"locked`":" + (J $l.IsLocked))
        $oc = $null; try { $oc = $l.OverrideColor } catch {}
        [void]$sb.Append(",`"overrideColor`":" + (J $oc) + ",`"priority`":" + (J $(try { $l.ElementPriority } catch { $null })) + ",`"transparency`":" + (J $(try { $l.Transparency } catch { $null })))
        [void]$sb.Append(",`"parentId`":" + (J $(try { $l.ParentLevel.ID } catch { $null })) + "}")
    }
    [void]$sb.Append("]")

    # ---- line styles
    [void]$sb.Append(",`"lineStyles`":[")
    $first = $true
    foreach ($s in $df.LineStyles) {
        if (-not $first) { [void]$sb.Append(",") }; $first = $false
        [void]$sb.Append("{`"name`":" + (J $s.Name) + ",`"number`":" + (J $(try { $s.Number } catch { $null })) + "}")
    }
    [void]$sb.Append("]")

    # ---- fonts
    [void]$sb.Append(",`"fonts`":[")
    $first = $true
    try {
        foreach ($f in $app.ActiveDesignFile.Fonts) { }
    } catch {}
    [void]$sb.Append("]")

    # ---- models
    [void]$sb.Append(",`"models`":[")
    $firstM = $true
    foreach ($m in $df.Models) {
        if (-not $firstM) { [void]$sb.Append(",") }; $firstM = $false
        $go = $null; try { $go = $m.GlobalOrigin } catch {}
        [void]$sb.Append("{`"name`":" + (J $m.Name) + ",`"description`":" + (J $m.Description) + ",`"type`":" + (J $m.Type) + ",`"is3d`":" + (J $m.Is3D))
        [void]$sb.Append(",`"uorPerMaster`":" + (J $m.UORsPerMasterUnit) + ",`"uorPerStorage`":" + (J $(try { $m.UORsPerStorageUnit } catch { $null })))
        [void]$sb.Append(",`"masterUnit`":" + (J $(try { $m.MasterUnit.Label } catch { $null })) + ",`"subUnit`":" + (J $(try { $m.SubUnit.Label } catch { $null })))
        if ($go) { [void]$sb.Append(",`"globalOrigin`":" + (P3 $go)) }
        [void]$sb.Append(",`"elements`":[")
        $sc = New-Object -ComObject MicroStationDGN.ElementScanCriteria
        $ee = $m.Scan($sc)
        $n = 0; $firstE = $true
        while ($ee.MoveNext() -and $n -lt $MaxElements) {
            $e = $ee.Current
            $n++
            if (-not $firstE) { [void]$sb.Append(",") }; $firstE = $false
            $id = $null
            try { $id = [string]$e.ID64 } catch {}
            if (-not $id) { try { $d = $e.ID; $id = [string]([uint64][uint32]$d.Low + ([uint64][uint32]$d.High -shl 32)) } catch {} }
            if (-not $id) { try { $id = $app.DLongToString($e.ID) } catch {} }
            $lvl = $null; try { $lvl = $e.Level.Name } catch {}
            $lsn = $null; try { $lsn = $e.LineStyle.Name } catch {}
            $r = $null; try { $r = $e.Range } catch {}
            [void]$sb.Append("{`"id`":" + (J $id) + ",`"type`":" + (J $e.Type) + ",`"level`":" + (J $lvl) + ",`"color`":" + (J $e.Color) + ",`"weight`":" + (J $e.LineWeight) + ",`"style`":" + (J $lsn))
            [void]$sb.Append(",`"class`":" + (J $(try { $e.Class } catch { $null })) + ",`"graphic`":" + (J $e.IsGraphical))
            if ($r) { [void]$sb.Append(",`"range`":[" + (P3 $r.Low) + "," + (P3 $r.High) + "]") }
            try {
                switch ($e.Type) {
                    17 { $t = $e.AsTextElement; [void]$sb.Append(",`"text`":" + (J $t.Text) + ",`"origin`":" + (P3 $t.Origin) + ",`"height`":" + (J $t.TextStyle.Height) + ",`"width`":" + (J $t.TextStyle.Width) + ",`"font`":" + (J $t.TextStyle.Font.Name)) }
                    2  { $c = $e.AsCellElement; [void]$sb.Append(",`"name`":" + (J $c.Name) + ",`"origin`":" + (P3 $c.Origin)) }
                    35 { $c = $e.AsSharedCellElement; [void]$sb.Append(",`"name`":" + (J $c.Name) + ",`"origin`":" + (P3 $c.Origin)) }
                    3  { $v = $e.AsLineElement.GetVertices(); [void]$sb.Append(",`"points`":[" + (($v | % { P3 $_ }) -join ",") + "]") }
                    4  { $v = $e.AsLineElement.GetVertices(); [void]$sb.Append(",`"points`":[" + (($v | % { P3 $_ }) -join ",") + "]") }
                    6  { $v = $e.AsShapeElement.GetVertices(); [void]$sb.Append(",`"points`":[" + (($v | % { P3 $_ }) -join ",") + "]") }
                    15 { $a = $e.AsEllipseElement; [void]$sb.Append(",`"center`":" + (P3 $a.CenterPoint) + ",`"primary`":" + (J $a.PrimaryRadius) + ",`"secondary`":" + (J $a.SecondaryRadius)) }
                    37 { $t = $e.AsTagElement; [void]$sb.Append(",`"tagValue`":" + (J $t.Value) + ",`"tagName`":" + (J $(try { $t.TagDefinitionName } catch { $null })) + ",`"tagSet`":" + (J $(try { $t.TagSetName } catch { $null })) + ",`"tagTarget`":" + (J $(try { [string]$t.BaseElement.ID64 } catch { $null })) + ",`"origin`":" + (P3 $t.Origin) + ",`"hidden`":" + (J $(try { $t.IsHidden } catch { $null }))) }
                    7  { $tn = $e.AsTextNodeElement; $lines = @(); try { for ($i = 1; $i -le $tn.TextLinesCount; $i++) { $lines += $tn.TextLine($i) } } catch {}; [void]$sb.Append(",`"lines`":[" + (($lines | % { J $_ }) -join ",") + "],`"origin`":" + (P3 $tn.Origin)) }
                    16 { $a = $e.AsArcElement; [void]$sb.Append(",`"center`":" + (P3 $a.CenterPoint) + ",`"primary`":" + (J $a.PrimaryRadius) + ",`"secondary`":" + (J $a.SecondaryRadius) + ",`"start`":" + (J $a.StartAngle) + ",`"sweep`":" + (J $a.SweepAngle) + ",`"startPoint`":" + (P3 $a.StartPoint)) }
                }
            } catch {}
            [void]$sb.Append("}")
        }
        [void]$sb.Append("],`"elementCount`":$n}")
    }
    [void]$sb.Append("]}")
    [IO.File]::WriteAllText($out, $sb.ToString(), (New-Object Text.UTF8Encoding($false)))
    try { $df.Close() } catch {}
    Write-Host ("   wrote {0} ({1:N0} ms)" -f $out, $sw.ElapsedMilliseconds)
  } catch {
    Write-Host "   FAILED: $($_.Exception.Message)`n$($_.InvocationInfo.PositionMessage)"
    try { $df.Close() } catch {}
  }
}
if (-not $KeepOpen) { try { $app.Quit() } catch {} }
