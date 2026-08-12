#!/bin/zsh
set -euo pipefail

script_dir="$(cd "$(dirname "$0")" && pwd)"
project_dir="$(cd "$script_dir/.." && pwd)"
app_dir="$project_dir/CodexQuotaWidget.app"
app_icon="$project_dir/Resources/AppIcon.icns"

[[ -f "$app_icon" ]] || {
  print -u2 "Missing macOS app icon: $app_icon"
  exit 1
}

# Build a universal binary so the release package supports both Apple Silicon
# and Intel Macs running macOS 14 or later.
architectures=(arm64 x86_64)
binaries=()
for architecture in "${architectures[@]}"; do
  scratch_dir="$project_dir/.build/release-$architecture"
  target_triple="$architecture-apple-macosx14.0"
  swift build --package-path "$project_dir" --product CodexQuotaWidget -c release \
    --scratch-path "$scratch_dir" --triple "$target_triple"
  binary_dir="$(swift build --package-path "$project_dir" -c release \
    --scratch-path "$scratch_dir" --triple "$target_triple" --show-bin-path)"
  binaries+=("$binary_dir/CodexQuotaWidget")
done

# The generated bundle is rebuilt from scratch so removed plug-in directories
# can never be carried forward from an earlier build.
[[ "$app_dir" == "$project_dir/CodexQuotaWidget.app" ]] || exit 1
rm -rf "$app_dir"
mkdir -p "$app_dir/Contents/MacOS" "$app_dir/Contents/Resources"
lipo -create "${binaries[@]}" -output "$app_dir/Contents/MacOS/CodexQuotaWidget"
cp "$project_dir/Info.plist" "$app_dir/Contents/Info.plist"
# AppIcon.icns is generated from the original Windows dashboard icon artwork.
cp "$app_icon" "$app_dir/Contents/Resources/AppIcon.icns"

# This project does not have an Apple Developer ID certificate or notarization.
# Deliberately leave the public bundle unsigned; release instructions explain
# the first-launch security confirmation required by macOS.

print "Built $app_dir"
