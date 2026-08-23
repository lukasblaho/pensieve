#!/usr/bin/env bash
#
# Pensieve macOS installer
#
# Downloads Pensieve from GitHub, installs prerequisites (Homebrew, .NET SDK,
# dotnet-script, GitHub Copilot CLI), interactively configures .env, and
# installs/loads a launchd agent so Pensieve runs in the background.
#
# Usage:
#   curl -fsSL https://raw.githubusercontent.com/lukasblaho/pensieve/master/install.sh | bash
#   # or, after cloning the repo:
#   ./install.sh
#
# Environment variables (all optional — you'll be prompted interactively for
# anything not already set, unless --non-interactive is passed):
#   PENSIEVE_REPO_URL   Git URL to clone (default: https://github.com/lukasblaho/pensieve.git)
#   PENSIEVE_INSTALL_DIR  Where to clone/update the app (default: $HOME/pensieve)
#   PENSIEVE_RUN_MODE    "watch" or "run" (default: watch) — used by the launchd agent
#   WATCH_FOLDER, SUMMARY_FOLDER, OUTPUT_DIR, POLL_INTERVAL_MINUTES  — pre-fill .env values
#
# Flags:
#   --non-interactive   Never prompt; use env vars / .env.example defaults as-is
#   --skip-launchd      Install and configure, but don't create/load the launchd agent
#   --skip-clone        Assume the repo is already present at PENSIEVE_INSTALL_DIR (or CWD)

set -euo pipefail

# --------------------------------------------------------------------------
# Helpers
# --------------------------------------------------------------------------

log()  { printf '\033[1;34m[pensieve-install]\033[0m %s\n' "$1"; }
warn() { printf '\033[1;33m[pensieve-install][warn]\033[0m %s\n' "$1"; }
err()  { printf '\033[1;31m[pensieve-install][error]\033[0m %s\n' "$1" >&2; }
die()  { err "$1"; exit 1; }

NON_INTERACTIVE=false
SKIP_LAUNCHD=false
SKIP_CLONE=false

for arg in "$@"; do
  case "$arg" in
    --non-interactive) NON_INTERACTIVE=true ;;
    --skip-launchd) SKIP_LAUNCHD=true ;;
    --skip-clone) SKIP_CLONE=true ;;
    *) warn "Unknown argument: $arg" ;;
  esac
done

prompt() {
  # prompt <var_name> <question> <default>
  local __var="$1" __question="$2" __default="$3" __answer
  if [ "$NON_INTERACTIVE" = true ] || [ ! -t 0 ]; then
    printf -v "$__var" '%s' "${!__var:-$__default}"
    return
  fi
  if [ -n "${!__var:-}" ]; then
    __default="${!__var}"
  fi
  read -r -p "$__question [$__default]: " __answer </dev/tty || __answer=""
  printf -v "$__var" '%s' "${__answer:-$__default}"
}

confirm() {
  # confirm <question> <default: y|n>
  local question="$1" default="${2:-y}" answer
  if [ "$NON_INTERACTIVE" = true ] || [ ! -t 0 ]; then
    [ "$default" = "y" ] && return 0 || return 1
  fi
  local hint="y/N"; [ "$default" = "y" ] && hint="Y/n"
  read -r -p "$question [$hint]: " answer </dev/tty || answer=""
  answer="${answer:-$default}"
  [[ "$answer" =~ ^[Yy] ]]
}

# --------------------------------------------------------------------------
# 0. Sanity checks
# --------------------------------------------------------------------------

[ "$(uname -s)" = "Darwin" ] || die "This installer only supports macOS."

REPO_URL="${PENSIEVE_REPO_URL:-https://github.com/lukasblaho/pensieve.git}"
INSTALL_DIR="${PENSIEVE_INSTALL_DIR:-$HOME/pensieve}"
RUN_MODE="${PENSIEVE_RUN_MODE:-watch}"

log "Installing Pensieve to: $INSTALL_DIR"

# --------------------------------------------------------------------------
# 1. Homebrew
# --------------------------------------------------------------------------

if ! command -v brew >/dev/null 2>&1; then
  log "Homebrew not found — installing..."
  /bin/bash -c "$(curl -fsSL https://raw.githubusercontent.com/Homebrew/install/HEAD/install.sh)"
  if [ -x /opt/homebrew/bin/brew ]; then
    eval "$(/opt/homebrew/bin/brew shellenv)"
  elif [ -x /usr/local/bin/brew ]; then
    eval "$(/usr/local/bin/brew shellenv)"
  fi
