#!/usr/bin/env bash
# Runs a rig-discovery script inside the open MunShot Unity editor via the Unity CLI.
#
#   Tools/RigDiscovery/run.sh find '<regex>'     list game prefab addresses matching <regex>
#   Tools/RigDiscovery/run.sh dump <address>     write a rig report to Tools/RigDiscovery/out/<address>.txt
#   Tools/RigDiscovery/run.sh animator <address> write Animator layers/params/clips to out/<address>.animator.txt
#   Tools/RigDiscovery/run.sh axes <address>     write each bone's rest rotation and axis directions to out/<address>.axes.txt
# Needs: the MunShot editor open with the com.unity.pipeline package, and `unity` on PATH.
set -euo pipefail

here="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
project="$(cd "$here/../.." && pwd)"

usage() { sed -n '2,8p' "${BASH_SOURCE[0]}" | sed 's/^# \{0,1\}//' >&2; exit 2; }
[ $# -eq 2 ] || usage

case "$1" in
  find) script="$here/find_prefabs.cs"; placeholder="__PATTERN__" ;;
  dump) script="$here/dump_rig.cs";     placeholder="__ADDRESS__" ;;
  animator) script="$here/probe_animator.cs"; placeholder="__ADDRESS__" ;;
  axes) script="$here/dump_axes.cs"; placeholder="__ADDRESS__" ;;
  *) usage ;;
esac

# The value lands inside a C# verbatim string, so double any quotes.
value="${2//\"/\"\"}"
tmp="$(mktemp "${TMPDIR:-/tmp}/munshot-rig-XXXXXX.cs")"
trap 'rm -f "$tmp"' EXIT
# Literal splice (not gsub), so regex characters like \ and & in the value survive.
PLACEHOLDER="$placeholder" VALUE="$value" awk '{
  p = ENVIRON["PLACEHOLDER"]; out = ""
  while ((i = index($0, p)) > 0) { out = out substr($0, 1, i - 1) ENVIRON["VALUE"]; $0 = substr($0, i + length(p)) }
  print out $0
}' "$script" > "$tmp"

# --project-path matters: another project's editor (e.g. KERNEL) may be open too.
unity command eval_file --project-path "$project" --no-banner --result-only --timeout 180 --file "$tmp"
