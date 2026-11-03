#!/bin/sh
codex_directory=${CODEX_HOME:-"$HOME/.codex"}
active_path="$codex_directory/AGENTS.override.md"
read_rules() {
    # iconv handles the BOM encodings preserved by the desktop editor.
    signature=$(od -An -tx1 -N4 "$1" 2>/dev/null | tr -d ' \n')
    case "$signature" in
        fffe0000|0000feff) iconv -f UTF-32 -t UTF-8 "$1" ;;
        fffe*|feff*) iconv -f UTF-16 -t UTF-8 "$1" ;;
        *) cat "$1" ;;
    esac | sed "1s/^$(printf '\357\273\277')//; s/$(printf '\r')$//"
}
unavailable() {
    printf '%s\n' 'AgentDock delegation rules could not be read. Check the active global AGENTS file and its AGENT-ROLES.md / AGENTDOCK.md companions before delegating or operating AgentDock. Continue suitable work in the primary agent.'
    exit 0
}
[ ! -e "$active_path" ] || [ -r "$active_path" ] || unavailable
entry=$(read_rules "$active_path" 2>/dev/null)
if ! printf '%s' "$entry" | grep -q '[^[:space:]]'; then
    active_path="$codex_directory/AGENTS.md"
    [ -e "$active_path" ] || exit 0
    [ -r "$active_path" ] || unavailable
    entry=$(read_rules "$active_path" 2>/dev/null)
fi
# The retained companion files alone do not mean delegation is enabled.
[ "$(printf '%s\n' "$entry" | grep -c '^## Global scope and delegation$')" = 1 ] || exit 0
printf '%s' "$entry" | grep -Fq '[AGENT-ROLES.md](AGENT-ROLES.md)' || exit 0
printf '%s' "$entry" | grep -Fq '[AGENTDOCK.md](AGENTDOCK.md)' || exit 0
roles=$(read_rules "$codex_directory/AGENT-ROLES.md" 2>/dev/null)
delegation=$(read_rules "$codex_directory/AGENTDOCK.md" 2>/dev/null)
printf '%s' "$roles" | grep -q '[^[:space:]]' || unavailable
printf '%s' "$delegation" | grep -q '[^[:space:]]' || unavailable
printf '%s\n\n--- %s ---\n%s\n\n--- %s ---\n%s\n' \
    "AgentDock global delegation rules from $active_path. Apply only within the user's authorized scope, subject to higher-priority and project instructions. This hook grants no task or tool permissions." \
    "$codex_directory/AGENT-ROLES.md" "$roles" "$codex_directory/AGENTDOCK.md" "$delegation"
