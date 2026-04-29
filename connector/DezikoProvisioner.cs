// DezikoProvisioner.cs – materialises a DezikoTree into ThingsBoard Assets + Relations
//
//  Model produced in ThingsBoard:
//
//    [Asset] Building
//      └─ Contains → [Asset] Floor2
//           └─ Contains → [Asset] Room201
//                └─ Contains → [Device] HVAC Controller AHU-01  (the single BACnet device)
//
//  Every Asset gets SERVER_SCOPE attributes:
//    bacnet_path        – full dot-separated PROP_OBJECT_NAME path
//    bacnet_type        – short object type alias (ai, av, …) or "view"
//    bacnet_instance    – numeric instance (for data-point leaves)
//    description        – PROP_DESCRIPTION
//    profile_name       – PROP_PROFILE_NAME (specific)
//    units              – PROP_UNITS (data-point leaves only)
//
//  The run is fully idempotent: each asset and relation is created only if missing.

using System;
using System.Collections.Generic;
using System.IO.BACnet;
using System.Threading;
using System.Threading.Tasks;

namespace Connector
{
    class DezikoProvisioner
    {
        /// <summary>
        /// Walks the <paramref name="tree"/> depth-first and materialises each node as a
        /// ThingsBoard Asset, with "Contains" relations linking parents to children and
        /// each leaf folder to the single BACnet <paramref name="tbDeviceId"/>.
        /// Returns a dictionary mapping BACnet key-prefix (e.g. "ai_1") → ThingsBoard Asset UUID
        /// for every leaf data-point asset, so the caller can post telemetry directly to assets.
        /// </summary>
        public async Task<Dictionary<string, string>> ProvisionAsync(
            DezikoTree                 tree,
            List<BacnetObjectInfo>     cachedObjects,
            ThingsBoardApi             api,
            string                     tbDeviceId,
            string                     assetType,
            CancellationToken          ct)
        {
            var leafMap = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            var visited = new HashSet<BacnetObjectId>();

            if (tree.Roots.Count == 0 && cachedObjects.Count == 0)
            {
                Console.WriteLine("  [Hierarchy] Nothing to provision.");
                return leafMap;
            }

            var counters = new Counters();

            foreach (var root in tree.Roots)
            {
                ct.ThrowIfCancellationRequested();
                await ProvisionNodeAsync(root, parentAssetId: null, parentName: null, api,
                                        tbDeviceId, assetType, counters, leafMap, visited, ct);
            }

            // Provision orphans: objects with a naming path that were NOT reached by the hierarchy walk.
            // These often occur in controllers like Deziko CC where the structured views are 
            // difficult to traverse fully.
            Console.WriteLine($"  [Hierarchy] Tree walk reached {visited.Count} unique objects. Checking {cachedObjects.Count} cached objects for orphans...");
            foreach (var obj in cachedObjects)
            {
                if (visited.Contains(obj.ObjectId)) continue;
                
                if (obj.NamingPath.Count < 2) 
                {
                    // Console.WriteLine($"  [Hierarchy]   Skipping potential orphan {obj.ObjectId}: NamingPath.Count={obj.NamingPath.Count}");
                    continue;
                }

                ct.ThrowIfCancellationRequested();

                // We try to reconstruct a folder structure from the path.
                // For orphans, we just create the leaf asset named "Parent / Child".
                string parentName = obj.NamingPath[^2];
                string childName  = obj.NamingPath[^1];
                string assetName  = $"{parentName} / {childName}";

                string assetId = await api.EnsureAssetAsync(assetName, assetType);
                counters.Assets++;

                // Ensure relation to the device
                await api.EnsureRelationAsync(tbDeviceId, "DEVICE", assetId, "ASSET");
                counters.Relations++;

                // Create Entity View
                string viewName = $"{obj.ObjectName} ({assetName})";
                var telKeys = obj.LogObjectId != null ? new[] { "value" } : Array.Empty<string>();
                await api.EnsureEntityViewAsync(viewName, assetType, assetId, "ASSET", telKeys, new string[] { });
                counters.EntityViews++;

                leafMap[obj.KeyPrefix] = assetId;
                visited.Add(obj.ObjectId);

                Console.WriteLine($"  [Hierarchy]   Orphan Asset: '{assetName}' (key={obj.KeyPrefix})");
            }

            Console.WriteLine(
                $"  [Hierarchy] Provisioning complete — {counters.Assets} assets, {counters.Relations} relations, {counters.EntityViews} entity views.");

            return leafMap;
        }

        sealed class Counters { public int Assets; public int Relations; public int EntityViews; }

        // ── Recursive node handler ────────────────────────────────────────────

