#!/usr/bin/env bash
set -euo pipefail

printf '%s\n' \
    "Use Orca's version-matched orchestration guide before creating worktrees:" \
    "  orca-ide skills get orchestration" \
    "Task ownership files live in .agents/tasks/. The integration owner creates and merges all worktrees."
