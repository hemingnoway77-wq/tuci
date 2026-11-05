#!/bin/sh
# The Test action uses a separate bundle ID and an ephemeral notebook.
set -eu
ios_dir=$(CDPATH= cd -- "$(dirname -- "$0")/.." && pwd)
project="$ios_dir/Tuci.xcodeproj"
device=${TUCI_SIMULATOR_ID:-}
if [ -z "$device" ]; then
    device=$(xcodebuild -showdestinations -project "$project" -scheme Tuci | python3 -c '
import re, sys
for line in sys.stdin:
    if "Ineligible destinations" in line:
        break
    match = re.search(r"\bid:\s*([0-9a-fA-F-]{36})\s*,", line)
    if match and "platform:iOS Simulator" in line.replace("platform: ", "platform:") and re.search(r"\bname:\s*iPhone", line):
        print(match.group(1))
        break
')
fi
if [ -z "$device" ]; then
    echo "No iPhone simulator available. Install an iOS simulator runtime in Xcode Settings." >&2
    exit 1
fi
results=${TUCI_XCRESULT_PATH:-"$(mktemp -d /tmp/tuci-ui-smoke.XXXXXX)/Tuci.xcresult"}
printf 'Simulator %s\nResults %s\n' "$device" "$results"
xcodebuild -version
xcodebuild test -project "$project" -scheme Tuci -configuration UISmoke \
    -destination "platform=iOS Simulator,id=$device" -parallel-testing-enabled NO \
    -resultBundlePath "$results" -only-testing:TuciUITests
