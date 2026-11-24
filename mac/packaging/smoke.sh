#!/bin/bash
# 실제로 앱을 띄워 보는 점검 (CI 의 macOS 러너에서 실행)
#   smoke.sh "/Applications/파란전화기.app"
# - 따로 만든 데이터 폴더로 실행해 창(설정 · 안내 · 업데이트)을 열어 보고
# - 몇 초 뒤에도 살아 있는지, error.log 가 없는지 확인
# - 두 번째 실행은 바로 끝나는지(단일 실행) 확인
set -uo pipefail
APP="${1:?앱 경로}"
EXE="$APP/Contents/MacOS/BluePhone"
DATA="$(mktemp -d)/bp-smoke"
mkdir -p "$DATA"
cat > "$DATA/settings.json" <<'JSON'
{"Id":"smoke000000000000000000000000001","Name":"점검","Dept":"CI","Port":39631,"AutoStart":false,"Sound":false,"AutoUpdate":false}
JSON

fail() {
  echo "::error::$1"
  echo "--- error.log ---"; cat "$DATA/error.log" 2>/dev/null
  echo "--- uitest.log ---"; cat "$DATA/uitest.log" 2>/dev/null
  echo "--- stdout/stderr ---"; cat "$DATA/out.txt" 2>/dev/null
  kill "$PID" 2>/dev/null
  exit 1
}

BLUEPHONE_UITEST="settings;notice;update;wait:2000" "$EXE" --data "$DATA" > "$DATA/out.txt" 2>&1 &
PID=$!
for i in $(seq 1 60); do
  sleep 0.5
  kill -0 "$PID" 2>/dev/null || fail "앱이 시작 직후 종료됐습니다"
  grep -q "done" "$DATA/uitest.log" 2>/dev/null && break
done
grep -q "done" "$DATA/uitest.log" 2>/dev/null || fail "창 점검이 끝나지 않았습니다"
grep -qvE "^[0-9:]+ (ok|done)" "$DATA/uitest.log" && fail "창 점검 중 오류"
sleep 2
kill -0 "$PID" 2>/dev/null || fail "앱이 실행 중에 종료됐습니다"
[ -s "$DATA/error.log" ] && fail "error.log 에 오류가 기록됐습니다"

# 단일 실행: 두 번째는 기존 창을 부르고 곧바로 끝나야 함
"$EXE" --data "$DATA" > /dev/null 2>&1 &
P2=$!
for i in $(seq 1 20); do sleep 0.5; kill -0 "$P2" 2>/dev/null || break; done
if kill -0 "$P2" 2>/dev/null; then kill "$P2"; fail "두 번째 실행이 끝나지 않았습니다 (단일 실행 잠금 실패)"; fi

kill "$PID" 2>/dev/null
wait "$PID" 2>/dev/null
echo "smoke ok"
cat "$DATA/uitest.log"
