#!/usr/bin/env bash
# Explicitly discard only a labelled demo database, retaining TLS and logs.
# An optional --env-file selects credentials; project and deployment files are fixed.
set -euo pipefail

if [[ "${1:-}" != "--confirm-demo-reset" ]]; then
  echo "Usage: $0 --confirm-demo-reset [--dokploy] [--env-file PATH]" >&2
  echo "Stops the demo and permanently replaces its database; preserves TLS volumes." >&2
  exit 2
fi
shift
cd "$(dirname "$0")/.."
restart=true
files=(-f compose.yaml -f compose.demo.yaml)
if [[ "${1:-}" == "--dokploy" ]]; then
  shift
  files=(-f compose.dokploy.yaml)
  restart=false
fi
settings_file=""
if (( $# > 0 )); then
  if [[ $# != 2 || "$1" != "--env-file" || -z "$2" || "$2" == -* ]]; then
    echo "Only --env-file PATH is accepted; project and deployment files cannot be overridden." >&2
    exit 2
  fi
  settings_file="$2"
fi
project="${DEMO_PROJECT_NAME:-countflow-demo}"
if [[ ! "$project" =~ ^[a-z0-9][a-z0-9_-]*$ ]]; then
  echo "Invalid demo project name." >&2
  exit 2
fi

# Require both Compose ownership and our explicit demo marker. Never infer a
# volume from a name alone; a project without this marker is not ours to reset.
database=$(docker volume ls \
  --filter "label=com.docker.compose.project=$project" \
  --filter "label=com.docker.compose.volume=postgres-data" \
  --filter "label=countflow.demo=true" --format '{{.Name}}')
if [[ -z "$database" || "$database" == *$'\n'* ]]; then
  echo "Expected exactly one labelled demo database for $project; refusing reset." >&2
  exit 1
fi

compose=(docker compose --project-name "$project" "${files[@]}")
if [[ -n "$settings_file" ]]; then
  compose+=(--env-file "$settings_file")
fi
"${compose[@]}" down
docker volume rm "$database"
if $restart; then
  "${compose[@]}" up -d --build
else
  echo "Demo database removed. Redeploy from Dokploy to restore its domains and networks."
fi
