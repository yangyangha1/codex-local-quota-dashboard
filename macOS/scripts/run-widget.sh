#!/bin/zsh
set -euo pipefail

script_dir="$(cd "$(dirname "$0")" && pwd)"
project_dir="$(cd "$script_dir/.." && pwd)"

if [[ ! -d "$project_dir/CodexQuotaWidget.app" ]]; then
  "$script_dir/build-app.sh"
fi

open -n "$project_dir/CodexQuotaWidget.app"
