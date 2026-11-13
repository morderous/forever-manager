#!/bin/sh
# Builds the app and its checks into tests/out with FAKE secrets only (never
# token.txt or curseforge-key.txt) and runs the checks. Arguments go to
# Tests.exe: live, zip <file> <n>.
#   run_tests.sh curseforge-live   one real CurseForge question (Quest Completist's
#                                  files, the one for 1.60.1 picked, nothing
#                                  downloaded), with the real key from
#                                  curseforge-key.txt and a fake token; that
#                                  Tests.exe is deleted after.
set -e
# Git Bash would turn csc's /options into paths.
export MSYS_NO_PATHCONV=1 MSYS2_ARG_CONV_EXCL='*'
here="$(cd "$(dirname "$0")" && pwd)"
app="$(dirname "$here")"
out="$here/out"
mkdir -p "$out"
csc="$(cygpath -u "$WINDIR")/Microsoft.NET/Framework64/v4.0.30319/csc.exe"
refs="/reference:System.Windows.Forms.dll /reference:System.Drawing.dll /reference:System.IO.Compression.dll /reference:System.IO.Compression.FileSystem.dll /reference:System.Web.Extensions.dll /reference:System.Numerics.dll /reference:System.Security.dll"
secrets="$out/Secrets.g.cs"
mode="-Fake"
[ "$1" = "curseforge-live" ] && mode="-OnlyCurse"
powershell -NoProfile -ExecutionPolicy Bypass -File "$(cygpath -w "$app/make_secrets.ps1")" -Out "$(cygpath -w "$secrets")" $mode
trap 'rm -f "$secrets"' EXIT
if [ "$1" = "curseforge-live" ]; then
    "$csc" /nologo /target:exe /main:ForeverManager.Tests $refs /out:"$(cygpath -w "$out/TestsLive.exe")" "$(cygpath -w "$app/ForeverManager.cs")" "$(cygpath -w "$secrets")" "$(cygpath -w "$here/Tests.cs")"
    rm -f "$secrets"
    status=0
    "$out/TestsLive.exe" curseforge-live || status=$?
    rm -f "$out/TestsLive.exe"
    exit $status
fi
icon="$(cygpath -w "$app/ForeverManager.ico")"
"$csc" /nologo /target:winexe /optimize+ $refs "/win32icon:$icon" "/resource:$icon,app.ico" /out:"$(cygpath -w "$out/ForeverManager.exe")" "$(cygpath -w "$app/ForeverManager.cs")" "$(cygpath -w "$secrets")"
"$csc" /nologo /target:exe /main:ForeverManager.Tests $refs /out:"$(cygpath -w "$out/Tests.exe")" "$(cygpath -w "$app/ForeverManager.cs")" "$(cygpath -w "$secrets")" "$(cygpath -w "$here/Tests.cs")"
rm -f "$secrets"
"$out/Tests.exe" "$@"
