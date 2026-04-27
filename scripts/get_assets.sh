#!/usr/bin/env bash
set -e
TOKEN=$(curl -s -X POST http://localhost:8080/api/auth/login \
  -H 'Content-Type: application/json' \
  -d '{"username":"tenant@thingsboard.org","password":"tenant"}' \
  | python3 -c "import sys,json; print(json.load(sys.stdin)['token'])")

echo "JWT: $TOKEN"
echo ""
echo "=== Assets ==="
curl -s "http://localhost:8080/api/tenant/assets?pageSize=30&page=0" \
  -H "X-Authorization: Bearer $TOKEN" \
  | python3 -c "
import sys, json
data = json.load(sys.stdin)
for a in data['data']:
    print(a['id']['id'], '|', a['name'])
"
