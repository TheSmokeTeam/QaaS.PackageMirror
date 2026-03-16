#!/usr/bin/env bash
set -euo pipefail

if [ "$#" -lt 1 ]; then
  echo "usage: $0 <packages-qaas-folder> [elastic-uri] [elastic-username] [elastic-password]" >&2
  exit 1
fi

# Edit these defaults if you want hard-coded values without passing runtime parameters.
FRAMEWORK_REPO_ROOT="/d/QaaS/_isolated/QaaS.Framework"
OUTPUT_FOLDER_NAME="qaas-local-elastic"
VERSION_MODE="same"                # "same" or "suffix"
VERSION_SUFFIX="local-elastic.1"   # used only when VERSION_MODE="suffix"
DEFAULT_SEND_LOGS="true"
DEFAULT_ELASTIC_URI="http://localhost:9200"
DEFAULT_ELASTIC_USERNAME=""
DEFAULT_ELASTIC_PASSWORD=""

INPUT_QAAS_DIR="${1%/}"
ELASTIC_URI="${2:-$DEFAULT_ELASTIC_URI}"
ELASTIC_USERNAME="${3:-$DEFAULT_ELASTIC_USERNAME}"
ELASTIC_PASSWORD="${4:-$DEFAULT_ELASTIC_PASSWORD}"

LOGGER_OPTIONS="$FRAMEWORK_REPO_ROOT/QaaS.Framework.Executions/Options/LoggerOptions.cs"
CONSTANTS="$FRAMEWORK_REPO_ROOT/QaaS.Framework.Executions/Constants.cs"
TMP_DIR="$(mktemp -d)"
PACK_DIR="$TMP_DIR/packed"
LOGGER_OPTIONS_BAK="$TMP_DIR/LoggerOptions.cs"
CONSTANTS_BAK="$TMP_DIR/Constants.cs"

cleanup() {
  cp "$LOGGER_OPTIONS_BAK" "$LOGGER_OPTIONS" >/dev/null 2>&1 || true
  cp "$CONSTANTS_BAK" "$CONSTANTS" >/dev/null 2>&1 || true
  rm -rf "$TMP_DIR"
}
trap cleanup EXIT

[ -d "$INPUT_QAAS_DIR" ] || { echo "missing packages folder: $INPUT_QAAS_DIR" >&2; exit 1; }
[ -d "$FRAMEWORK_REPO_ROOT" ] || { echo "missing framework repo: $FRAMEWORK_REPO_ROOT" >&2; exit 1; }
[ -f "$LOGGER_OPTIONS" ] || { echo "missing file: $LOGGER_OPTIONS" >&2; exit 1; }
[ -f "$CONSTANTS" ] || { echo "missing file: $CONSTANTS" >&2; exit 1; }

command -v python3 >/dev/null 2>&1 || { echo "python3 is required" >&2; exit 1; }
command -v dotnet >/dev/null 2>&1 || { echo "dotnet is required" >&2; exit 1; }

BASE_VERSION="$(find "$INPUT_QAAS_DIR/qaas.framework.executions" -mindepth 1 -maxdepth 1 -type d -printf '%f\n' | sort -V | tail -n1)"
[ -n "$BASE_VERSION" ] || { echo "could not determine QaaS.Framework.Executions version from $INPUT_QAAS_DIR" >&2; exit 1; }

if [ "$VERSION_MODE" = "same" ]; then
  PACKAGE_VERSION="$BASE_VERSION"
else
  PACKAGE_VERSION="${BASE_VERSION}-${VERSION_SUFFIX}"
fi

OUTPUT_ROOT="$(dirname "$INPUT_QAAS_DIR")/$OUTPUT_FOLDER_NAME"

cp "$LOGGER_OPTIONS" "$LOGGER_OPTIONS_BAK"
cp "$CONSTANTS" "$CONSTANTS_BAK"

python3 - "$LOGGER_OPTIONS" "$CONSTANTS" "$DEFAULT_SEND_LOGS" "$ELASTIC_URI" "$ELASTIC_USERNAME" "$ELASTIC_PASSWORD" <<'PY'
import re
import sys
from pathlib import Path

logger_options = Path(sys.argv[1])
constants = Path(sys.argv[2])
send_logs = sys.argv[3].lower()
elastic_uri = sys.argv[4]
elastic_username = sys.argv[5]
elastic_password = sys.argv[6]

if send_logs not in {"true", "false"}:
    raise SystemExit("DEFAULT_SEND_LOGS must be true or false")

def cs_string(value: str) -> str:
    return '"' + value.replace('\\', '\\\\').replace('"', '\\"') + '"'

def cs_nullable(value: str) -> str:
    return "null" if value == "" else cs_string(value)

def patch(path: Path, replacements):
    text = path.read_text(encoding="utf-8")
    for pattern, repl, expected in replacements:
        text, count = re.subn(pattern, repl, text, flags=re.S)
        if count != expected:
            raise SystemExit(f"{path}: expected {expected} replacement(s) for {pattern!r}, got {count}")
    path.write_text(text, encoding="utf-8")

