#!/usr/bin/env python3
"""Export an existing dashboard to understand its exact JSON structure."""
import json, urllib.request

TB_URL   = "http://localhost:8080"
EMAIL    = "tenant@thingsboard.org"
PASSWORD = "tenant"

def post(path, body, token=None):
    data    = json.dumps(body).encode()
    headers = {"Content-Type": "application/json"}
    if token: headers["X-Authorization"] = f"Bearer {token}"
    req = urllib.request.Request(TB_URL + path, data=data, headers=headers, method="POST")
    with urllib.request.urlopen(req) as r: return json.loads(r.read())

def get(path, token):
    req = urllib.request.Request(TB_URL + path, headers={"X-Authorization": f"Bearer {token}"})
    with urllib.request.urlopen(req) as r: return json.loads(r.read())

token = post("/api/auth/login", {"username": EMAIL, "password": PASSWORD})["token"]

# Get our broken dashboard
dash = get("/api/dashboard/0f1239c0-4075-11f1-a861-d3c145850c19", token)
cfg  = dash.get("configuration", {})

print("=== STATES ===")
for sname, state in cfg.get("states", {}).items():
    print(f"State: {sname}")
    for lname, layout in state.get("layouts", {}).items():
        print(f"  Layout: {lname}")
        for wid, wpos in layout.get("widgets", {}).items():
            print(f"    widget_id={wid!r}  pos={wpos}")

print("\n=== WIDGETS keys (first 5) ===")
widgets = cfg.get("widgets", {})
for k in list(widgets.keys())[:5]:
    w = widgets[k]
    print(f"  key={k!r}")
    print(f"    typeId={w.get('id')}")
    print(f"    bundleAlias={w.get('bundleAlias')!r}")
    print(f"    typeAlias={w.get('typeAlias')!r}")
    print(f"    sizeX={w.get('sizeX')} sizeY={w.get('sizeY')}")
    print()
