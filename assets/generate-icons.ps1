# Génère les icônes Windows (.ico) du compilateur à partir des SVG, avec Inkscape :
#   rgl.svg      -> rgl.ico       icône de rgl.exe
#   rgl-item.svg -> rgl-item.ico  icône par défaut des programmes compilés (intégrée au compilateur)
# Chaque taille est rendue depuis le SVG (pas une réduction d'une grande image) : le dessin reste
# net à 16 pixels comme à 256. Le .ico contient une image PNG par taille (Windows Vista et après).
#
# Usage, depuis n'importe quel dossier :  powershell -File assets/generate-icons.ps1

param([string]$Inkscape = "C:\Program Files\Inkscape\bin\inkscape.exe")

$ErrorActionPreference = "Stop"
$sizes = 256, 48, 32, 16
$assets = $PSScriptRoot

function New-Icon([string]$svg, [string]$ico) {
    $temp = Join-Path ([System.IO.Path]::GetTempPath()) ("rgl-icons-" + [guid]::NewGuid())
    New-Item -ItemType Directory $temp | Out-Null
    try {
        $images = foreach ($size in $sizes) {
            $png = Join-Path $temp "$size.png"
            & $Inkscape --export-type=png --export-area-page --export-width=$size --export-height=$size --export-filename=$png $svg 2>$null | Out-Null
            if (-not (Test-Path $png)) { throw "Inkscape n'a pas produit $png" }
            # La virgule garde chaque PNG entier : sans elle, PowerShell aplatirait les octets en un seul tableau.
            ,[System.IO.File]::ReadAllBytes($png)
        }

        # En-tête ICONDIR, puis une entrée ICONDIRENTRY par image, puis les images.
        $stream = New-Object System.IO.MemoryStream
        $writer = New-Object System.IO.BinaryWriter $stream
        $writer.Write([uint16]0); $writer.Write([uint16]1); $writer.Write([uint16]$sizes.Count)
        $offset = 6 + 16 * $sizes.Count
        for ($i = 0; $i -lt $sizes.Count; $i++) {
            $side = if ($sizes[$i] -ge 256) { 0 } else { $sizes[$i] }   # 0 signifie 256
            $writer.Write([byte]$side); $writer.Write([byte]$side)
            $writer.Write([byte]0); $writer.Write([byte]0)               # pas de palette
            $writer.Write([uint16]1); $writer.Write([uint16]32)         # plans, bits par pixel
            $writer.Write([uint32]$images[$i].Length); $writer.Write([uint32]$offset)
            $offset += $images[$i].Length
        }
        foreach ($image in $images) { $writer.Write($image) }
        $writer.Flush()
        [System.IO.File]::WriteAllBytes($ico, $stream.ToArray())
    }
    finally {
        Remove-Item -Recurse -Force $temp
    }
}

New-Icon (Join-Path $assets "rgl.svg") (Join-Path $assets "rgl.ico")
New-Icon (Join-Path $assets "rgl-item.svg") (Join-Path $assets "rgl-item.ico")
Write-Output "Icônes générées dans $assets"
