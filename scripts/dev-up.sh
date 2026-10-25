#!/usr/bin/env bash
set -euo pipefail
cd "$(dirname "${BASH_SOURCE[0]}")/.."
command -v docker >/dev/null || { echo 'Docker with Compose v2 is required.' >&2; exit 1; }
if [[ ! -e .env ]]; then
  (umask 077; python3 - <<'PY' > .env
import secrets
print('TEAMMY_DEV_DB_PASSWORD=' + secrets.token_urlsafe(32))
PY
  )
  echo 'Created a private local development configuration.'
fi
docker compose up --build --detach
python3 scripts/smoke-api.py --base-url http://127.0.0.1:5080 --demo
echo 'Development API: http://127.0.0.1:5080; stop with docker compose down.'
echo 'The named database volume persists. docker compose down --volumes deletes its data.'
