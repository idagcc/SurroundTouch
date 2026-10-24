#!/bin/sh
# macOS/Linux: compile Windows variants and run only pure managed tests under Mono.
set -eu
task_root=$(CDPATH= cd -- "$(dirname -- "$0")/.." && pwd)
task_output=$(mktemp -d "${TMPDIR:-/tmp}/SurroundTouch-tests.XXXXXX")
compile_app() {
    mcs -langversion:5 -sdk:4.5 -warn:4 -warnaserror -optimize+ -platform:x64 \
        -r:System.Windows.Forms -r:System.Drawing -r:System.Runtime.Serialization -r:System.Management "$@"
}
compile_app -target:winexe -out:"$task_output/SurroundTouch.exe" "$task_root"/src/*.cs
python3 "$task_root/tests/manifest_tools.py" build "$task_root/app.manifest" "$task_output/SurroundTouch.exe" "$task_output/app.res"
compile_app -target:winexe -win32res:"$task_output/app.res" -out:"$task_output/SurroundTouch.exe" "$task_root"/src/*.cs
compile_app -target:winexe -out:"$task_output/SurroundTouch.Diagnostics.exe" "$task_root"/src/*.cs
python3 "$task_root/tests/manifest_tools.py" build "$task_root/diagnostics.manifest" "$task_output/SurroundTouch.Diagnostics.exe" "$task_output/diagnostics.res"
compile_app -target:winexe -win32res:"$task_output/diagnostics.res" -out:"$task_output/SurroundTouch.Diagnostics.exe" "$task_root"/src/*.cs
python3 "$task_root/tests/manifest_tools.py" verify "$task_output/SurroundTouch.exe" true
python3 "$task_root/tests/manifest_tools.py" verify "$task_output/SurroundTouch.Diagnostics.exe" false
mcs -langversion:5 -sdk:4.5 -warn:4 -warnaserror -optimize+ -platform:x64 \
    -out:"$task_output/CoreTests.exe" "$task_root/src/TouchCore.cs" "$task_root/tests/CoreTests.cs"
compile_app -main:SurroundTouch.RuntimeTests -out:"$task_output/RuntimeTests.exe" "$task_root"/src/*.cs "$task_root/tests/RuntimeTests.cs"
compile_app -main:SurroundTouch.WindowsIntegration -out:"$task_output/WindowsIntegration.exe" "$task_root"/src/*.cs "$task_root/tests/WindowsIntegration.cs"
mono "$task_output/CoreTests.exe"
mono "$task_output/RuntimeTests.exe"
printf 'Windows applications and integration test compiled. Windows UI/input tests were not run.\nArtifacts: %s\n' "$task_output"
