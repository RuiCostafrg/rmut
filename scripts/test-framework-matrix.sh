#!/usr/bin/env bash
set -euo pipefail

if [[ $# -ne 5 ]]; then
  echo "Usage: $0 LOCAL_FEED PACKAGE_VERSION MSTEST|XUNIT|NUNIT SDK_VERSION ARTIFACT_DIRECTORY" >&2
  exit 2
fi

repo=$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")/.." && pwd -P)
feed=$(cd -- "$1" && pwd -P)
version=$2
framework=$3
sdk=$4
case "$framework" in MSTEST|XUNIT|NUNIT) ;; *) exit 2 ;; esac
case "$sdk" in 10.0.*|11.0.*) ;; *) exit 2 ;; esac
target="net${sdk%%.*}.0"

mkdir -p -- "$5"
artifacts=$(cd -- "$5" && pwd -P)
work=$(mktemp -d)
trap 'rm -rf -- "$work"' EXIT
work=$(cd -- "$work" && pwd -P)

cp -R -- "$repo/fixtures/FrameworkMatrix" "$work/consumer"
cd -- "$work/consumer"
jq -n --arg sdk "$sdk" '{sdk: {version: $sdk, rollForward: "disable", allowPrerelease: true}}' > global.json
cat > Directory.Build.props <<PROPS
<Project>
  <PropertyGroup>
    <TargetFramework>$target</TargetFramework>
    <MatrixFramework>$framework</MatrixFramework>
  </PropertyGroup>
</Project>
PROPS
test "$(dotnet --version)" = "$sdk"
export NUGET_PACKAGES="$work/packages"
dotnet tool install Rmut --version "$version" --tool-path "$work/tools" \
  --source "$feed" --no-http-cache
dotnet --info | tee "$artifacts/dotnet-info.txt"
dotnet msbuild Calculator.Tests/Calculator.Tests.csproj -nologo -getItem:PackageReference |
  jq -r '.Items.PackageReference[] | "\(.Identity) \(.Version)"' |
  tee "$artifacts/package-versions.txt"

status=0
for mode in off on; do
  collector=()
  if [[ "$mode" == on ]]; then
    collector=(--collect 'XPlat Code Coverage')
  fi
  if ! "$work/tools/rmut" run --project Calculator/Calculator.csproj \
    --test-project Calculator.Tests/Calculator.Tests.csproj --execution switched \
    --switched-test-assembly "bin/Debug/$target/Calculator.Tests.dll" \
    --baseline-results "$artifacts/$mode" --json "$artifacts/$mode.json" \
    "${collector[@]}" 2>&1 | tee "$artifacts/$mode.log"; then
    status=1
  fi
  if [[ "$(grep -c '^Switched execution ' "$artifacts/$mode.log")" != 2 ]]; then
    echo "Expected both mutants to use switched execution with Coverlet $mode." >&2
    status=1
  fi
done
if ((status)); then
  exit "$status"
fi

python3 "$repo/scripts/verify-framework-matrix.py" "$artifacts" "$framework" "$target" "$sdk" \
  | tee "$artifacts/verified.txt"