patch(
    logger_options,
    [
        (
            r'(\[Option\("send-logs".*?\bDefault\s*=\s*)(true|false)',
            lambda m: m.group(1) + send_logs,
            1,
        ),
        (
            r'(public\s+bool\s+SendLogs\s*\{\s*get;\s*init;\s*\}\s*=\s*)(true|false)',
            lambda m: m.group(1) + send_logs,
            1,
        ),
        (
            r'(\[Option\("elastic-uri".*?\bDefault\s*=\s*)(null|"([^"\\]|\\.)*")',
            lambda m: m.group(1) + cs_nullable(elastic_uri),
            1,
        ),
        (
            r'(public\s+string\?\s+ElasticUri\s*\{\s*get;\s*init;\s*\}\s*=\s*)(null|"([^"\\]|\\.)*")',
            lambda m: m.group(1) + cs_nullable(elastic_uri),
            1,
        ),
        (
            r'(\[Option\("elastic-username".*?\bDefault\s*=\s*)(null|"([^"\\]|\\.)*")',
            lambda m: m.group(1) + cs_nullable(elastic_username),
            1,
        ),
        (
            r'(public\s+string\?\s+ElasticUsername\s*\{\s*get;\s*init;\s*\}\s*=\s*)(null|"([^"\\]|\\.)*")',
            lambda m: m.group(1) + cs_nullable(elastic_username),
            1,
        ),
        (
            r'(\[Option\("elastic-password".*?\bDefault\s*=\s*)(null|"([^"\\]|\\.)*")',
            lambda m: m.group(1) + cs_nullable(elastic_password),
            1,
        ),
        (
            r'(public\s+string\?\s+ElasticPassword\s*\{\s*get;\s*init;\s*\}\s*=\s*)(null|"([^"\\]|\\.)*")',
            lambda m: m.group(1) + cs_nullable(elastic_password),
            1,
        ),
    ],
)

patch(
    constants,
    [
        (
            r'(string\?\s+elasticUri\s*=\s*)(null|"([^"\\]|\\.)*")',
            lambda m: m.group(1) + cs_nullable(elastic_uri),
            1,
        ),
        (
            r'(string\?\s+username\s*=\s*)(null|"([^"\\]|\\.)*")',
            lambda m: m.group(1) + cs_nullable(elastic_username),
            1,
        ),
        (
            r'(string\?\s+password\s*=\s*)(null|"([^"\\]|\\.)*")',
            lambda m: m.group(1) + cs_nullable(elastic_password),
            1,
        ),
    ],
)
PY

mkdir -p "$PACK_DIR"

dotnet pack "$FRAMEWORK_REPO_ROOT/QaaS.Framework.sln" \
  -c Release \
  --include-symbols \
  -p:SymbolPackageFormat=snupkg \
  -p:PackageVersion="$PACKAGE_VERSION" \
  -o "$PACK_DIR"

rm -rf "$OUTPUT_ROOT"
mkdir -p "$OUTPUT_ROOT"
cp -R "$INPUT_QAAS_DIR"/. "$OUTPUT_ROOT"/

python3 - "$PACK_DIR" "$OUTPUT_ROOT" <<'PY'
import base64
import hashlib
import re
import shutil
import sys
import zipfile
from pathlib import Path

pack_dir = Path(sys.argv[1])
output_root = Path(sys.argv[2])

for nupkg in sorted(pack_dir.glob("QaaS.Framework.*.nupkg")):
    with zipfile.ZipFile(nupkg) as zf:
        nuspec_name = next(name for name in zf.namelist() if name.endswith(".nuspec"))
        nuspec_text = zf.read(nuspec_name).decode("utf-8", errors="ignore")

    package_id = re.search(r"<id>(.*?)</id>", nuspec_text).group(1)
    package_version = re.search(r"<version>(.*?)</version>", nuspec_text).group(1)
    package_dir = output_root / package_id.lower() / package_version

    if package_dir.exists():
        shutil.rmtree(package_dir)
    package_dir.mkdir(parents=True, exist_ok=True)

    with zipfile.ZipFile(nupkg) as zf:
        zf.extractall(package_dir)

    shutil.copy2(nupkg, package_dir / nupkg.name)

    snupkg = nupkg.with_suffix(".snupkg")
    if snupkg.exists():
        shutil.copy2(snupkg, package_dir / snupkg.name)

    sha512_name = nupkg.name.lower() + ".sha512"
    sha512_value = base64.b64encode(hashlib.sha512(nupkg.read_bytes()).digest()).decode("ascii")
    (package_dir / sha512_name).write_text(sha512_value, encoding="ascii")
PY

echo "created updated package mirror at: $OUTPUT_ROOT"
echo "framework package version: $PACKAGE_VERSION"
echo "effective send-logs default: $DEFAULT_SEND_LOGS"
echo "effective elastic-uri default: $ELASTIC_URI"
if [ -n "$ELASTIC_USERNAME" ]; then
  echo "effective elastic-username default: $ELASTIC_USERNAME"
else
  echo "effective elastic-username default: <null>"
fi
if [ -n "$ELASTIC_PASSWORD" ]; then
  echo "effective elastic-password default: <set>"
else
  echo "effective elastic-password default: <null>"
fi
