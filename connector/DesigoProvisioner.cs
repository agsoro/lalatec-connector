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
            DezikoTree     tree,
            ThingsBoardApi api,
            string         tbDeviceId,
            string         assetType,
            CancellationToken ct = default)
        {
            var leafMap = new Dictionary<string, string>();   // keyPrefix → assetId

            if (tree.Roots.Count == 0)
            {
                Console.WriteLine("  [Hierarchy] No Structured View roots – nothing to provision.");
                return leafMap;
            }

            var counters = new Counters();

            foreach (var root in tree.Roots)
            {
                ct.ThrowIfCancellationRequested();
                await ProvisionNodeAsync(root, parentAssetId: null, parentName: null, api,
                                        tbDeviceId, assetType, counters, leafMap, ct);
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
            CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();

            // The TB asset name is the last dot-segment; it must be unique within a
            // given level of the tree.  If two sibling views share the same short name
            // (rare in Deziko but possible), we append the instance number.
            string assetName = node.ShortName;

            string assetId = await api.EnsureAssetAsync(assetName, assetType);
            c.Assets++;
            Console.WriteLine($"  [Hierarchy]   Asset: '{assetName}' (id={assetId})");

            // Set rich attributes on the asset
            var attrs = new Dictionary<string, string>
            {
                ["bacnet_path"]     = node.ObjectName,
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
                                            tbDeviceId, assetType, c, leafMap, ct);
                }
                else
                {
                    // Leaf data-point: qualify the name with the parent view name to avoid
                    // collisions between identically-named points under different views.
                    string leafName = string.IsNullOrEmpty(assetName)
                        ? child.ShortName
                        : $"{assetName} / {child.ShortName}";

                    string leafId = await api.EnsureAssetAsync(leafName, assetType);
                    c.Assets++;
                    Console.WriteLine($"  [Hierarchy]   Leaf:  '{leafName}' ({ShortType(child.ObjectId.type)}:{child.ObjectId.instance})");

                    var leafAttrs = new Dictionary<string, string>
                    {
                        ["bacnet_path"]     = child.ObjectName,
                        ["bacnet_type"]     = ShortType(child.ObjectId.type),
                        ["bacnet_instance"] = child.ObjectId.instance.ToString(),
                        ["description"]     = child.Description,
                        ["profile_name"]    = child.ProfileName,
                    };
                    if (!string.IsNullOrEmpty(child.Units))
                        leafAttrs["units"] = child.Units;

                    await api.SetAssetAttributesAsync(leafId, leafAttrs);

                    // Build key-prefix identical to what BacnetReader.cs uses:
                    // ShortType + "_" + instance  (e.g. "ai_1", "ao_1", "bi_1")
                    string keyPrefix = $"{ShortType(child.ObjectId.type)}_{child.ObjectId.instance}";
                    leafMap[keyPrefix] = leafId;

                    // Asset folder → leaf asset
                    await api.EnsureRelationAsync(assetId, "ASSET", leafId, "ASSET");
                    c.Relations++;

                    // Leaf asset → BACnet device (so data flows through in TB dashboards)
                    await api.EnsureRelationAsync(leafId, "ASSET", tbDeviceId, "DEVICE");
                    c.Relations++;

                    // ── Entity View for this data-point ───────────────────────
                    // The view references the leaf Asset and exposes the live
                    // 'value' timeseries plus human-readable server attributes.
                    var evAttrs = new[] { "bacnet_path", "bacnet_type", "bacnet_instance",
                                         "description", "profile_name", "units" };
                    string evId = await api.EnsureEntityViewAsync(
                        viewName:          leafName,
                        viewType:          assetType,
                        sourceEntityId:    leafId,
                        sourceEntityType:  "ASSET",
                        telemetryKeys:     new[] { "value" },
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
