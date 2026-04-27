#!/usr/bin/env python3
"""List all dashboards and export the first one that's NOT ours (to see TB's own format)."""
import json, urllib.request

TB_URL   = "http://localhost:8080"
EMAIL    = "tenant@thingsboard.org"
PASSWORD = "tenant"
OUR_DASH = "0f1239c0-4075-11f1-a861-d3c145850c19"

def req(method, path, body=None, token=None):
    data    = json.dumps(body).encode() if body else None
    headers = {"Content-Type": "application/json"}
    if token: headers["X-Authorization"] = f"Bearer {token}"
    r = urllib.request.Request(TB_URL + path, data=data, headers=headers, method=method)
    with urllib.request.urlopen(r) as resp:
        return json.loads(resp.read())

token = req("POST", "/api/auth/login", {"username": EMAIL, "password": PASSWORD})["token"]
all_dashes = req("GET", "/api/tenant/dashboards?pageSize=20&page=0", token=token)

print("All dashboards:")
for d in all_dashes["data"]:
    did = d["id"]["id"]
    print(f"  {did}  {d['title']}")

# Export the first one that TB might have made (look for non-ours)
for d in all_dashes["data"]:
    did = d["id"]["id"]
    if did != OUR_DASH:
        dash = req("GET", f"/api/dashboard/{did}", token=token)
        cfg  = dash["configuration"]
        widgets = cfg.get("widgets", {})
        if widgets:
            print(f"\n=== Exporting '{d['title']}' (has {len(widgets)} widgets) ===")
            # Show layout of first widget
            for state in cfg.get("states", {}).values():
                for layout in state.get("layouts", {}).values():
                    for wid, pos in list(layout.get("widgets", {}).items())[:1]:
                        print(f"Layout pos: {json.dumps(pos)}")
            # Show first widget structure
            first_w = list(widgets.values())[0]
            print(f"Widget top-level keys: {list(first_w.keys())}")
            with open("/tmp/sample_dash.json", "w") as f:
                json.dump(dash, f, indent=2)
            print("Saved to /tmp/sample_dash.json")
            break
