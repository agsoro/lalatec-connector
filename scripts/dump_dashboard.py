#!/usr/bin/env python3
"""Dump full raw dashboard JSON for inspection."""
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
dash  = req("GET", f"/api/dashboard/0f1239c0-4075-11f1-a861-d3c145850c19", token=token)

with open("/tmp/dashboard_raw.json", "w") as f:
    json.dump(dash, f, indent=2)
print("Saved to /tmp/dashboard_raw.json")
print(f"gridSettings: {dash['configuration']['states']['default']['layouts']['main']['gridSettings']}")
