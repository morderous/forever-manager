# Writes the half of the Secrets class the build makes: the Forever pack's
# read-only GitHub token (..\token.txt, one line), the CurseForge key
# (..\curseforge-key.txt, one line) and the friends' server key
# (..\worker-key.txt, one line, since 1.0.7), each XOR-masked with a random
# key made for this build, so none is in the exe in the clear. Never prints them.
#
#   make_secrets.ps1 -Out <file.cs>              the real ones (a fake one, with a warning, for a file that is missing)
#   make_secrets.ps1 -Out <file.cs> -Fake        fake ones only (the tests)
#   make_secrets.ps1 -Out <file.cs> -OnlyCurse   a fake token and the real CurseForge key (the one live CurseForge check)
#   make_secrets.ps1 -Check <file.exe>           says whether any of them is in that exe in the clear
param(
    [string]$Out,
    [switch]$Fake,
    [switch]$OnlyCurse,
    [string]$Check
)
$ErrorActionPreference = 'Stop'

$FakeToken = 'github_pat_FAKE_TEST_TOKEN_0123456789_abcdefghijklmnopqrstuvwxyz'
$FakeCurse = 'FAKE_TEST_CURSEFORGE_KEY_0123456789_abcdefghijklmnop'
$TokenFile = Join-Path $PSScriptRoot '..\token.txt'
$CurseFile = Join-Path $PSScriptRoot '..\curseforge-key.txt'
$FakeKey = 'FAKE_TEST_FRIENDS_KEY_0123456789_abcdefghijklmnopqrs'
$KeyFile = Join-Path $PSScriptRoot '..\worker-key.txt'

function Read-One([string]$path) {
    if (-not (Test-Path -LiteralPath $path)) { return $null }
    $text = [IO.File]::ReadAllText((Resolve-Path -LiteralPath $path)).Trim()
    if ($text -eq '' -or $text -match '\s') { return $null }
    return $text
}

if ($Check) {
    $bytes = [IO.File]::ReadAllBytes((Resolve-Path -LiteralPath $Check))
    $latin = [Text.Encoding]::GetEncoding(28591)
    $hay = $latin.GetString($bytes)
    $found = @()
    $all = @(@('the fake token', $FakeToken), @('the fake CurseForge key', $FakeCurse),
             @('the fake friends key', $FakeKey),
             @('token.txt', (Read-One $TokenFile)), @('curseforge-key.txt', (Read-One $CurseFile)),
             @('worker-key.txt', (Read-One $KeyFile)))
    foreach ($pair in $all) {
        $secret = $pair[1]
        if (-not $secret) { continue }
        foreach ($enc in @([Text.Encoding]::UTF8, [Text.Encoding]::Unicode)) {
            $needle = $latin.GetString($enc.GetBytes($secret))
            if ($hay.IndexOf($needle, [StringComparison]::Ordinal) -ge 0) { $found += $pair[0] }
        }
    }
    if ($found.Count -gt 0) {
        Write-Host ('clear-text check: FOUND in the exe: ' + (($found | Select-Object -Unique) -join ', '))
        exit 1
    }
    Write-Host 'clear-text check: ok (no secret in the clear)'
    exit 0
}

if (-not $Out) { Write-Host 'make_secrets.ps1: -Out <file.cs> or -Check <file.exe>'; exit 2 }

$token = $null
$curse = $null
if (-not $Fake -and -not $OnlyCurse) {
    # Since 1.0.8 no GitHub token goes in: the friends' server gives everything.
    # token.txt is still checked for below, so a build never holds it in the clear.
    Write-Host 'token.txt: left out since 1.0.8 (a fake one, never sent)'
    $friends = Read-One $KeyFile
    if ($friends) { Write-Host 'worker-key.txt: found, embedded masked' }
    else { Write-Host 'WARNING: worker-key.txt not found (or empty): this build never asks the friends server, only GitHub' }
}
if (-not $Fake) {
    $curse = Read-One $CurseFile
    if ($curse) { Write-Host 'curseforge-key.txt: found, embedded masked' }
    else { Write-Host 'WARNING: curseforge-key.txt not found (or empty): CurseForge is off in this build' }
}

$rng = New-Object System.Security.Cryptography.RNGCryptoServiceProvider
function Masked([string]$secret) {
    $plain = [Text.Encoding]::UTF8.GetBytes($secret)
    $key = New-Object byte[] $plain.Length
    $rng.GetBytes($key)
    $masked = New-Object byte[] $plain.Length
    for ($i = 0; $i -lt $plain.Length; $i++) { $masked[$i] = $plain[$i] -bxor $key[$i] }
    return @((($masked | ForEach-Object { '0x{0:x2}' -f $_ }) -join ', '), (($key | ForEach-Object { '0x{0:x2}' -f $_ }) -join ', '))
}

$t = Masked $(if ($token) { $token } else { $FakeToken })
$c = Masked $(if ($curse) { $curse } else { $FakeCurse })
$k = Masked $(if ($friends) { $friends } else { $FakeKey })
$given = @{ $true = 'true'; $false = 'false' }
$cs = @"
// Made by make_secrets.ps1 for one build, then deleted. Do not keep, do not share.
namespace ForeverManager
{
    static partial class Secrets
    {
        static readonly byte[] TokenMasked = { $($t[0]) };
        static readonly byte[] TokenKey = { $($t[1]) };
        static readonly byte[] CurseMasked = { $($c[0]) };
        static readonly byte[] CurseKey = { $($c[1]) };
        static readonly byte[] KeyMasked = { $($k[0]) };
        static readonly byte[] KeyKey = { $($k[1]) };
        static readonly bool TokenGiven = $($given[[bool]$token]);
        static readonly bool CurseGiven = $($given[[bool]$curse]);
        static readonly bool KeyGiven = $($given[[bool]$friends]);
    }
}
"@
$dir = Split-Path -Parent $Out
if ($dir -and -not (Test-Path -LiteralPath $dir)) { New-Item -ItemType Directory -Path $dir | Out-Null }
[IO.File]::WriteAllText($Out, $cs, (New-Object Text.UTF8Encoding($false)))
exit 0
