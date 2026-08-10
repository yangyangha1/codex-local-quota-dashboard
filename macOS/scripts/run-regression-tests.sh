#!/bin/zsh
set -euo pipefail

script_dir="$(cd "$(dirname "$0")" && pwd)"
project_dir="$(cd "$script_dir/.." && pwd)"
build_dir="$(mktemp -d "${TMPDIR:-/tmp}/codex-quota-widget-tests.XXXXXX")"
trap 'rm -rf "$build_dir"' EXIT

swiftc -parse-as-library \
  "$project_dir/Sources/CodexQuotaWidget/Models.swift" \
  "$project_dir/Sources/CodexQuotaWidget/WidgetWindowGeometry.swift" \
  "$project_dir/Sources/CodexQuotaWidget/UsageScanner.swift" \
  "$project_dir/Sources/CodexQuotaWidget/HistoryStore.swift" \
  "$project_dir/Sources/CodexQuotaWidget/TokenRateChart.swift" \
  "$project_dir/Verification/RegressionMain.swift" \
  -o "$build_dir/CodexQuotaWidgetRegression"

"$build_dir/CodexQuotaWidgetRegression"
