#!/usr/bin/env python3
"""Inspect what TB actually stored for our dashboard."""
import json, urllib.request

TB_URL   = "http://localhost:8080"
EMAIL    = "tenant@thingsboard.org"
PASSWORD = "tenant"
DASH_ID  = "0f1239c0-4075-11f1-a861-d3c145850c19"

def req(method, path, body=None, token=None):
    data    = json.dumps(body).encode() if body else None
    headers = {"Content-Type": "application/json"}
    if token: headers["X-Authorization"] = f"Bearer {token}"
    r = urllib.request.Request(TB_URL + path, data=data, headers=headers, method=method)
    with urllib.request.urlopen(r) as resp:
        return json.loads(resp.read())

token = req("POST", "/api/auth/login", {"username": EMAIL, "password": PASSWORD})["token"]
dash  = req("GET", f"/api/dashboard/{DASH_ID}", token=token)

cfg     = dash["configuration"]
widgets = cfg.get("widgets", {})
states  = cfg.get("states", {})

print(f"Widget count: {len(widgets)}")
print(f"State count:  {len(states)}")

layout = states.get("default", {}).get("layouts", {}).get("main", {}).get("widgets", {})
print(f"Layout entries: {len(layout)}")

for key, pos in layout.items():
    print(f"  layout[{key!r}] = {pos}")

print()
for key, w in widgets.items():
    print(f"  widget[{key!r}] typeAlias={w.get('typeAlias')} title={w.get('title')} sizeX={w.get('sizeX')} sizeY={w.get('sizeY')}")

# Save full config for analysis
with open("/tmp/dash_cfg.json", "w") as f:
    json.dump(cfg, f, indent=2)
print("\nFull config saved to /tmp/dash_cfg.json")
