#!/bin/sh
set -eu
package_dir=$(CDPATH= cd -- "$(dirname -- "$0")/.." && pwd)
developer_dir=$(xcode-select -p)
# SwiftPM's CLT runner needs its bundled Swift Testing framework and macros explicitly.
if [ -d "$developer_dir/Library/Developer/Frameworks/Testing.framework" ] && [ ! -d "$developer_dir/Platforms/iPhoneOS.platform" ]; then
  frameworks="$developer_dir/Library/Developer/Frameworks"
  plugins="$developer_dir/usr/lib/swift/host/plugins/testing"
  interop="$developer_dir/Library/Developer/usr/lib"
  scratch=$(mktemp -d /tmp/tuci-swift-tests.XXXXXX)
  trap 'rm -rf "$scratch"' EXIT
  swift test --package-path "$package_dir" --scratch-path "$scratch" --disable-xctest --enable-swift-testing \
    -Xswiftc -F -Xswiftc "$frameworks" -Xswiftc -plugin-path -Xswiftc "$plugins" \
    -Xlinker -F -Xlinker "$frameworks" -Xlinker -rpath -Xlinker "$frameworks" \
    -Xlinker -rpath -Xlinker "$interop" "$@"
else
  exec swift test --package-path "$package_dir" "$@"
fi
