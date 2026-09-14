#!/usr/bin/env bash
# One-time setup - NOT part of the per-commit pipeline. Run this once (and
# again only if the thresholds change) against your SonarCloud/SonarQube
# organization to create and assign a custom Quality Gate matching:
#   - fail if coverage < 70%
#   - fail if there is any code smell
#
# Usage:
#   SONAR_HOST_URL=https://sonarcloud.io \
#   SONAR_TOKEN=xxxxx \
#   SONAR_PROJECT_KEY=purushottam17031983_PracticeConsoleApp \
#   ./scripts/setup-sonar-quality-gate.sh

set -euo pipefail

: "${SONAR_HOST_URL:?Set SONAR_HOST_URL, e.g. https://sonarcloud.io}"
: "${SONAR_TOKEN:?Set SONAR_TOKEN to a token with Administer Quality Gates permission}"
: "${SONAR_PROJECT_KEY:?Set SONAR_PROJECT_KEY to the project key used by the scanner (sonar.projectKey)}"

GATE_NAME="PracticeConsoleApp-Gate"

api() {
  curl -sf -u "${SONAR_TOKEN}:" "${SONAR_HOST_URL}/api/$1" "${@:2}"
}

echo "Creating quality gate '${GATE_NAME}' (ignored if it already exists)..."
api "qualitygates/create" -X POST --data-urlencode "name=${GATE_NAME}" >/dev/null || true

echo "Adding condition: coverage < 70 fails the gate..."
api "qualitygates/create_condition" -X POST \
  --data-urlencode "gateName=${GATE_NAME}" \
  --data-urlencode "metric=coverage" \
  --data-urlencode "op=LT" \
  --data-urlencode "error=70" >/dev/null || true

echo "Adding condition: any code smell fails the gate..."
api "qualitygates/create_condition" -X POST \
  --data-urlencode "gateName=${GATE_NAME}" \
  --data-urlencode "metric=code_smells" \
  --data-urlencode "op=GT" \
  --data-urlencode "error=0" >/dev/null || true

echo "Assigning '${GATE_NAME}' to project '${SONAR_PROJECT_KEY}'..."
api "qualitygates/select" -X POST \
  --data-urlencode "gateName=${GATE_NAME}" \
  --data-urlencode "projectKey=${SONAR_PROJECT_KEY}" >/dev/null

echo "Done. '${SONAR_PROJECT_KEY}' now fails analysis when coverage < 70% or any code smell is present."