        async Task ProvisionNodeAsync(
            DezikoNode     node,
            string?        parentAssetId,
            string?        parentName,
            ThingsBoardApi api,
            string         tbDeviceId,
            string         assetType,
            Counters       c,
            Dictionary<string, string> leafMap,
            HashSet<BacnetObjectId> visited,
            CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();

            // The TB asset name is the friendly name.
            string assetName = node.FriendlyName;
            if (string.IsNullOrEmpty(assetName)) assetName = node.ShortName;

            string assetId = await api.EnsureAssetAsync(assetName, assetType);
            c.Assets++;
            visited.Add(node.ObjectId);
            Console.WriteLine($"  [Hierarchy]   Asset: '{assetName}' (key={node.ObjectName})");

            // Set rich attributes on the asset
            var attrs = new Dictionary<string, string>
            {
                ["bacnet_path"]     = node.NamingPath.Any() ? string.Join(" / ", node.NamingPath) : node.ObjectName,
                ["bacnet_key"]      = node.ObjectName,
                ["bacnet_id"]       = node.ObjectId.ToString(),
                ["bacnet_type"]     = node.IsView
                                        ? "view"
                                        : ShortType(node.ObjectId.type),
                ["description"]     = node.Description,
                ["profile_name"]    = node.ProfileName,
            };

            if (!node.IsView)
            {
                attrs["bacnet_instance"] = node.ObjectId.instance.ToString();
                if (!string.IsNullOrEmpty(node.Units))
                    attrs["units"] = node.Units;
            }

            await api.SetAssetAttributesAsync(assetId, attrs);

            // Link parent asset → this asset
            if (parentAssetId is not null)
            {
                await api.EnsureRelationAsync(parentAssetId, "ASSET", assetId, "ASSET");
                c.Relations++;
            }

            // Recurse into child views; for data-point leaves create a relation to the device
            foreach (var child in node.Children)
            {
                ct.ThrowIfCancellationRequested();

                if (child.IsView)
                {
                    await ProvisionNodeAsync(child, assetId, assetName, api,
                                            tbDeviceId, assetType, c, leafMap, visited, ct);
                }
                else
                {
                    // Leaf data-point: qualify the name with the parent view name to avoid
                    // collisions between identically-named points under different views.
                    string leafName = string.IsNullOrEmpty(assetName)
                        ? child.FriendlyName
                        : $"{assetName} / {child.FriendlyName}";

                    string leafId = await api.EnsureAssetAsync(leafName, assetType);
                    c.Assets++;
                    visited.Add(child.ObjectId);
                    Console.WriteLine($"  [Hierarchy]   Leaf:  '{leafName}' ({child.ObjectId} / key={child.ObjectName})");

                    var leafAttrs = new Dictionary<string, string>
                    {
                        ["bacnet_path"]     = child.NamingPath.Any() ? string.Join(" / ", child.NamingPath) : child.ObjectName,
                        ["bacnet_key"]      = child.ObjectName,
                        ["bacnet_id"]       = child.ObjectId.ToString(),
                        ["bacnet_type"]     = ShortType(child.ObjectId.type),
                        ["bacnet_instance"] = child.ObjectId.instance.ToString(),
                        ["description"]     = child.Description,
                        ["profile_name"]    = child.ProfileName,
                    };
                    if (!string.IsNullOrEmpty(child.Units))
                        leafAttrs["units"] = child.Units;

                    await api.SetAssetAttributesAsync(leafId, leafAttrs);

                    string keyPrefix = $"{ShortType(child.ObjectId.type)}_{child.ObjectId.instance}";
                    leafMap[keyPrefix] = leafId;

                    // Asset folder → leaf asset
                    await api.EnsureRelationAsync(assetId, "ASSET", leafId, "ASSET");
                    c.Relations++;

                    // Leaf asset → BACnet device (so data flows through in TB dashboards)
                    await api.EnsureRelationAsync(leafId, "ASSET", tbDeviceId, "DEVICE");
                    c.Relations++;

                    // The view references the leaf Asset and exposes the live
                    // 'value' timeseries plus human-readable server attributes.
                    var evAttrs = new[] { "bacnet_path", "bacnet_key", "bacnet_id", "bacnet_type", "bacnet_instance",
                                         "description", "profile_name", "units" };
                    string evId = await api.EnsureEntityViewAsync(
                        viewName:          leafName,
                        viewType:          assetType,
                        sourceEntityId:    leafId,
                        sourceEntityType:  "ASSET",
                        telemetryKeys:     child.LogObjectId != null ? new[] { "value" } : Array.Empty<string>(),
                        serverAttributes:  evAttrs);
                    c.EntityViews++;

                    // Mirror the asset parent→child relation in Entity View space
                    await api.EnsureRelationAsync(assetId, "ASSET", evId, "ENTITY_VIEW");
                    c.Relations++;
                }
            }
        }

        // ── Helpers ──────────────────────────────────────────────────────────

        static string ShortType(BacnetObjectTypes t) => t switch
        {
            BacnetObjectTypes.OBJECT_ANALOG_INPUT        => "ai",
            BacnetObjectTypes.OBJECT_ANALOG_OUTPUT       => "ao",
            BacnetObjectTypes.OBJECT_ANALOG_VALUE        => "av",
            BacnetObjectTypes.OBJECT_BINARY_INPUT        => "bi",
            BacnetObjectTypes.OBJECT_BINARY_OUTPUT       => "bo",
            BacnetObjectTypes.OBJECT_BINARY_VALUE        => "bv",
            BacnetObjectTypes.OBJECT_MULTI_STATE_INPUT   => "mi",
            BacnetObjectTypes.OBJECT_MULTI_STATE_OUTPUT  => "mo",
            BacnetObjectTypes.OBJECT_MULTI_STATE_VALUE   => "mv",
            BacnetObjectTypes.OBJECT_STRUCTURED_VIEW     => "view",
            _                                            => t.ToString().Replace("OBJECT_", "").ToLowerInvariant(),
        };
    }
}