else
  log "Homebrew already installed ($(brew --version | head -1))."
fi

BREW_PREFIX="$(brew --prefix)"

# --------------------------------------------------------------------------
# 2. .NET SDK (8.0+)
# --------------------------------------------------------------------------

if ! command -v dotnet >/dev/null 2>&1; then
  log ".NET SDK not found — installing via Homebrew..."
  brew install dotnet
else
  log ".NET SDK already installed ($(dotnet --version))."
fi

# --------------------------------------------------------------------------
# 3. dotnet-script
# --------------------------------------------------------------------------

export PATH="$PATH:$HOME/.dotnet/tools"

if ! command -v dotnet-script >/dev/null 2>&1; then
  log "dotnet-script not found — installing as a global dotnet tool..."
  dotnet tool install -g dotnet-script
else
  log "dotnet-script already installed."
fi

# --------------------------------------------------------------------------
# 4. GitHub Copilot CLI
# --------------------------------------------------------------------------

if ! command -v copilot >/dev/null 2>&1; then
  log "GitHub Copilot CLI not found — installing via Homebrew cask..."
  brew install --cask copilot-cli
else
  log "GitHub Copilot CLI already installed ($(copilot --version 2>/dev/null || echo 'version unknown'))."
fi

if [ "$NON_INTERACTIVE" = true ]; then
  warn "Non-interactive mode: skipping 'copilot login' (it requires an interactive browser device-code flow). Run 'copilot login' manually before using Pensieve."
elif confirm "Log in to GitHub Copilot CLI now (opens browser device-code flow)?" "y"; then
  copilot login || warn "copilot login did not complete — you can run 'copilot login' manually later."
else
  warn "Skipping 'copilot login' — Pensieve will fail to analyze transcripts until you run it."
fi

# --------------------------------------------------------------------------
# 5. Clone / update source
# --------------------------------------------------------------------------

if [ "$SKIP_CLONE" = true ]; then
  [ -f "$INSTALL_DIR/main.csx" ] || die "--skip-clone given but $INSTALL_DIR/main.csx not found."
  log "Skipping clone, using existing checkout at $INSTALL_DIR."
elif [ -d "$INSTALL_DIR/.git" ]; then
  log "Existing checkout found at $INSTALL_DIR — pulling latest changes..."
  git -C "$INSTALL_DIR" pull --ff-only
else
  command -v git >/dev/null 2>&1 || { log "git not found — installing..."; brew install git; }
  log "Cloning $REPO_URL into $INSTALL_DIR..."
  git clone "$REPO_URL" "$INSTALL_DIR"
fi

cd "$INSTALL_DIR"

# --------------------------------------------------------------------------
# 6. Configure .env
# --------------------------------------------------------------------------

if [ -f .env ]; then
  log ".env already exists — leaving it untouched. Delete it first to reconfigure via this installer."
else
  [ -f .env.example ] || die ".env.example not found in $INSTALL_DIR."
  cp .env.example .env
  log "Created .env from .env.example. Let's fill in the essentials:"

  prompt WATCH_FOLDER "Folder where Fireflies drops transcript .md files (Transcripts/)" "$HOME/Fireflies/Transcripts"
  prompt SUMMARY_FOLDER "Sibling Fireflies Summaries/ folder (optional, blank to skip)" ""
  prompt OUTPUT_DIR "Where should generated meeting notes go?" "./notes"
  prompt POLL_INTERVAL_MINUTES "Poll interval in minutes (used by 'run' mode)" "15"

  # Portable in-place sed for both GNU and BSD sed
  sedi() { sed -i.bak "$1" "$2" && rm -f "$2.bak"; }

  ESCAPED_WATCH_FOLDER=$(printf '%s' "$WATCH_FOLDER" | sed 's/[&/\]/\\&/g')
  sedi "s#^WATCH_FOLDER=.*#WATCH_FOLDER=${ESCAPED_WATCH_FOLDER}#" .env

  if [ -n "$SUMMARY_FOLDER" ]; then
    ESCAPED_SUMMARY_FOLDER=$(printf '%s' "$SUMMARY_FOLDER" | sed 's/[&/\]/\\&/g')
    sedi "s#^SUMMARY_FOLDER=.*#SUMMARY_FOLDER=${ESCAPED_SUMMARY_FOLDER}#" .env
  fi

  ESCAPED_OUTPUT_DIR=$(printf '%s' "$OUTPUT_DIR" | sed 's/[&/\]/\\&/g')
  sedi "s#^OUTPUT_DIR=.*#OUTPUT_DIR=${ESCAPED_OUTPUT_DIR}#" .env
  sedi "s#^POLL_INTERVAL_MINUTES=.*#POLL_INTERVAL_MINUTES=${POLL_INTERVAL_MINUTES}#" .env

  if confirm "Enable macOS Notification Center alerts when a meeting finishes processing?" "n"; then
    sedi "s#^ENABLE_MACOS_NOTIFICATIONS=.*#ENABLE_MACOS_NOTIFICATIONS=true#" .env
  fi

  log ".env configured. Review $INSTALL_DIR/.env for all other options (Fireflies API, Obsidian/Notion export, auto-delete)."
