#!/usr/bin/env python3
"""
Creates the BACnet AHU dashboard using TB 4.x widget format:
  - typeFullFqn: "system.<bundle>.<alias>"
  - widget id: random UUID (instance id, not type id)
  - layout keyed by the same random UUID
"""
import json, uuid, urllib.request, urllib.error

TB_URL        = "http://localhost:8080"
EMAIL         = "tenant@thingsboard.org"
PASSWORD      = "tenant"
ROOT_ASSET_ID = "2ec93830-4026-11f1-af56-d3c145850c19"

def req(method, path, body=None, token=None):
    data    = json.dumps(body).encode() if body else None
    headers = {"Content-Type": "application/json"}
    if token: headers["X-Authorization"] = f"Bearer {token}"
    r = urllib.request.Request(TB_URL + path, data=data, headers=headers, method=method)
    with urllib.request.urlopen(r) as resp:
        return json.loads(resp.read())

def delete(path, token):
    r = urllib.request.Request(TB_URL + path,
                               headers={"X-Authorization": f"Bearer {token}"},
                               method="DELETE")
    try:
        with urllib.request.urlopen(r): pass
    except urllib.error.HTTPError: pass

token = req("POST", "/api/auth/login", {"username": EMAIL, "password": PASSWORD})["token"]
print("✓ JWT")

# Remove existing BACnet AHU dashboards
existing = req("GET", "/api/tenant/dashboards?pageSize=50&page=0&textSearch=BACnet+AHU", token=token)
for d in existing.get("data", []):
    if d["title"] == "BACnet AHU":
        delete(f"/api/dashboard/{d['id']['id']}", token)
        print(f"  Deleted {d['id']['id']}")

# ── Stable alias IDs ──────────────────────────────────────────────────────────
A_ROOT = "aa000001-0000-0000-0000-000000000001"
A_SEL  = "aa000001-0000-0000-0000-000000000002"

# ── Widget instance IDs (random UUIDs, used as both widget key AND layout key) ─
W1 = str(uuid.uuid4())  # hierarchy tree
W2 = str(uuid.uuid4())  # timeseries chart
W3 = str(uuid.uuid4())  # attributes card

TREE_QUERY = (
    "var query = {"
    "  parameters: { rootId: nodeCtx.entity.id, rootType: nodeCtx.entity.entityType,"
    "    direction: 'FROM', relationTypeGroup: 'COMMON', maxLevel: 1, fetchLastLevelOnly: false },"
    "  filters: [{ relationType: 'Contains', entityTypes: ['ASSET', 'ENTITY_VIEW'] }]"
    "}; return query;"
)

def attr_key(name, label):
    return {"name": name, "type": "attribute", "label": label,
            "color": "#4caf50", "settings": {}}

def ts_key(name, label, color="#2196f3"):
    return {"name": name, "type": "timeseries", "label": label,
            "color": color, "settings": {}, "hidden": False}

def ds(alias_id, keys=None):
    return {"type": "entity", "entityAliasId": alias_id,
            "filterId": None, "dataKeys": keys or []}

# ── Widget definitions (TB 4.x format) ────────────────────────────────────────
def make_widget(wid, fqn, wtype, title, sizeX, sizeY, config):
    return {
        "id":          wid,
        "typeFullFqn": fqn,
        "type":        wtype,
        "title":       title,
        "sizeX":       sizeX,
        "sizeY":       sizeY,
        "config":      config
    }

w_tree = make_widget(W1, "system.cards.entities_hierarchy", "latest",
                     "BACnet Object Tree", 8, 12, {
                         "datasources": [ds(A_ROOT)],
                         "actions": {
                             "nodeSelected": [{
                                 "id": "a1", "name": "nodeSelected",
                                 "icon": "chevron_right",
                                 "type": "updateDashboardState",
                                 "targetDashboardStateId": "default",
                                 "openRightLayout": False,
                                 "setEntityId": True,
                                 "stateEntityParamName": "selectedNode"
                             }]
                         },
                         "settings": {
                             "nodeRelationQueryFunction": TREE_QUERY,
                             "nodeHasChildrenFunction":
                                 "return nodeCtx.entity.entityType === 'ASSET';",
                             "nodeIconFunction": (
                                 "return nodeCtx.entity.entityType === 'ENTITY_VIEW'"
                                 " ? { materialIcon: 'sensors' }"
                                 " : { materialIcon: 'folder_open' };"
                             ),
                             "nodeTitleFunction":    "return nodeCtx.entity.name;",
                             "nodeOpenedFunction":   "return nodeCtx.level <= 2;",
                             "nodeDisabledFunction": "return false;"
                         },
                         "title": "BACnet Object Tree", "showTitle": True,
                         "backgroundColor": "#1e1e1e",
                         "color": "rgba(255,255,255,0.87)",
                         "padding": "8px", "showLegend": False,
                         "widgetStyle": {}, "titleStyle": {"fontSize": "16px", "fontWeight": 400}
                     })

