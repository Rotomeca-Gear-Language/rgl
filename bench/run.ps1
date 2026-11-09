<#
.SYNOPSIS
Mesure les programmes de bench/ : temps d'exécution, taille de l'exécutable, temps de compilation.

.DESCRIPTION
Pour chaque dossier de bench/ qui contient un main.rgl : compile avec le compilateur construit en Release,
lance l'exécutable une fois pour vérifier sa sortie (expected.txt) puis -Runs fois pour mesurer, et écrit
un tableau Markdown et un fichier JSON dans bench/results/.

Un banc dont la sortie diffère de expected.txt est en échec : la mesure d'un programme faux n'a aucun sens,
et c'est aussi ce qui servira de test différentiel quand une optimisation change la façon d'exécuter.

.PARAMETER Runs
Nombre de lancements mesurés par banc (après un lancement de chauffe).

.PARAMETER Mode
Profil de compilation : release (par défaut) ou debug.

.PARAMETER Only
Ne mesure que ces bancs (demarrage est toujours mesuré : il sert de référence de lancement).

.PARAMETER NoBuild
Ne reconstruit pas le compilateur.

.PARAMETER Baseline
Fichier JSON d'une mesure précédente : ajoute la colonne « vs base » (rapport des médianes).

.PARAMETER Output
Chemin du fichier JSON écrit (le tableau Markdown est écrit à côté). Par défaut bench/results/latest-<mode>.json.

.EXAMPLE
./bench/run.ps1
./bench/run.ps1 -Only particules,parcours -Runs 15
./bench/run.ps1 -Baseline bench/results/base-release.json
#>
[CmdletBinding()]
param(
    [int]$Runs = 7,
    [ValidateSet('release', 'debug')][string]$Mode = 'release',
    [string[]]$Only,
    [switch]$NoBuild,
    [string]$Baseline,
    [string]$Output
)

$ErrorActionPreference = 'Stop'

$bench = $PSScriptRoot
$root = Split-Path $bench -Parent
$rgl = Join-Path $root 'bin\Release\net10.0\rgl.exe'
$timeoutMs = 120000

if (-not $NoBuild) {
    Write-Host 'Construction du compilateur (Release)...'
    & dotnet build (Join-Path $root 'rgl.csproj') -c Release --nologo -v q | Out-Null
    if ($LASTEXITCODE -ne 0) { throw 'La construction du compilateur a échoué.' }
}
if (-not (Test-Path $rgl)) { throw "Compilateur introuvable : $rgl" }

# Les bancs à mesurer, demarrage en premier : sa médiane est la référence de lancement.
$names = @(Get-ChildItem $bench -Directory | Where-Object { Test-Path (Join-Path $_.FullName 'main.rgl') } | ForEach-Object { $_.Name } | Sort-Object)
if ($Only) {
    $unknown = @($Only | Where-Object { $names -notcontains $_ })
    if ($unknown.Count -gt 0) { throw ('Bancs inconnus : ' + ($unknown -join ', ')) }
    $names = @($names | Where-Object { $Only -contains $_ -or $_ -eq 'demarrage' })
}
$names = @($names | Where-Object { $_ -eq 'demarrage' }) + @($names | Where-Object { $_ -ne 'demarrage' })

function Invoke-Once([string]$exe, [string]$cwd) {
    $info = New-Object System.Diagnostics.ProcessStartInfo
    $info.FileName = $exe
    $info.WorkingDirectory = $cwd
    $info.UseShellExecute = $false
    $info.RedirectStandardOutput = $true
    $info.CreateNoWindow = $true

    $watch = [System.Diagnostics.Stopwatch]::StartNew()
    $process = [System.Diagnostics.Process]::Start($info)
    $text = $process.StandardOutput.ReadToEndAsync()
    if (-not $process.WaitForExit($timeoutMs)) {
        $process.Kill()
        throw "« $exe » n'a pas fini en $($timeoutMs / 1000) s."
    }
    $text.Wait()
    $watch.Stop()
    return [pscustomobject]@{ Ms = $watch.Elapsed.TotalMilliseconds; Code = $process.ExitCode; Output = $text.Result }
}

