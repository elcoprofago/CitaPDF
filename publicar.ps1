# Publica una release de CitaPDF en GitHub: compila la versión portable
# autocontenida, arma el zip, crea la etiqueta v<Version> y la release.
#
#   .\publicar.ps1 -Notas "Qué cambió"            # publica
#   .\publicar.ps1 -Notas "..." -SoloProbar       # todo menos etiqueta/push/release
#
# Se niega a publicar si: hay cambios sin commitear, no se está en master, la
# etiqueta ya existe (local o en GitHub), o la versión no es mayor que la
# última publicada. La versión sale de <Version> en CitaPDF.csproj y se
# verifica contra la que quedó grabada en el .exe.
#
# Los valores de publicación (autocontenido, un solo archivo, win-x64) van
# explícitos acá y no se toman del .pubxml: el diálogo de Visual Studio
# reescribe ese perfil y ya lo dejó una vez en SelfContained=false.

param(
    [Parameter(Mandatory = $true)][string]$Notas,
    [switch]$SoloProbar
)

$ErrorActionPreference = 'Stop'
Set-Location $PSScriptRoot

function Fallar($msg) { Write-Host "ERROR: $msg" -ForegroundColor Red; exit 1 }
function Paso($msg) { Write-Host "-- $msg" -ForegroundColor Cyan }

# ---------- Versión ----------
[xml]$csproj = Get-Content (Join-Path $PSScriptRoot 'CitaPDF.csproj') -Raw -Encoding UTF8
$version = ($csproj.Project.PropertyGroup | ForEach-Object { $_.Version } | Where-Object { $_ }) | Select-Object -First 1
if (-not $version -or $version -notmatch '^\d+\.\d+\.\d+$') { Fallar "No se encontró <Version>x.y.z</Version> en CitaPDF.csproj." }
$etiqueta = "v$version"
Paso "Versión $version (etiqueta $etiqueta)"

# ---------- Estado de git ----------
$cambios = git status --porcelain
if ($LASTEXITCODE -ne 0) { Fallar "git status falló." }
if ($cambios) { Fallar "Hay cambios sin commitear:`n$($cambios -join "`n")" }

$rama = git rev-parse --abbrev-ref HEAD
if ($rama -ne 'master') { Fallar "Hay que publicar desde master (rama actual: $rama)." }

if (git tag --list $etiqueta) { Fallar "La etiqueta $etiqueta ya existe localmente." }
$remotas = git ls-remote --tags origin
if ($LASTEXITCODE -ne 0) { Fallar "No se pudo consultar las etiquetas de origin." }
if ($remotas -match "refs/tags/$([regex]::Escape($etiqueta))$") { Fallar "La etiqueta $etiqueta ya existe en GitHub." }

$publicadas = @($remotas | ForEach-Object { if ($_ -match 'refs/tags/v(\d+\.\d+\.\d+)$') { [version]$Matches[1] } })
if ($publicadas.Count -gt 0) {
    $ultima = ($publicadas | Sort-Object | Select-Object -Last 1)
    if ([version]$version -le $ultima) { Fallar "La versión $version no es mayor que la última publicada ($ultima). Subí <Version> en CitaPDF.csproj." }
}

# ---------- Compilación ----------
# Carpeta propia del script, dentro de bin\: se regenera en cada corrida.
$base = Join-Path $PSScriptRoot "bin\Release\release\$etiqueta"
$staging = Join-Path $base 'CitaPDF'
$zip = Join-Path $base "CitaPDF-$version-win-x64.zip"
if (Test-Path $base) { Remove-Item -LiteralPath $base -Recurse -Force }

Paso "Publicando en $staging"
dotnet publish (Join-Path $PSScriptRoot 'CitaPDF.csproj') -c Release -r win-x64 --self-contained true `
    -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true `
    -p:PublishDir="$staging\" -nologo -v q
if ($LASTEXITCODE -ne 0) { Fallar "dotnet publish falló (código $LASTEXITCODE)." }

# ---------- Verificación del resultado ----------
$exe = Join-Path $staging 'CitaPDF.exe'
if (-not (Test-Path $exe)) { Fallar "No se generó CitaPDF.exe." }
$fileVersion = (Get-Item $exe).VersionInfo.FileVersion
if ($fileVersion -ne "$version.0") { Fallar "El .exe quedó con versión $fileVersion, se esperaba $version.0." }
if (-not (Test-Path (Join-Path $staging 'datos\LEEME.txt'))) { Fallar "Falta datos\LEEME.txt (sin carpeta datos no hay modo portable)." }
# El .pdb no se distribuye.
Get-ChildItem $staging -Filter *.pdb | Remove-Item -Force
$sueltos = Get-ChildItem $staging -Recurse -File | Where-Object { $_.Name -notin 'CitaPDF.exe', 'LEEME.txt' }
if ($sueltos) { Fallar "Archivos inesperados en la publicación: $($sueltos.Name -join ', ')" }

Paso "Armando $zip"
Add-Type -AssemblyName System.IO.Compression.FileSystem
# Entrada por entrada: CreateFromDirectory de .NET Framework (PowerShell 5.1)
# graba las rutas con "\", que otros descompresores no toman como carpeta.
$archivo = [IO.Compression.ZipFile]::Open($zip, 'Create')
try {
    foreach ($f in Get-ChildItem $staging -Recurse -File) {
        $nombre = 'CitaPDF/' + $f.FullName.Substring($staging.Length + 1).Replace('\', '/')
        [void][IO.Compression.ZipFileExtensions]::CreateEntryFromFile($archivo, $f.FullName, $nombre, 'Optimal')
    }
} finally { $archivo.Dispose() }
$entradas = [IO.Compression.ZipFile]::OpenRead($zip)
try { $nombres = @($entradas.Entries | ForEach-Object { $_.FullName }) } finally { $entradas.Dispose() }
if ($nombres -match '\.json$') { Fallar "El zip contiene un .json (¿datos personales?): $($nombres -join ', ')" }
foreach ($esperado in 'CitaPDF/CitaPDF.exe', 'CitaPDF/datos/LEEME.txt') {
    if ($nombres -notcontains $esperado) { Fallar "Falta $esperado en el zip (contiene: $($nombres -join ', '))." }
}
Paso "Zip verificado: $($nombres -join ', ') ($([math]::Round((Get-Item $zip).Length / 1MB, 1)) MB)"

if ($SoloProbar) {
    Paso "SoloProbar: no se crea etiqueta, ni push, ni release."
    exit 0
}

# ---------- Etiqueta, push y release ----------
Paso "Etiqueta y push"
git tag -a $etiqueta -m "CitaPDF $version"
if ($LASTEXITCODE -ne 0) { Fallar "No se pudo crear la etiqueta." }
git push origin master $etiqueta
if ($LASTEXITCODE -ne 0) { Fallar "git push falló (la etiqueta $etiqueta quedó solo local: 'git tag -d $etiqueta' para reintentar)." }

Paso "Release en GitHub"
gh release create $etiqueta $zip --title "CitaPDF $version" --notes $Notas
if ($LASTEXITCODE -ne 0) { Fallar "gh release create falló (la etiqueta ya está en GitHub; reintentar solo este paso)." }

$assets = gh release view $etiqueta --json assets --jq '.assets[].name'
if ($LASTEXITCODE -ne 0 -or $assets -notcontains (Split-Path $zip -Leaf)) { Fallar "La release se creó pero no se verificó el zip adjunto." }
Paso "Publicada $etiqueta con $(Split-Path $zip -Leaf)"
