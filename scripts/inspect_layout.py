#!/usr/bin/env python3
"""Export an existing real dashboard that has widgets to see the correct layout format."""
import json, urllib.request

TB_URL   = "http://localhost:8080"
EMAIL    = "tenant@thingsboard.org"
PASSWORD = "tenant"

def req(method, path, body=None, token=None):
    data    = json.dumps(body).encode() if body else None
    headers = {"Content-Type": "application/json"}
    if token: headers["X-Authorization"] = f"Bearer {token}"
    r = urllib.request.Request(TB_URL + path, data=data, headers=headers, method=method)
    with urllib.request.urlopen(r) as resp:
        return json.loads(resp.read())

token = req("POST", "/api/auth/login", {"username": EMAIL, "password": PASSWORD})["token"]

# Get our broken dashboard and dump the raw layout widget entries
dash = req("GET", f"/api/dashboard/0f1239c0-4075-11f1-a861-d3c145850c19", token=token)
cfg = dash["configuration"]

print("=== Layout widget positions ===")
for wid, pos in cfg["states"]["default"]["layouts"]["main"]["widgets"].items():
    print(f"  {wid}: {json.dumps(pos)}")

print("\n=== First widget config keys ===")
for wid, w in list(cfg.get("widgets", {}).items())[:1]:
    print(f"  widget keys: {list(w.keys())}")
    print(f"  config keys: {list(w.get('config', {}).keys())}")
