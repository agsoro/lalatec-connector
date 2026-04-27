#!/usr/bin/env bash
# Creates the BACnet AHU dashboard with correct widget FQNs for this TB version
set -e

TB_URL="http://localhost:8080"
ROOT_ASSET_ID="2ec93830-4026-11f1-af56-d3c145850c19"

TOKEN=$(curl -s -X POST "${TB_URL}/api/auth/login" \
  -H 'Content-Type: application/json' \
  -d '{"username":"tenant@thingsboard.org","password":"tenant"}' \
  | python3 -c "import sys,json; print(json.load(sys.stdin)['token'])")
echo "✓ JWT obtained"

# Delete existing dashboard if present
EXISTING=$(curl -s "${TB_URL}/api/tenant/dashboards?pageSize=50&page=0&textSearch=BACnet+AHU" \
  -H "X-Authorization: Bearer ${TOKEN}" \
  | python3 -c "
import sys, json
for d in json.load(sys.stdin).get('data', []):
    if d['title'] == 'BACnet AHU':
        print(d['id']['id']); break
")
if [ -n "$EXISTING" ]; then
  echo "Removing existing dashboard ${EXISTING}…"
  curl -s -X DELETE "${TB_URL}/api/dashboard/${EXISTING}" \
    -H "X-Authorization: Bearer ${TOKEN}" > /dev/null
fi

# Deterministic alias / widget IDs
ALIAS_ROOT="aa000001-0000-0000-0000-000000000001"
ALIAS_SEL="aa000001-0000-0000-0000-000000000002"
W_TREE="widget_tree"
W_CHART="widget_chart"
W_ATTRS="widget_attrs"

python3 - <<PYEOF
import json, sys

root_asset = "$ROOT_ASSET_ID"
alias_root = "$ALIAS_ROOT"
alias_sel  = "$ALIAS_SEL"
w_tree  = "$W_TREE"
w_chart = "$W_CHART"
w_attrs = "$W_ATTRS"

dashboard = {
  "title": "BACnet AHU",
  "configuration": {
    "description": "Live BACnet object hierarchy — click a node to see its telemetry",
    "entityAliases": {
      alias_root: {
        "id": alias_root,
        "alias": "BACnet Root",
        "filter": {
          "type": "singleEntity",
          "singleEntity": {"id": root_asset, "entityType": "ASSET"},
          "resolveMultiple": False
        }
      },
      alias_sel: {
        "id": alias_sel,
        "alias": "Selected Node",
        "filter": {
          "type": "stateEntity",
          "stateEntityParamName": "selectedNode",
          "defaultStateEntity": {"id": root_asset, "entityType": "ASSET"},
          "resolveMultiple": False
        }
      }
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
      "showDashboardsSelect": True,
      "showEntitiesSelect": True,
      "showFilters": True,
      "hideToolbar": False
    },
    "states": {
      "default": {
        "name": "BACnet AHU",
        "root": True,
        "layouts": {
          "main": {
            "widgets": {
              w_tree:  {"sizeX": 7, "sizeY": 12, "row": 0, "col": 0},
              w_chart: {"sizeX": 17, "sizeY": 6,  "row": 0, "col": 7},
              w_attrs: {"sizeX": 17, "sizeY": 6,  "row": 6, "col": 7}
            },
            "gridSettings": {
              "columns": 24,
              "margin": 10,
              "outerMargin": True,
              "backgroundColor": "#2b2b2b",
              "backgroundSizeMode": "100%",
              "autoFillHeight": True
            }
          }
        }
      }
    },
    "widgets": {
      w_tree: {
        "isSystemType": True,
        "bundleAlias": "cards",
        "typeAlias": "entities_hierarchy",
        "type": "latest",
        "title": "BACnet Object Tree",
        "sizeX": 7,
        "sizeY": 12,
        "config": {
          "datasources": [{
            "type": "entity",
            "name": "Root",
            "entityAliasId": alias_root,
            "filterId": None,
            "dataKeys": []
          }],
          "actions": {
            "nodeSelected": [{
              "id": "a1",
              "name": "nodeSelected",
              "icon": "chevron_right",
              "type": "updateDashboardState",
              "targetDashboardStateId": "default",
              "openRightLayout": False,
              "setEntityId": True,
              "stateEntityParamName": "selectedNode"
            }]
          },
          "settings": {
            "nodeRelationQueryFunction": (
              "var query = {\n"
              "  parameters: {\n"
              "    rootId: nodeCtx.entity.id,\n"
              "    rootType: nodeCtx.entity.entityType,\n"
              "    direction: 'FROM',\n"
              "    relationTypeGroup: 'COMMON',\n"
              "    maxLevel: 1,\n"
              "    fetchLastLevelOnly: false\n"
              "  },\n"
              "  filters: [{ relationType: 'Contains', entityTypes: ['ASSET', 'ENTITY_VIEW'] }]\n"
              "};\n"
              "return query;"
            ),
            "nodeHasChildrenFunction": "return nodeCtx.entity.entityType === 'ASSET';",
            "nodeIconFunction": "if (nodeCtx.entity.entityType === 'ENTITY_VIEW') { return { materialIcon: 'sensors' }; } return { materialIcon: 'folder_open' };",
            "nodeTitleFunction": "return nodeCtx.entity.name;",
            "nodeOpenedFunction": "return nodeCtx.level <= 2;",
            "nodeDisabledFunction": "return false;"
          },
          "title": "BACnet Object Tree",
          "showTitle": True,
          "backgroundColor": "#1e1e1e",
          "color": "rgba(255,255,255,0.87)",
          "padding": "8px",
          "showLegend": False,
          "widgetStyle": {},
          "titleStyle": {"fontSize": "16px", "fontWeight": 400}
        }
      },
      w_chart: {
        "isSystemType": True,
        "bundleAlias": "charts",
        "typeAlias": "basic_timeseries",
        "type": "timeseries",
        "title": "Live Value",
        "sizeX": 17,
        "sizeY": 6,
        "config": {
          "datasources": [{
            "type": "entity",
            "name": "Selected",
            "entityAliasId": alias_sel,
            "filterId": None,
            "dataKeys": [{
              "name": "value",
              "type": "timeseries",
              "label": "Present Value",
              "color": "#2196f3",
              "settings": {},
              "hidden": False
            }]
          }],
          "timewindow": {"realtime": {"realtimeType": 1, "timewindowMs": 300000}},
          "title": "Live Value",
          "showTitle": True,
          "backgroundColor": "#1e1e1e",
          "color": "rgba(255,255,255,0.87)",
          "padding": "8px",
          "settings": {
            "shadowSize": 4,
            "fontColor": "#aaaaaa",
            "fontSize": 10,
            "smoothLines": False,
            "stack": False,
            "xaxis": {"showLabels": True, "color": "#aaaaaa"},
            "yaxis": {"min": None, "max": None, "showLabels": True, "color": "#aaaaaa"},
            "grid": {"color": "#555555", "tickColor": "#444444", "verticalLines": True, "horizontalLines": True, "outlineWidth": 1}
          },
          "showLegend": True,
          "widgetStyle": {},
          "titleStyle": {"fontSize": "16px", "fontWeight": 400}
        }
      },
      w_attrs: {
        "isSystemType": True,
        "bundleAlias": "cards",
        "typeAlias": "attributes_card",
        "type": "latest",
        "title": "BACnet Point Info",
        "sizeX": 17,
        "sizeY": 6,
        "config": {
          "datasources": [{
            "type": "entity",
            "name": "Selected",
            "entityAliasId": alias_sel,
            "filterId": None,
            "dataKeys": [
              {"name": "bacnet_path",     "type": "attribute", "label": "BACnet Path",     "color": "#4caf50", "settings": {}},
              {"name": "bacnet_type",     "type": "attribute", "label": "Object Type",     "color": "#4caf50", "settings": {}},
              {"name": "description",     "type": "attribute", "label": "Description",     "color": "#4caf50", "settings": {}},
              {"name": "units",           "type": "attribute", "label": "Engineering Units","color": "#4caf50", "settings": {}},
              {"name": "bacnet_instance", "type": "attribute", "label": "Instance",        "color": "#4caf50", "settings": {}}
            ]
          }],
          "title": "BACnet Point Info",
          "showTitle": True,
          "backgroundColor": "#1e1e1e",
          "color": "rgba(255,255,255,0.87)",
          "padding": "8px",
          "settings": {"labelValueSeparator": ":", "showActionButtons": False},
          "showLegend": False,
          "widgetStyle": {},
          "titleStyle": {"fontSize": "16px", "fontWeight": 400}
        }
      }
    }
  }
}

print(json.dumps(dashboard))
PYEOF
