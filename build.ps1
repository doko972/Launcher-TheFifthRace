<#
.SYNOPSIS
    Compile SGWLauncher.exe avec le compilateur C# du .NET Framework 4.8.

.DESCRIPTION
    Aucun outil a installer : csc.exe est livre avec Windows
    (C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe).

    Les assemblies WebView2 (lib\), la page d'interface et les polices (ui\) sont
    embarquees dans l'executable. Le resultat est un fichier unique a distribuer :
    il ne depend que du runtime WebView2, que le launcher propose d'installer s'il
    est absent.

.EXAMPLE
    .\build.ps1
    .\build.ps1 -OutFile ..\..\dist\SGWLauncher.exe
#>
[CmdletBinding()]
param(
    [string]$OutFile  = (Join-Path $PSScriptRoot 'build\SGWLauncher.exe'),
    [string]$IconFile = (Join-Path $PSScriptRoot 'sgw.ico')
)

$ErrorActionPreference = 'Stop'

# --- Localiser csc.exe -------------------------------------------------------
$cscCandidates = @(
    'C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe',
    'C:\Windows\Microsoft.NET\Framework\v4.0.30319\csc.exe'
)
$csc = $cscCandidates | Where-Object { Test-Path $_ } | Select-Object -First 1
if (-not $csc) {
    throw "csc.exe introuvable. Le .NET Framework 4.x est-il installe ? Cherche dans : $($cscCandidates -join ', ')"
}

# --- Verifier les dependances vendorisees ------------------------------------
$lib = Join-Path $PSScriptRoot 'lib'
$needed = @(
    'Microsoft.Web.WebView2.Core.dll',
    'Microsoft.Web.WebView2.WinForms.dll',
    'WebView2Loader.x64.dll',
    'WebView2Loader.x86.dll'
)
foreach ($n in $needed) {
    if (-not (Test-Path (Join-Path $lib $n))) {
        throw "Dependance manquante : lib\$n`n" +
              "Recuperez le SDK : https://www.nuget.org/api/v2/package/Microsoft.Web.WebView2 " +
              "(le .nupkg est une archive zip ; voir lib\README.txt)"
    }
}

# --- Preparer la sortie ------------------------------------------------------
$outDir = Split-Path -Parent $OutFile
if (-not (Test-Path $outDir)) { New-Item -ItemType Directory -Force $outDir | Out-Null }

$sources  = @('SGWLauncher.cs', 'UpdateEngine.cs') | ForEach-Object { Join-Path $PSScriptRoot $_ }
foreach ($s in $sources) { if (-not (Test-Path $s)) { throw "Source introuvable : $s" } }
$manifest = Join-Path $PSScriptRoot 'app.manifest'

