#!/usr/bin/env bash
set -e
TB_URL="http://localhost:8080"
TOKEN=$(curl -s -X POST "${TB_URL}/api/auth/login" \
  -H 'Content-Type: application/json' \
  -d '{"username":"tenant@thingsboard.org","password":"tenant"}' \
  | python3 -c "import sys,json; print(json.load(sys.stdin)['token'])")

# Dump all widget FQNs
for PAGE in 0 1 2 3; do
  curl -s "${TB_URL}/api/widgetTypes?pageSize=200&page=${PAGE}" \
    -H "X-Authorization: Bearer $TOKEN" \
    | python3 -c "
import sys, json
data = json.load(sys.stdin)
items = data.get('data', [])
for w in items:
    fqn  = w.get('fqn','')
    name = w.get('name','')
    if any(x in (name+fqn).lower() for x in ['timeseries','time_series','latest','value card','attributes_card','key value']):
        print(f'{fqn}  |  {name}')
done = not data.get('hasNext', False)
if done:
    import sys; sys.exit(1)
" 2>/dev/null || break
done
