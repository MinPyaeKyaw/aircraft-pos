#!/usr/bin/env bash
# End-to-end check against a deployed stack.
#
#   ./smoke-test.sh                 # finds the API URL from the CloudFormation stack
#   ./smoke-test.sh https://.../prod
#
# Exercises the happy path, the low-fuel warning, the permanent-failure path,
# and the rejections. Exits non-zero if anything is wrong.
set -uo pipefail

STACK="${STACK:-PosReportPipelineStack}"
REGION="${AWS_REGION:-ap-southeast-1}"

API="${1:-${API_URL:-}}"
if [ -z "$API" ]; then
  echo "Looking up the API URL from stack $STACK in $REGION..."
  API=$(aws cloudformation describe-stacks --stack-name "$STACK" --region "$REGION" \
        --query "Stacks[0].Outputs[?OutputKey=='ApiUrl'].OutputValue" --output text 2>/dev/null)
fi
API="${API%/}"

if [ -z "$API" ] || [ "$API" = "None" ]; then
  echo "Could not find the API URL. Deploy first (./build.sh && cd cdk && npx cdk deploy),"
  echo "or pass it: ./smoke-test.sh https://xxxx.execute-api.$REGION.amazonaws.com/prod"
  exit 1
fi

echo "API: $API"
echo

PASS=0; FAIL=0
ok()   { PASS=$((PASS+1)); printf '  \033[32mPASS\033[0m  %s\n' "$1"; }
bad()  { FAIL=$((FAIL+1)); printf '  \033[31mFAIL\033[0m  %s\n     expected: %s\n     actual:   %s\n' "$1" "$2" "$3"; }
check(){ [ "$2" = "$3" ] && ok "$1" || bad "$1" "$2" "$3"; }

post() { curl -sS -X POST "$API/pos-reports" -H 'Content-Type: text/plain' --data "$1"; }
code() { curl -sS -o /dev/null -w '%{http_code}' -X POST "$API/pos-reports" -H 'Content-Type: text/plain' --data "$1"; }
field(){ python3 -c "import json,sys; print(json.load(sys.stdin).get('$1',''))" 2>/dev/null; }

# Poll until the calculator has caught up. The pipeline is asynchronous, so an
# immediate lookup legitimately returns 404 or PARSED.
await_calculated() {
  for _ in $(seq 1 15); do
    body=$(curl -sS "$API/status/$1")
    [ "$(echo "$body" | field status)" = "CALCULATED" ] && { echo "$body"; return 0; }
    python3 -c 'import time; time.sleep(3)'
  done
  echo "$body"; return 1
}

echo "1. Rejections (400 before anything is stored)"
check "empty body"            400 "$(code '')"
check "not a POS report"      400 "$(code 'hello')"
check "wrong number of parts" 400 "$(code 'POS/UL204.FR RGN/TO BKK/041205/N1642.3E09612.5/450/12500')"
check "zero ground speed"     400 "$(code 'POS/UL204.FR RGN/TO BKK/041205/N1642.3E09612.5/0/12500/2800')"
check "bad position"          400 "$(code 'POS/UL204.FR RGN/TO BKK/041205/nonsense/450/12500/2800')"
echo

echo "2. Unknown flight"
check "unknown flightId -> 404" 404 "$(curl -sS -o /dev/null -w '%{http_code}' "$API/status/NOSUCHFLIGHT")"
echo

echo "3. Happy path (the worked example from the brief)"
resp=$(post 'POS/UL204.FR RGN/TO BKK/041205/N1642.3E09612.5/450/12500/2800')
FID=$(echo "$resp" | field flightId)
check "POST returns RECEIVED" "RECEIVED" "$(echo "$resp" | field status)"
# The flight date takes its year and month from now, so build the expectation the same way.
check "flightId"             "UL204$(date -u +%Y%m)04RGNBKK" "$FID"

status=$(await_calculated "$FID")
check "reaches CALCULATED"            "CALCULATED" "$(echo "$status" | field status)"
check "remainingFlightTimeMinutes"    "43"         "$(echo "$status" | field remainingFlightTimeMinutes)"
check "estimatedFuelAtArrivalKg"      "10513"      "$(echo "$status" | field estimatedFuelAtArrivalKg)"
check "lowFuelWarning"                "False"      "$(echo "$status" | field lowFuelWarning)"
echo

echo "4. Low fuel warning (500 kg on board, the leg needs 1987 kg)"
LOW=$(post 'POS/UL777.FR RGN/TO BKK/041205/N1642.3E09612.5/450/500/2800' | field flightId)
status=$(await_calculated "$LOW")
check "lowFuelWarning set"       "True"   "$(echo "$status" | field lowFuelWarning)"
check "negative arrival fuel"    "-1487"  "$(echo "$status" | field estimatedFuelAtArrivalKg)"
echo

echo "5. Unknown destination is a permanent failure, not a retry"
UNK=$(post 'POS/UL888.FR RGN/TO ZZZ/041205/N1642.3E09612.5/450/12500/2800' | field flightId)
python3 -c 'import time; time.sleep(12)'
check "report still readable as PARSED" "PARSED" "$(curl -sS "$API/status/$UNK" | field status)"

DLQ=$(aws cloudformation describe-stacks --stack-name "$STACK" --region "$REGION" \
      --query "Stacks[0].Outputs[?OutputKey=='DeadLetterQueueUrl'].OutputValue" --output text 2>/dev/null)
if [ -n "$DLQ" ] && [ "$DLQ" != "None" ]; then
  DEPTH=$(aws sqs get-queue-attributes --queue-url "$DLQ" --region "$REGION" \
          --attribute-names ApproximateNumberOfMessages \
          --query 'Attributes.ApproximateNumberOfMessages' --output text 2>/dev/null)
  check "DLQ stayed empty" "0" "$DEPTH"
else
  echo "  SKIP  DLQ depth (queue URL not available)"
fi
echo

echo "-------------------------------------------"
printf 'passed %d, failed %d\n' "$PASS" "$FAIL"
[ "$FAIL" -eq 0 ] || exit 1
