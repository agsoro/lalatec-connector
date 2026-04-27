#!/usr/bin/env bash
set -e
TB_URL="http://localhost:8080"
TOKEN=$(curl -s -X POST "${TB_URL}/api/auth/login" \
  -H 'Content-Type: application/json' \
  -d '{"username":"tenant@thingsboard.org","password":"tenant"}' \
  | python3 -c "import sys,json; print(json.load(sys.stdin)['token'])")

echo "=== All widget types (paginated) ==="
for PAGE in 0 1 2 3; do
  curl -s "${TB_URL}/api/widgetTypes?pageSize=200&page=${PAGE}&fullSearch=false" \
    -H "X-Authorization: Bearer $TOKEN" \
    | python3 -c "
import sys, json
data = json.load(sys.stdin)
items = data.get('data', [])
hasNext = data.get('hasNext', False)
print(f'Page ${PAGE}: {len(items)} items, hasNext={hasNext}')
for w in items:
    name = w.get('name','')
    fqn  = w.get('fqn','')
    if any(x in (name+fqn).lower() for x in ['hierarch','tree','relation','entit']):
        print(f'  FQN={fqn!r}  name={name!r}')
if not hasNext:
    import sys; sys.exit(1)
" 2>/dev/null && true || break
done

echo ""
echo "=== entity_widgets bundle widget types ==="
curl -s "${TB_URL}/api/widgetTypes?pageSize=200&page=0&fullSearch=false" \
  -H "X-Authorization: Bearer $TOKEN" \
  | python3 -c "
import sys,json
data = json.load(sys.stdin)
for w in data.get('data',[]):
    fqn = w.get('fqn','')
    if 'entity_widget' in fqn.lower():
        print(f'  FQN={fqn!r}  name={w[\"name\"]!r}')
"
