#!/usr/bin/env bash
set -euo pipefail

if [[ $# -ne 2 ]]; then
  echo "Usage: $0 LOCAL_FEED PACKAGE_VERSION" >&2
  exit 2
fi

repo=$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")/.." && pwd -P)
feed=$(cd -- "$1" && pwd -P)
version=$2
work=$(mktemp -d)
trap 'rm -rf -- "$work"' EXIT
work=$(cd -- "$work" && pwd -P)

fixture="$repo/fixtures/Consumer"
mkdir -p "$work/consumer/Calculator" "$work/consumer/Calculator.Tests"
cp -- "$fixture"/Calculator/*.{cs,csproj} "$work/consumer/Calculator/"
cp -- "$fixture"/Calculator.Tests/*.{cs,csproj} "$work/consumer/Calculator.Tests/"

cat > "$work/tool.NuGet.Config" <<'EOF_CONFIG'
<?xml version="1.0" encoding="utf-8"?>
<configuration>
  <packageSources>
    <clear />
  </packageSources>
</configuration>
EOF_CONFIG
dotnet nuget add source "$feed" --name smoke-test --configfile "$work/tool.NuGet.Config"

cd -- "$work/consumer"
NUGET_PACKAGES="$work/packages" dotnet tool install Rmut --version "$version" \
  --tool-path "$work/tools" --configfile "$work/tool.NuGet.Config" --no-http-cache
"$work/tools/rmut" run --project Calculator/Calculator.csproj \
  --test-project Calculator.Tests/Calculator.Tests.csproj --json "$work/report.json"

if ! jq -e '
  .baseline.outcome == "passed" and
  ([.mutants[] | [.original, .replacement, .outcome]] | sort) ==
    [["+", "-", "killed"], [">", ">=", "survived"]]
' "$work/report.json" >/dev/null; then
  cat "$work/report.json" >&2
  exit 1
fi
echo "Package smoke test passed (one killed mutant and one survivor)."
