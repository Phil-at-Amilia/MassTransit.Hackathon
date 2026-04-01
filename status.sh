#!/usr/bin/env bash
# =============================================================================
# status.sh  —  RabbitMQ × MassTransit live status dashboard
#
# Usage:
#   ./status.sh                  # one-shot
#   watch -n 5 ./status.sh       # auto-refresh every 5 s
#
# Requires: curl, jq
# RabbitMQ must be running: docker-compose up -d
# =============================================================================

set -euo pipefail

RABBIT_HOST="${RABBIT_HOST:-localhost}"
RABBIT_PORT="${RABBIT_PORT:-15672}"
RABBIT_USER="${RABBIT_USER:-guest}"
RABBIT_PASS="${RABBIT_PASS:-guest}"
BASE_URL="http://${RABBIT_HOST}:${RABBIT_PORT}/api"

# ── colour helpers ────────────────────────────────────────────────────────────
RED='\033[0;31m'; GREEN='\033[0;32m'; YELLOW='\033[1;33m'
CYAN='\033[0;36m'; BOLD='\033[1m'; DIM='\033[2m'; RESET='\033[0m'

# ── fetch helper (exits with a friendly message when broker is unreachable) ───
fetch() {
  local url="$1"
  local result
  result=$(curl --silent --fail --max-time 4 \
    -u "${RABBIT_USER}:${RABBIT_PASS}" \
    "${url}" 2>/dev/null)
  local rc=$?
  if [ $rc -ne 0 ]; then
    echo "" >&2
    echo -e "${RED}  ✗  Cannot reach RabbitMQ at ${BASE_URL}${RESET}" >&2
    echo -e "${DIM}     Is the container running?  →  docker-compose up -d${RESET}" >&2
    echo "" >&2
    exit 1
  fi
  echo "$result"
}

# ── Check dependencies ────────────────────────────────────────────────────────
for cmd in curl jq; do
  if ! command -v "$cmd" &>/dev/null; then
    echo -e "${RED}  ✗  '$cmd' is not installed. Please install it first.${RESET}"
    exit 1
  fi
done

# ── Fetch data in parallel ────────────────────────────────────────────────────
OVERVIEW=$(fetch "${BASE_URL}/overview")
QUEUES=$(fetch "${BASE_URL}/queues")
EXCHANGES=$(fetch "${BASE_URL}/exchanges")
CONNECTIONS=$(fetch "${BASE_URL}/connections")

# ── Parse overview fields ─────────────────────────────────────────────────────
BROKER_NAME=$(echo "$OVERVIEW"  | jq -r '.node                  // "unknown"')
RMQ_VERSION=$(echo "$OVERVIEW"  | jq -r '.rabbitmq_version      // "?"')
ERLANG_VER=$(echo "$OVERVIEW"   | jq -r '.erlang_version        // "?"')
UPTIME_MS=$(echo "$OVERVIEW"    | jq -r '.node_details[0].uptime // 0')
UPTIME_MIN=$(( UPTIME_MS / 60000 ))

TOTAL_CONN=$(echo "$CONNECTIONS" | jq 'length')
TOTAL_CHAN=$(echo "$OVERVIEW"    | jq -r '.churn_rates.channel_created // .object_totals.channels // 0')
TOTAL_EXCH=$(echo "$OVERVIEW"   | jq -r '.object_totals.exchanges   // 0')
TOTAL_Q=$(echo "$OVERVIEW"      | jq -r '.object_totals.queues      // 0')

MSG_READY=$(echo "$OVERVIEW"    | jq -r '.queue_totals.messages_ready         // 0')
MSG_UNACK=$(echo "$OVERVIEW"    | jq -r '.queue_totals.messages_unacknowledged // 0')
MSG_TOTAL=$(echo "$OVERVIEW"    | jq -r '.queue_totals.messages               // 0')

MSGS_IN=$(echo "$OVERVIEW"  | jq -r '.message_stats.publish_details.rate  // 0')
MSGS_OUT=$(echo "$OVERVIEW" | jq -r '.message_stats.deliver_details.rate  // 0')

# ── Print banner ──────────────────────────────────────────────────────────────
clear 2>/dev/null || true
echo ""
echo -e "${BOLD}${CYAN}╔══════════════════════════════════════════════════════════╗${RESET}"
echo -e "${BOLD}${CYAN}║   🐇  RabbitMQ  ×  🚌  MassTransit  —  Status Board     ║${RESET}"
echo -e "${BOLD}${CYAN}╚══════════════════════════════════════════════════════════╝${RESET}"
echo ""
echo -e "  ${YELLOW}(\\ /)${RESET}   ${BOLD}Broker :${RESET} ${BROKER_NAME}"
echo -e "  ${YELLOW}( •.•)${RESET}  ${BOLD}Version:${RESET} RabbitMQ ${RMQ_VERSION}  |  Erlang ${ERLANG_VER}"
echo -e "  ${YELLOW}o(\")(\"${RESET}  ${BOLD}Uptime :${RESET} ${UPTIME_MIN} minute(s)"
echo ""

# ── Overview ──────────────────────────────────────────────────────────────────
echo -e "${BOLD}────────────────────────────── OVERVIEW ─────────────────────────────${RESET}"
printf "  %-14s ${GREEN}%-6s${RESET}   %-14s ${GREEN}%-6s${RESET}\n" \
  "Connections:" "$TOTAL_CONN" "Channels:"  "$TOTAL_CHAN"
