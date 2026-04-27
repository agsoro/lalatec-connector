#!/usr/bin/env bash
# Find the correct entity hierarchy widget FQN/ID in this ThingsBoard instance
set -e

TB_URL="http://localhost:8080"
TOKEN=$(curl -s -X POST "${TB_URL}/api/auth/login" \
  -H 'Content-Type: application/json' \
  -d '{"username":"tenant@thingsboard.org","password":"tenant"}' \
  | python3 -c "import sys,json; print(json.load(sys.stdin)['token'])")

echo "=== Searching for hierarchy widget types ==="
curl -s "${TB_URL}/api/widgetTypes?pageSize=200&page=0&fullSearch=false" \
  -H "X-Authorization: Bearer $TOKEN" \
  | python3 -c "
import sys, json
data = json.load(sys.stdin)
items = data.get('data', [])
print(f'Total widget types: {len(items)}')
for w in items:
    name = w.get('name','')
    alias = w.get('alias','')
    fqn = w.get('fqn','')
    if any(x in (name+alias+fqn).lower() for x in ['hierarch','tree','entity']):
        print(f'  name={name!r}  alias={alias!r}  fqn={fqn!r}  id={w[\"id\"][\"id\"]}')
" 2>/dev/null || true

echo ""
echo "=== Searching widget bundles ==="
curl -s "${TB_URL}/api/widgetsBundles?pageSize=50&page=0" \
  -H "X-Authorization: Bearer $TOKEN" \
  | python3 -c "
import sys, json
data = json.load(sys.stdin)
for b in data.get('data',[]):
    print(f'  bundle={b[\"alias\"]!r}  title={b[\"title\"]!r}')
" 2>/dev/null || true