# --- Metadonnees de version --------------------------------------------------
# Sans ces attributs, l'onglet « Details » de l'executable est entierement vide
# et la version vaut 0.0.0.0. Presque tout logiciel legitime renseigne ces
# champs : leur absence pese lourd dans les moteurs antivirus par apprentissage,
# qui classaient le launcher en Trojan:Win32/Wacatac.B!ml.
#
# L'editeur declare est « The Fifth Race », le nom du projet, et non celui du
# jeu d'origine : ces champs disent qui publie le binaire, pas a quel univers il
# se rattache. Le champ Copyright porte la mention de non-affiliation.
#
# La version est lue dans SGWLauncher.cs plutot que saisie ici : une seule
# source de verite, impossible de laisser les deux diverger d'une release a
# l'autre.
$version = (Select-String -Path (Join-Path $PSScriptRoot 'SGWLauncher.cs') `
            -Pattern 'LauncherVersion\s*=\s*"([^"]+)"' | Select-Object -First 1).Matches[0].Groups[1].Value
if (-not $version) { throw "Version introuvable : constante LauncherVersion absente de SGWLauncher.cs" }

# csc attend quatre composants ; la constante n'en porte que trois (2.0.0).
$version4 = $version
while (($version4 -split '\.').Count -lt 4) { $version4 += '.0' }

$assemblyInfo = Join-Path $outDir 'AssemblyInfo.generated.cs'
@"
// Fichier genere par build.ps1 -- ne pas editer, ne pas versionner.
// La version provient de la constante LauncherVersion de SGWLauncher.cs.
using System.Reflection;
using System.Runtime.InteropServices;

[assembly: AssemblyTitle("The Fifth Race Launcher")]
[assembly: AssemblyDescription("Installe les mises a jour du serveur The Fifth Race et lance le jeu.")]
[assembly: AssemblyProduct("The Fifth Race")]
[assembly: AssemblyCompany("The Fifth Race")]
[assembly: AssemblyCopyright("Projet fan-made non affilie a MGM / Amazon Studios.")]
[assembly: AssemblyVersion("$version4")]
[assembly: AssemblyFileVersion("$version4")]
[assembly: AssemblyInformationalVersion("$version")]
[assembly: ComVisible(false)]
"@ | Set-Content -Path $assemblyInfo -Encoding UTF8

$sources += $assemblyInfo

# --- Ressources embarquees ---------------------------------------------------
# Le second champ est le nom logique utilise par Res.Open() cote C#.
$resources = @()
foreach ($n in $needed) { $resources += "/resource:$lib\$n,lib/$n" }

# Tout le contenu de ui\ (page, polices, images) est embarque tel quel.
#
# La documentation qui accompagne les ressources en est exclue : les licences
# des polices vivent a cote des fichiers qu'elles couvrent, mais les embarquer
# alourdirait l'executable sans que rien ne les lise jamais.
$exclusDoc = @('.md', '.txt')
$ui = (Join-Path $PSScriptRoot 'ui').TrimEnd('\')
Get-ChildItem $ui -Recurse -File |
    Where-Object { $exclusDoc -notcontains $_.Extension.ToLowerInvariant() } |
    ForEach-Object {
        $rel = $_.FullName.Substring($ui.Length + 1) -replace '\\', '/'
        $resources += "/resource:$($_.FullName),ui/$rel"
    }

# --- Arguments de compilation ------------------------------------------------
$cscArgs = @(
    '/nologo'
    '/target:winexe'          # application fenetree : pas de console derriere
    '/platform:anycpu'
    '/optimize+'
    '/codepage:65001'         # les sources sont en UTF-8 (accents dans l'UI)
    "/out:$OutFile"
    '/reference:System.dll'
    '/reference:System.Core.dll'
    '/reference:System.Drawing.dll'
    '/reference:System.Windows.Forms.dll'
    '/reference:System.Web.Extensions.dll'   # JavaScriptSerializer (manifest + pont JS)
    "/reference:$lib\Microsoft.Web.WebView2.Core.dll"
    "/reference:$lib\Microsoft.Web.WebView2.WinForms.dll"
)
$cscArgs += $resources
if (Test-Path $manifest) { $cscArgs += "/win32manifest:$manifest" }
if (Test-Path $IconFile) { $cscArgs += "/win32icon:$IconFile" }
$cscArgs += $sources

Write-Host "Compilation de SGWLauncher.exe..." -ForegroundColor Cyan
& $csc @cscArgs
if ($LASTEXITCODE -ne 0) { throw "Echec de la compilation (code $LASTEXITCODE)." }

$info = Get-Item $OutFile

Write-Host ""
Write-Host "  OK  $($info.FullName)" -ForegroundColor Green
Write-Host ("      version {0}  —  {1:N0} ko  ({2} ressources embarquees)" -f `
            $version, ($info.Length / 1KB), $resources.Count)
Write-Host ("      SHA-256 {0}" -f (Get-FileHash $OutFile -Algorithm SHA256).Hash)

# Le script de publication ne fait pas partie du depot public du launcher :
# on ne renvoie vers lui que s'il est effectivement la.
if (Test-Path (Join-Path $PSScriptRoot 'Publish-ClientUpdate.ps1')) {
    Write-Host ""
    Write-Host "Etape suivante : .\Publish-ClientUpdate.ps1" -ForegroundColor DarkGray
}