function Get-Median([double[]]$values) {
    $sorted = @($values | Sort-Object)
    $middle = [int][math]::Floor($sorted.Count / 2)
    if ($sorted.Count % 2 -eq 1) { return $sorted[$middle] }
    return ($sorted[$middle - 1] + $sorted[$middle]) / 2
}

$results = @()
foreach ($name in $names) {
    $directory = Join-Path $bench $name
    Write-Host "== $name"

    Push-Location $directory
    try {
        $compileArguments = @('compile', 'main.rgl')
        if ($Mode -eq 'release') { $compileArguments += '--release' }
        # Les diagnostics du compilateur (notices, avertissements) ne sont montrés que si la compilation échoue.
        $previous = $ErrorActionPreference
        $ErrorActionPreference = 'Continue'
        $watch = [System.Diagnostics.Stopwatch]::StartNew()
        $log = & $rgl @compileArguments 2>&1 | Out-String
        $compileCode = $LASTEXITCODE
        $watch.Stop()
        $ErrorActionPreference = $previous
    }
    finally { Pop-Location }

    $exe = Join-Path $directory "out\$Mode\$name.exe"
    if (-not (Test-Path $exe)) { $exe = Join-Path $directory "out/$Mode/$name" }
    if ($compileCode -ne 0 -or -not (Test-Path $exe)) {
        Write-Warning "$name : la compilation a échoué."
        Write-Host $log
        $results += [pscustomobject]@{ Name = $name; Ok = $false; Reason = 'compilation'; CompileSeconds = [math]::Round($watch.Elapsed.TotalSeconds, 1) }
        continue
    }

    # Dossier de travail propre : un banc qui écrit un fichier ne pollue pas le dépôt.
    $scratch = Join-Path ([System.IO.Path]::GetTempPath()) ('rgl-bench-' + [guid]::NewGuid().ToString('N'))
    New-Item -ItemType Directory -Path $scratch | Out-Null
    try {
        $expected = [System.IO.File]::ReadAllText((Join-Path $directory 'expected.txt'), [System.Text.Encoding]::UTF8).Replace("`r`n", "`n")

        $warm = Invoke-Once $exe $scratch
        $actual = $warm.Output.Replace("`r`n", "`n")
        if ($warm.Code -ne 0 -or $actual -ne $expected) {
            Write-Warning "$name : sortie ou code de retour différents de ceux attendus (code $($warm.Code))."
            $results += [pscustomobject]@{ Name = $name; Ok = $false; Reason = 'sortie'; CompileSeconds = [math]::Round($watch.Elapsed.TotalSeconds, 1) }
            continue
        }

        $times = @()
        for ($i = 0; $i -lt $Runs; $i++) { $times += (Invoke-Once $exe $scratch).Ms }
    }
    finally { Remove-Item -Recurse -Force $scratch -ErrorAction SilentlyContinue }

    $median = Get-Median $times
    $results += [pscustomobject]@{
        Name           = $name
        Ok             = $true
        Reason         = ''
        MedianMs       = [math]::Round($median, 1)
        MinMs          = [math]::Round(($times | Measure-Object -Minimum).Minimum, 1)
        MaxMs          = [math]::Round(($times | Measure-Object -Maximum).Maximum, 1)
        SizeBytes      = (Get-Item $exe).Length
        CompileSeconds = [math]::Round($watch.Elapsed.TotalSeconds, 1)
    }
    Write-Host ("   médiane {0} ms (min {1}, max {2}), {3} Ko" -f $results[-1].MedianMs, $results[-1].MinMs, $results[-1].MaxMs, [math]::Round($results[-1].SizeBytes / 1024))
}

# Le coût de lancement est retiré pour donner le temps de travail du programme.
$startup = $results | Where-Object { $_.Name -eq 'demarrage' -and $_.Ok } | Select-Object -First 1
$startupMs = if ($startup) { $startup.MedianMs } else { $null }