fi

mkdir -p "$INSTALL_DIR/data/logs"

# --------------------------------------------------------------------------
# 7. Run tests as a sanity check
# --------------------------------------------------------------------------

if confirm "Run the test suite now to verify the install?" "y"; then
  log "Running tests..."
  dotnet script tests/run-tests.csx || die "Tests failed — fix the environment before proceeding."
fi

# --------------------------------------------------------------------------
# 8. launchd agent
# --------------------------------------------------------------------------

if [ "$SKIP_LAUNCHD" = true ]; then
  log "Skipping launchd setup (--skip-launchd)."
else
  if [[ "$RUN_MODE" != "watch" && "$RUN_MODE" != "run" ]]; then
    warn "Invalid PENSIEVE_RUN_MODE '$RUN_MODE' — defaulting to 'watch'."
    RUN_MODE="watch"
  fi

  DOTNET_SCRIPT_PATH="$(command -v dotnet-script)"
  DOTNET_TOOLS_DIR="$(dirname "$DOTNET_SCRIPT_PATH")"
  PLIST_LABEL="com.pensieve"
  PLIST_PATH="$HOME/Library/LaunchAgents/${PLIST_LABEL}.plist"

  mkdir -p "$HOME/Library/LaunchAgents"

  cat > "$PLIST_PATH" <<PLIST
<?xml version="1.0" encoding="UTF-8"?>
<!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN"
  "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
<plist version="1.0">
<dict>
  <key>Label</key>
  <string>${PLIST_LABEL}</string>

  <key>ProgramArguments</key>
  <array>
    <string>${DOTNET_SCRIPT_PATH}</string>
    <string>main.csx</string>
    <string>--</string>
    <string>${RUN_MODE}</string>
  </array>

  <key>WorkingDirectory</key>
  <string>${INSTALL_DIR}</string>

  <key>EnvironmentVariables</key>
  <dict>
    <key>PATH</key>
    <string>${BREW_PREFIX}/bin:${DOTNET_TOOLS_DIR}:/usr/bin:/bin</string>
  </dict>

  <key>RunAtLoad</key>
  <true/>
  <key>KeepAlive</key>
  <true/>

  <key>StandardOutPath</key>
  <string>${INSTALL_DIR}/data/logs/launchd.out.log</string>
  <key>StandardErrorPath</key>
  <string>${INSTALL_DIR}/data/logs/launchd.err.log</string>
</dict>
</plist>
PLIST

  log "Wrote launchd agent to $PLIST_PATH (mode: $RUN_MODE)."

  SHOULD_LOAD=false
  if [ "$NON_INTERACTIVE" = true ]; then
    # Loading starts a real background process immediately — never do this
    # automatically in non-interactive mode; require explicit opt-in.
    if [ "${PENSIEVE_AUTO_LOAD_LAUNCHD:-false}" = "true" ]; then
      SHOULD_LOAD=true
    else
      log "Non-interactive mode: agent written but not loaded (set PENSIEVE_AUTO_LOAD_LAUNCHD=true to auto-load)."
    fi
  elif confirm "Load the launchd agent now so Pensieve starts running in the background?" "y"; then
    SHOULD_LOAD=true
  fi

  if [ "$SHOULD_LOAD" = true ]; then
    launchctl unload "$PLIST_PATH" >/dev/null 2>&1 || true
    launchctl load "$PLIST_PATH"
    log "launchd agent loaded. Check status with: launchctl list | grep ${PLIST_LABEL}"
    log "Logs: tail -f '${INSTALL_DIR}/data/logs/launchd.out.log'"
  else
    log "Agent written but not loaded. Load it later with: launchctl load '$PLIST_PATH'"
  fi

  log "To stop/uninstall the background agent: launchctl unload '$PLIST_PATH' && rm '$PLIST_PATH'"
fi

log "Done. Pensieve is installed at: $INSTALL_DIR"
log "Manual run commands: dotnet script main.csx -- sync | run | watch"
