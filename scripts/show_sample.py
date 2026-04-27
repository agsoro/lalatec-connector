#!/usr/bin/env python3
import json

dash = json.load(open("/tmp/sample_dash.json"))
cfg  = dash["configuration"]
widgets = cfg.get("widgets", {})

for wid, w in list(widgets.items())[:3]:
    print(f"=== Widget: {w.get('type')} ===")
    print(f"  keys: {list(w.keys())}")
    print(f"  typeFullFqn: {w.get('typeFullFqn')}")
    print(f"  id: {w.get('id')}")
    print(f"  sizeX: {w.get('sizeX')}, sizeY: {w.get('sizeY')}")
    print()

# Show one layout entry
state = list(cfg["states"].values())[0]
layout = list(state["layouts"].values())[0]
for wid, pos in list(layout["widgets"].items())[:3]:
    print(f"layout[{wid!r}] = {pos}")