$base = @{}
if ($Baseline) {
    foreach ($entry in (Get-Content $Baseline -Raw -Encoding UTF8 | ConvertFrom-Json).Results) { $base[$entry.Name] = $entry }
}

# Contexte de la mesure : sans lui deux séries de chiffres ne se comparent pas.
$commit = ''
try {
    $commit = (& git -C $root rev-parse --short HEAD).Trim()
    if ((& git -C $root status --porcelain)) { $commit += '+modifié' }
}
catch { $commit = 'inconnu' }
$cpu = 'inconnu'
try { $cpu = (Get-CimInstance Win32_Processor | Select-Object -First 1).Name.Trim() } catch { }
$zig = ''
try { $zig = (Get-Content (Join-Path $root 'tools\toolchain.json') -Raw | ConvertFrom-Json).zig } catch { }

$context = [ordered]@{
    Date    = (Get-Date).ToString('yyyy-MM-dd HH:mm')
    Commit  = $commit
    Mode    = $Mode
    Runs    = $Runs
    Cpu     = $cpu
    Coeurs  = [Environment]::ProcessorCount
    Os      = [Environment]::OSVersion.VersionString
    Zig     = $zig
}

$lines = @()
$lines += "# Mesures des bancs ($Mode)"
$lines += ''
$lines += ('Date : {0} · commit : {1} · {2} lancements · {3} ({4} cœurs logiques) · Zig {5}' -f $context.Date, $context.Commit, $Runs, $cpu, $context.Coeurs, $zig)
$lines += ''
$lines += '| Banc | Médiane (ms) | Min (ms) | Travail (ms) | Taille (Ko) | Compilation (s) | vs base | Sortie |'
$lines += '|---|---:|---:|---:|---:|---:|---:|---|'
foreach ($r in $results) {
    if (-not $r.Ok) {
        $lines += ('| {0} | | | | | {1} | | ÉCHEC ({2}) |' -f $r.Name, $r.CompileSeconds, $r.Reason)
        continue
    }
    $work = if ($null -ne $startupMs -and $r.Name -ne 'demarrage') { [math]::Round([math]::Max(0, $r.MedianMs - $startupMs), 1) } else { $null }
    $versus = ''
    if ($base.ContainsKey($r.Name) -and $base[$r.Name].MedianMs) {
        $ratio = $r.MedianMs / $base[$r.Name].MedianMs
        $versus = ('×{0:N2}' -f $ratio)
        if ($ratio -lt 0.9) { $versus += ' (plus rapide)' } elseif ($ratio -gt 1.1) { $versus += ' (plus lent)' }
    }
    $lines += ('| {0} | {1} | {2} | {3} | {4} | {5} | {6} | ok |' -f $r.Name, $r.MedianMs, $r.MinMs, $work, [math]::Round($r.SizeBytes / 1024), $r.CompileSeconds, $versus)
}
$lines += ''
$lines += 'Travail = médiane moins celle de demarrage (coût de lancement du processus).'
$table = $lines -join "`n"
Write-Host ''
Write-Host $table

$resultsFolder = Join-Path $bench 'results'
New-Item -ItemType Directory -Path $resultsFolder -Force | Out-Null
if (-not $Output) { $Output = Join-Path $resultsFolder "latest-$Mode.json" }
$report = [ordered]@{ Context = $context; StartupMs = $startupMs; Results = $results }
[System.IO.File]::WriteAllText($Output, ($report | ConvertTo-Json -Depth 5), (New-Object System.Text.UTF8Encoding($false)))
[System.IO.File]::WriteAllText([System.IO.Path]::ChangeExtension($Output, '.md'), $table + "`n", (New-Object System.Text.UTF8Encoding($false)))
Write-Host ''
Write-Host "Résultats écrits dans $Output"

if ($results | Where-Object { -not $_.Ok }) { exit 1 }
