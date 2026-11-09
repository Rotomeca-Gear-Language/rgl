<#
.SYNOPSIS
Compte les allocations et les copies profondes de chaque banc de bench/, et les classe.

.DESCRIPTION
Compile chaque banc avec --count-allocations (dans out/<profil>-compteurs), le lance une fois, vérifie sa sortie (expected.txt)
et lit la ligne « rgl-compteurs : … » qu'il écrit sur la sortie d'erreur. Écrit un tableau Markdown et un fichier JSON dans
bench/results/ (compteurs-<mode>.md et .json), triés par octets alloués décroissants : c'est ce classement, pas une liste de
techniques, qui décide de l'ordre des optimisations de mémoire.

Un banc qui n'utilise pas la mémoire du runtime (aucun tableau ni texte long) n'écrit pas de ligne : il est listé à zéro.

.PARAMETER Mode
Profil de compilation : release (par défaut) ou debug.

.PARAMETER Only
Ne traite que ces bancs.

.PARAMETER NoBuild
Ne reconstruit pas le compilateur.

.EXAMPLE
./bench/compteurs.ps1
./bench/compteurs.ps1 -Only json,chaines -Mode debug
#>
[CmdletBinding()]
param(
    [ValidateSet('release', 'debug')][string]$Mode = 'release',
    [string[]]$Only,
    [switch]$NoBuild
)

$ErrorActionPreference = 'Stop'

$bench = $PSScriptRoot
$root = Split-Path $bench -Parent
$rgl = Join-Path $root 'bin\Release\net10.0\rgl.exe'

if (-not $NoBuild) {
    Write-Host 'Construction du compilateur (Release)...'
    & dotnet build (Join-Path $root 'rgl.csproj') -c Release --nologo -v q | Out-Null
    if ($LASTEXITCODE -ne 0) { throw 'La construction du compilateur a échoué.' }
}
if (-not (Test-Path $rgl)) { throw "Compilateur introuvable : $rgl" }

$names = @(Get-ChildItem $bench -Directory | Where-Object { Test-Path (Join-Path $_.FullName 'main.rgl') } | ForEach-Object { $_.Name } | Sort-Object)
if ($Only) {
    $unknown = @($Only | Where-Object { $names -notcontains $_ })
    if ($unknown.Count -gt 0) { throw ('Bancs inconnus : ' + ($unknown -join ', ')) }
    $names = @($names | Where-Object { $Only -contains $_ })
}