w_chart = make_widget(W2, "system.charts.basic_timeseries", "timeseries",
                      "Live Value", 16, 6, {
                          "datasources": [ds(A_SEL, [ts_key("value", "Present Value")])],
                          "timewindow": {"realtime": {"realtimeType": 1, "timewindowMs": 300000}},
                          "title": "Live Value", "showTitle": True,
                          "backgroundColor": "#1e1e1e",
                          "color": "rgba(255,255,255,0.87)",
                          "padding": "8px",
                          "settings": {"dataZoom": True, "smooth": False, "stack": False,
                                       "showLegend": True, "legendPosition": "bottom"},
                          "showLegend": True, "widgetStyle": {},
                          "titleStyle": {"fontSize": "16px", "fontWeight": 400}
                      })

w_attrs = make_widget(W3, "system.cards.attributes_card", "latest",
                      "Point Info", 16, 6, {
                          "datasources": [ds(A_SEL, [
                              attr_key("bacnet_path",     "BACnet Path"),
                              attr_key("bacnet_type",     "Object Type"),
                              attr_key("description",     "Description"),
                              attr_key("units",           "Engineering Units"),
                              attr_key("bacnet_instance", "Instance #"),
                          ])],
                          "title": "Point Info", "showTitle": True,
                          "backgroundColor": "#1e1e1e",
                          "color": "rgba(255,255,255,0.87)",
                          "padding": "8px",
                          "settings": {"labelValueSeparator": ":", "showActionButtons": False},
                          "showLegend": False, "widgetStyle": {},
                          "titleStyle": {"fontSize": "16px", "fontWeight": 400}
                      })

dashboard = {
    "title": "BACnet AHU",
    "configuration": {
        "description": "BACnet object tree — click a node to see its telemetry",
        "entityAliases": {
            A_ROOT: {"id": A_ROOT, "alias": "BACnet Root", "filter": {
                "type": "singleEntity",
                "singleEntity": {"id": ROOT_ASSET_ID, "entityType": "ASSET"},
                "resolveMultiple": False}},
            A_SEL:  {"id": A_SEL,  "alias": "Selected Node", "filter": {
                "type": "stateEntity",
                "stateEntityParamName": "selectedNode",
                "defaultStateEntity": {"id": ROOT_ASSET_ID, "entityType": "ASSET"},
                "resolveMultiple": False}}
        },
        "filters": {},
        "timewindow": {
            "selectedTab": 0,
            "realtime": {"realtimeType": 1, "timewindowMs": 300000},
            "history": {"historyType": 0, "timewindowMs": 300000},
            "aggregation": {"type": "NONE", "limit": 200}
        },
        "settings": {
            "stateControllerId": "entity",
            "showTitle": False,
            "showDashboardsSelect": True, "showEntitiesSelect": True,
            "showFilters": True, "hideToolbar": False
        },
        "states": {
            "default": {
                "name": "BACnet AHU",
                "root": True,
                "layouts": {
                    "main": {
                        "widgets": {
                            W1: {"sizeX": 8,  "sizeY": 12, "row": 0, "col": 0},
                            W2: {"sizeX": 16, "sizeY": 6,  "row": 0, "col": 8},
                            W3: {"sizeX": 16, "sizeY": 6,  "row": 6, "col": 8},
                        },
                        "gridSettings": {
                            "columns": 24, "margin": 10, "outerMargin": True,
                            "backgroundColor": "#2b2b2b",
                            "backgroundSizeMode": "100%",
                            "autoFillHeight": True
                        }
                    }
                }
            }
        },
        "widgets": {
            W1: w_tree,
            W2: w_chart,
            W3: w_attrs,
        }
    }
}

result = req("POST", "/api/dashboard", dashboard, token)
did = result["id"]["id"]
print(f"\n✓ Dashboard created → {did}")
print(f"  {TB_URL}/dashboards/{did}")
