#!/usr/bin/env python3
import json
d = json.load(open("/tmp/dashboard_raw.json"))
w = list(d["configuration"]["widgets"].values())[0]
print(json.dumps(w, indent=2))
print("\n=== gridSettings ===")
print(json.dumps(d["configuration"]["states"]["default"]["layouts"]["main"]["gridSettings"], indent=2))