$fields = 'allocations', 'octets', 'reallocations', 'liberations', 'copies_tableaux', 'elements_copies', 'copies_textes', 'octets_textes'
$results = @()
foreach ($name in $names) {
    $directory = Join-Path $bench $name
    Write-Host "== $name"

    $arguments = @('compile', 'main.rgl', '--count-allocations')
    if ($Mode -eq 'release') { $arguments += '--release' }
    Push-Location $directory
    try {
        $previous = $ErrorActionPreference
        $ErrorActionPreference = 'Continue'
        $log = & $rgl @arguments 2>&1 | Out-String
        $compileCode = $LASTEXITCODE
        $ErrorActionPreference = $previous
    }
    finally { Pop-Location }

    $exe = Join-Path $directory "out\$Mode-compteurs\$name.exe"
    if ($compileCode -ne 0 -or -not (Test-Path $exe)) {
        Write-Warning "$name : la compilation a échoué."
        Write-Host $log
        $results += [pscustomobject]@{ Name = $name; Ok = $false; Reason = 'compilation' }
        continue
    }

    $scratch = Join-Path ([System.IO.Path]::GetTempPath()) ('rgl-count-' + [guid]::NewGuid().ToString('N'))
    New-Item -ItemType Directory -Path $scratch | Out-Null
    try {
        $info = New-Object System.Diagnostics.ProcessStartInfo
        $info.FileName = $exe
        $info.WorkingDirectory = $scratch
        $info.UseShellExecute = $false
        $info.RedirectStandardOutput = $true
        $info.RedirectStandardError = $true
        $info.CreateNoWindow = $true
        $process = [System.Diagnostics.Process]::Start($info)
        $out = $process.StandardOutput.ReadToEndAsync()
        $err = $process.StandardError.ReadToEndAsync()
        if (-not $process.WaitForExit(120000)) { $process.Kill(); throw "$name n'a pas fini en 120 s." }
        $out.Wait(); $err.Wait()
    }
    finally { Remove-Item -Recurse -Force $scratch -ErrorAction SilentlyContinue }

    $expected = [System.IO.File]::ReadAllText((Join-Path $directory 'expected.txt'), [System.Text.Encoding]::UTF8).Replace("`r`n", "`n")
    if ($process.ExitCode -ne 0 -or $out.Result.Replace("`r`n", "`n") -ne $expected) {
        Write-Warning "$name : sortie ou code de retour différents de ceux attendus."
        $results += [pscustomobject]@{ Name = $name; Ok = $false; Reason = 'sortie' }
        continue
    }

    $values = @{}
    foreach ($f in $fields) { $values[$f] = [int64]0 }
    $line = ($err.Result -split "`r?`n" | Where-Object { $_ -like 'rgl-compteurs :*' } | Select-Object -Last 1)
    if ($line) {
        foreach ($f in $fields) {
            if ($line -match "(?:^|\s)$f=(\d+)") { $values[$f] = [int64]$Matches[1] }
        }
    }
    $results += [pscustomobject]@{
        Name = $name; Ok = $true; Reason = ''
        Allocations = $values['allocations']; Octets = $values['octets']; Reallocations = $values['reallocations']; Liberations = $values['liberations']
        CopiesTableaux = $values['copies_tableaux']; ElementsCopies = $values['elements_copies']; CopiesTextes = $values['copies_textes']; OctetsTextes = $values['octets_textes']
    }
    Write-Host ("   {0} allocations, {1} Ko, {2} copies de tableaux ({3} éléments)" -f $values['allocations'], [math]::Round($values['octets'] / 1024), $values['copies_tableaux'], $values['elements_copies'])
}

$commit = ''
try {
    $commit = (& git -C $root rev-parse --short HEAD).Trim()
    if ((& git -C $root status --porcelain)) { $commit += '+modifié' }
}
catch { $commit = 'inconnu' }

$ranked = @($results | Where-Object { $_.Ok } | Sort-Object -Property Octets -Descending)
$lines = @("# Compteurs de mémoire des bancs ($Mode)", '', ('Date : {0} · commit : {1}' -f (Get-Date).ToString('yyyy-MM-dd HH:mm'), $commit), '')
$lines += '| Banc | Allocations | Octets (Ko) | Réallocations | Copies de tableaux | Éléments copiés | Copies de textes | Octets de textes copiés |'
$lines += '|---|---:|---:|---:|---:|---:|---:|---:|'
foreach ($r in $ranked) {
    $lines += ('| {0} | {1} | {2} | {3} | {4} | {5} | {6} | {7} |' -f $r.Name, $r.Allocations, [math]::Round($r.Octets / 1024), $r.Reallocations, $r.CopiesTableaux, $r.ElementsCopies, $r.CopiesTextes, $r.OctetsTextes)
}
foreach ($r in ($results | Where-Object { -not $_.Ok })) { $lines += ('| {0} | ÉCHEC ({1}) | | | | | | |' -f $r.Name, $r.Reason) }
$lines += ''
$lines += 'Trié par octets alloués. Seuls les blocs du système (tas) sont comptés ; le pool et les arènes ont leurs propres allocateurs. Une copie profonde est un tableau ou un texte recopié élément par élément (les textes courts ou partagés par compteur ne comptent pas). Un banc sans tableau ni texte long n''écrit rien : il est à zéro.'
$table = $lines -join "`n"
Write-Host ''
Write-Host $table

$folder = Join-Path $bench 'results'
New-Item -ItemType Directory -Path $folder -Force | Out-Null
$utf8 = New-Object System.Text.UTF8Encoding($false)
[System.IO.File]::WriteAllText((Join-Path $folder "compteurs-$Mode.md"), $table + "`n", $utf8)
[System.IO.File]::WriteAllText((Join-Path $folder "compteurs-$Mode.json"), ([ordered]@{ Commit = $commit; Mode = $Mode; Results = $results } | ConvertTo-Json -Depth 4), $utf8)

if ($results | Where-Object { -not $_.Ok }) { exit 1 }
