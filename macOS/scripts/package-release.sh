#!/bin/zsh
set -euo pipefail

script_dir="$(cd "$(dirname "$0")" && pwd)"
project_dir="$(cd "$script_dir/.." && pwd)"
app_dir="$project_dir/CodexQuotaWidget.app"
release_dir="$project_dir/dist"
version="$(/usr/libexec/PlistBuddy -c 'Print :CFBundleShortVersionString' "$project_dir/Info.plist")"
archive_name="CodexQuotaWidget-macOS-v${version}.zip"
archive_path="$release_dir/$archive_name"
temporary_archive="$release_dir/.${archive_name}.tmp"

"$script_dir/build-app.sh"
mkdir -p "$release_dir"
rm -f "$temporary_archive" "$archive_path"

# ditto keeps the app bundle structure and executable permissions intact for
# Finder users without adding __MACOSX, resource-fork, ACL, quarantine, or
# extended-attribute sidecars. The archive intentionally carries no Developer
# ID signature.
ditto -c -k --keepParent --norsrc --noextattr --noqtn --noacl \
  "$app_dir" "$temporary_archive"
mv "$temporary_archive" "$archive_path"

print "Built $archive_path"
shasum -a 256 "$archive_path"
