#!/usr/bin/env python3
"""Get exact widget type UUIDs for the widgets we need."""

import json, urllib.request

TB_URL  = "http://localhost:8080"
EMAIL   = "tenant@thingsboard.org"
PASSWORD = "tenant"

def post(path, body, token=None):
    data    = json.dumps(body).encode()
    headers = {"Content-Type": "application/json"}
    if token:
        headers["X-Authorization"] = f"Bearer {token}"
    req = urllib.request.Request(TB_URL + path, data=data, headers=headers, method="POST")
    with urllib.request.urlopen(req) as r:
        return json.loads(r.read())

def get(path, token):
    req = urllib.request.Request(TB_URL + path,
                                 headers={"X-Authorization": f"Bearer {token}"})
    with urllib.request.urlopen(req) as r:
        return json.loads(r.read())

token = post("/api/auth/login", {"username": EMAIL, "password": PASSWORD})["token"]

targets = [
    "cards.entities_hierarchy",
    "charts.basic_timeseries",
    "cards.attributes_card",
    "time_series_chart",
]

for page in range(5):
    data = get(f"/api/widgetTypes?pageSize=200&page={page}", token)
    for w in data.get("data", []):
        fqn = w.get("fqn", "")
        if fqn in targets:
            tid = w["id"]["id"]
            print(f"FQN={fqn!r}")
            print(f"  id={tid}")
            print(f"  name={w['name']!r}")
            print(f"  descriptor_type={w.get('descriptor',{}).get('type','?')!r}")
            print()
    if not data.get("hasNext"):
        break