printf "  %-14s ${GREEN}%-6s${RESET}   %-14s ${GREEN}%-6s${RESET}\n" \
  "Exchanges:"  "$TOTAL_EXCH" "Queues:"    "$TOTAL_Q"
printf "  %-14s ${GREEN}%-6s${RESET}   %-14s ${GREEN}%-6s${RESET}   %-14s ${GREEN}%-6s${RESET}\n" \
  "Msgs ready:" "$MSG_READY" "Unacked:"   "$MSG_UNACK"  "Total:"     "$MSG_TOTAL"
printf "  %-14s ${CYAN}%-8s${RESET}  %-14s ${CYAN}%-8s${RESET}\n" \
  "Publish/s:"  "${MSGS_IN}/s" "Deliver/s:" "${MSGS_OUT}/s"
echo ""

# ── Queues ────────────────────────────────────────────────────────────────────
echo -e "${BOLD}──────────────────────────── QUEUES ─────────────────────────────────${RESET}"
printf "  ${BOLD}%-40s %7s  %7s  %9s  %-8s${RESET}\n" \
  "NAME" "READY" "UNACKED" "CONSUMERS" "STATE"

Q_COUNT=$(echo "$QUEUES" | jq 'length')

if [ "$Q_COUNT" -eq 0 ]; then
  echo -e "  ${DIM}(no queues yet — start the app with: dotnet run)${RESET}"
else
  echo "$QUEUES" | jq -r '.[] |
    [ .name,
      (.messages_ready         // 0 | tostring),
      (.messages_unacknowledged // 0 | tostring),
      (.consumers              // 0 | tostring),
      (.state                  // "unknown")
    ] | @tsv' | \
  while IFS=$'\t' read -r name ready unacked consumers state; do
    # colour the state
    if [ "$state" = "running" ]; then
      state_col="${GREEN}${state}${RESET}"
    else
      state_col="${RED}${state}${RESET}"
    fi
    printf "  %-40s %7s  %7s  %9s  %b\n" \
      "$name" "$ready" "$unacked" "$consumers" "$state_col"
  done
fi
echo ""

# ── Consumers (derived from queue data) ──────────────────────────────────────
echo -e "${BOLD}──────────────────────────── CONSUMERS ──────────────────────────────${RESET}"

if [ "$Q_COUNT" -eq 0 ]; then
  echo -e "  ${DIM}(no consumers yet)${RESET}"
else
  FOUND_CONSUMERS=false
  while IFS=$'\t' read -r name consumers; do
    if [ "$consumers" -gt 0 ]; then
      FOUND_CONSUMERS=true
      echo -e "  ${GREEN}✔${RESET}  ${BOLD}${name}${RESET}  →  ${GREEN}${consumers}${RESET} active consumer(s)"
    fi
  done < <(echo "$QUEUES" | jq -r '.[] | [.name, (.consumers // 0 | tostring)] | @tsv')

  if [ "$FOUND_CONSUMERS" = false ]; then
    echo -e "  ${YELLOW}⚠${RESET}  No active consumers (is the app running? →  dotnet run)"
  fi
fi
echo ""

# ── Exchanges (non-default, user-created) ─────────────────────────────────────
echo -e "${BOLD}──────────────────────────── EXCHANGES ──────────────────────────────${RESET}"
printf "  ${BOLD}%-50s %-8s %-3s${RESET}\n" "NAME" "TYPE" "D"

USER_EXCHANGES=$(echo "$EXCHANGES" | jq '[.[] | select(.name != "" and .internal == false)]')
EX_COUNT=$(echo "$USER_EXCHANGES" | jq 'length')

if [ "$EX_COUNT" -eq 0 ]; then
  echo -e "  ${DIM}(only default exchanges exist — start the app to create MassTransit exchanges)${RESET}"
else
  echo "$USER_EXCHANGES" | jq -r '.[] |
    [ .name,
      .type,
      (if .durable then "D" else "-" end)
    ] | @tsv' | \
  while IFS=$'\t' read -r name type durable; do
    printf "  %-50s ${CYAN}%-8s${RESET} %s\n" "$name" "$type" "$durable"
  done
fi
echo ""

# ── Connections ───────────────────────────────────────────────────────────────
echo -e "${BOLD}──────────────────────────── CONNECTIONS ────────────────────────────${RESET}"

if [ "$TOTAL_CONN" -eq 0 ]; then
  echo -e "  ${YELLOW}⚠${RESET}  No active connections (is the app running? →  dotnet run)"
else
  printf "  ${BOLD}%-30s %-12s %-10s${RESET}\n" "CLIENT" "STATE" "CHANNELS"
  echo "$CONNECTIONS" | jq -r '.[] |
    [ (.client_properties.product // .name // "unknown"),
      (.state   // "?"),
      (.channels // 0 | tostring)
    ] | @tsv' | \
  while IFS=$'\t' read -r client state channels; do
    if [ "$state" = "running" ]; then
      state_col="${GREEN}${state}${RESET}"
    else
      state_col="${YELLOW}${state}${RESET}"
    fi
    printf "  %-30s %b%-12s${RESET} %-10s\n" \
      "${client:0:30}" "$state_col" "" "$channels"
  done
fi

echo ""
echo -e "${DIM}  Management UI → http://${RABBIT_HOST}:15672  (guest / guest)${RESET}"
echo -e "${DIM}  Refreshed at $(date '+%Y-%m-%d %H:%M:%S')${RESET}"
echo ""
