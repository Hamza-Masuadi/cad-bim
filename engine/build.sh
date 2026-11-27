#!/usr/bin/env bash
# Builds every variant into engine/bin (engine/bin-$SA_TAG when SA_TAG is set):
#   bin/autocad/<year>/  bin/revit/<year>/  bin/cli/
# Usage: ./build.sh [--test] [--config Debug|Release] [--years "2024 2025 2026 2027"]
set -euo pipefail

here="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
config="Release"
years="2024 2025 2026 2027"
run_tests=0

while [ $# -gt 0 ]; do
  case "$1" in
    --test) run_tests=1 ;;
    --config) config="$2"; shift ;;
    --years) years="$2"; shift ;;
    *) echo "unknown option $1" >&2; exit 2 ;;
  esac
  shift
done

# Prefer the workspace-local SDK (tools/env.sh beside the project, or $SA_ENV) over a system one.
env_file="${SA_ENV:-$here/../../tools/env.sh}"
# shellcheck disable=SC1090
if [ -f "$env_file" ]; then source "$env_file"; fi
command -v dotnet >/dev/null 2>&1 || { echo "dotnet SDK not found" >&2; exit 1; }

cd "$here"
failed=0
build() {
  local label="$1"; shift
  if dotnet build "$@" -c "$config" -nologo -v:q -clp:NoSummary; then
    echo "OK    $label"
  else
    echo "FAIL  $label"
    failed=1
  fi
}

build "SA.Core" src/SA.Core/SA.Core.csproj
build "sa.exe (net48)" src/SA.Cli/SA.Cli.csproj
for y in $years; do
  build "SA.AutoCAD $y" src/SA.AutoCAD/SA.AutoCAD.csproj -p:AppYear="$y"
  build "SA.Revit $y" src/SA.Revit/SA.Revit.csproj -p:AppYear="$y"
done

if [ "$run_tests" = 1 ]; then
  if dotnet test src/SA.Core.Tests/SA.Core.Tests.csproj -c "$config" -nologo -v:q; then
    echo "OK    tests"
  else
    echo "FAIL  tests"
    failed=1
  fi
fi

exit $failed
