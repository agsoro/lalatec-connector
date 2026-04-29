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
            DezikoTree tree,
            List<BacnetObjectInfo> cachedObjects,
            ThingsBoardApi api,
            string tbDeviceId,
            string assetType,
            CancellationToken ct)
        {
            var leafMap = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            var visitedPaths = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            var visitedObjects = new HashSet<BacnetObjectId>();

            // Build a lookup for technical telemetry keys based on object ID
            var keyPrefixMap = cachedObjects.ToDictionary(o => o.ObjectId, o => o.KeyPrefix);

            if (tree.Roots.Count == 0 && cachedObjects.Count == 0)
            {
                Console.WriteLine("  [Hierarchy] Nothing to provision.");
                return leafMap;
            }

            var counters = new Counters();

            // 1. Process the explicit Structured View tree
            foreach (var root in tree.Roots)
            {
                ct.ThrowIfCancellationRequested();
                await ProvisionNodeRecursiveAsync(root, null, api, tbDeviceId, assetType, counters, leafMap, visitedPaths, visitedObjects, keyPrefixMap, ct);
            }

            // 2. Process all cached objects to catch "orphans" (points with NamingPath but not in Structured View)
            Console.WriteLine($"  [Hierarchy] Tree walk reached {visitedObjects.Count} unique objects. Processing remaining cached objects...");
            foreach (var obj in cachedObjects)
            {
                if (visitedObjects.Contains(obj.ObjectId)) continue;
                if (obj.NamingPath.Count == 0) continue;

                ct.ThrowIfCancellationRequested();

                // For orphans, we ensure the parent folder path exists
                var parentPath = obj.NamingPath.Take(obj.NamingPath.Count - 1).ToList();

                // Classify the orphan point
                string effectiveType = Classify(obj.ObjectName);
                if (effectiveType == "default") effectiveType = assetType;

                string parentAssetId = await EnsurePathAsync(parentPath, api, tbDeviceId, effectiveType, counters, visitedPaths, ct);

                // Create the Entity View for the sensor directly under the parent asset
                string leafName = obj.NamingPath.Last();
                if (obj.NamingPath.Count > 1)
                {
                    string parentName = obj.NamingPath[^2];
                    if (!leafName.StartsWith(parentName, StringComparison.OrdinalIgnoreCase))
                        leafName = $"{parentName} / {leafName}";
                }

                // Technical telemetry keys (close to BACnet reality)
                var telKeys = new[] { $"{obj.KeyPrefix}_value", $"{obj.KeyPrefix}_status", $"{obj.KeyPrefix}_alarm", $"{obj.KeyPrefix}_fault" };

                string evId = await api.EnsureEntityViewAsync(
                    viewName: leafName,
                    viewType: effectiveType,
                    sourceEntityId: parentAssetId,
                    sourceEntityType: "ASSET",
                    telemetryKeys: telKeys,
                    serverAttributes: new[] { "bacnet_path", "bacnet_id", "description", "units" },
                    description: obj.ObjectName);
                counters.EntityViews++;

                // Set description explicitly to the technical key
                await api.SetEntityViewAttributesAsync(evId, new Dictionary<string, string>
                {
                    ["bacnet_key"] = obj.ObjectName
                });

                // Parent Asset --[Contains]--> Entity View
                await api.EnsureRelationAsync(parentAssetId, "ASSET", evId, "ENTITY_VIEW");
                counters.Relations++;

                leafMap[obj.KeyPrefix] = evId;
                visitedObjects.Add(obj.ObjectId);

                Console.WriteLine($"  [Hierarchy]   Orphan Sensor: '{leafName}' (key={obj.KeyPrefix})");
            }

            Console.WriteLine(
                $"  [Hierarchy] Provisioning complete — {counters.Assets} assets, {counters.Relations} relations, {counters.EntityViews} entity views.");

            return leafMap;
        }

        sealed class Counters { public int Assets; public int Relations; public int EntityViews; }

        // ── Recursive Path/Node Handler ───────────────────────────────────────

        async Task ProvisionNodeRecursiveAsync(
            DezikoNode node,
            string? parentAssetId,
            ThingsBoardApi api,
            string tbDeviceId,
            string assetType,
            Counters c,
            Dictionary<string, string> leafMap,
            Dictionary<string, string> visitedPaths,
            HashSet<BacnetObjectId> visitedObjects,
            Dictionary<BacnetObjectId, string> keyPrefixMap,
            CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            visitedObjects.Add(node.ObjectId);

            if (node.IsView)
            {
                // Ensure this folder's path exists as an Asset
                var path = node.NamingPath.Any() ? node.NamingPath : new List<string> { node.ShortName };

                // Dynamic classification for folders
                string effectiveType = Classify(node.ObjectName);
                if (effectiveType == "default") effectiveType = assetType;

                string assetId = await EnsurePathAsync(path, api, tbDeviceId, effectiveType, c, visitedPaths, ct, node.ObjectName);

                // Set attributes for the folder
                var attrs = new Dictionary<string, string>
                {
                    ["bacnet_path"] = string.Join(" / ", path),
                    ["bacnet_key"] = node.ObjectName,
                    ["bacnet_id"] = node.ObjectId.ToString(),
                    ["bacnet_type"] = "view",
                    ["description"] = node.ObjectName, // Key in description as requested
                    ["profile_name"] = node.ProfileName,
                };
                await api.SetAssetAttributesAsync(assetId, attrs);

                // Recurse into children
                foreach (var child in node.Children)
                {
                    await ProvisionNodeRecursiveAsync(child, assetId, api, tbDeviceId, assetType, c, leafMap, visitedPaths, visitedObjects, keyPrefixMap, ct);
                }
            }
            else
            {
                // It's a leaf data-point sensor: create an Entity View directly under the parent asset
                if (parentAssetId == null) return; // Should not happen for valid hierarchies

                string leafName = node.FriendlyName;
                if (node.NamingPath.Count > 1)
                {
                    string parentName = node.NamingPath[^2];
                    if (!leafName.StartsWith(parentName, StringComparison.OrdinalIgnoreCase))
                        leafName = $"{parentName} / {leafName}";
                }

                if (!keyPrefixMap.TryGetValue(node.ObjectId, out string? keyPrefix))
                    keyPrefix = $"{ShortType(node.ObjectId.type)}_{node.ObjectId.instance}";

                // Dynamic classification for sensors
                string effectiveViewType = Classify(node.ObjectName);
                if (effectiveViewType == "default") effectiveViewType = assetType;

                // Technical keys exposed by the view
                var telKeys = new[] { $"{keyPrefix}_value", $"{keyPrefix}_status", $"{keyPrefix}_alarm", $"{keyPrefix}_fault" };

                string evId = await api.EnsureEntityViewAsync(
                    viewName: leafName,
                    viewType: effectiveViewType,
                    sourceEntityId: parentAssetId,
                    sourceEntityType: "ASSET",
                    telemetryKeys: telKeys,
                    serverAttributes: new[] { "bacnet_path", "bacnet_id", "description", "units" },
                    description: node.ObjectName);
                c.EntityViews++;

                // Set description explicitly to the technical key
                await api.SetEntityViewAttributesAsync(evId, new Dictionary<string, string>
                {
                    ["description"] = node.ObjectName,
                    ["bacnet_key"] = node.ObjectName
                });

                // Folder Asset --[Contains]--> Entity View
                await api.EnsureRelationAsync(parentAssetId, "ASSET", evId, "ENTITY_VIEW");
                c.Relations++;

                leafMap[keyPrefix] = parentAssetId;
                Console.WriteLine($"  [Hierarchy]   Sensor: '{leafName}' (Target Asset: {parentAssetId})");
            }
        }

        async Task<string> EnsurePathAsync(
            List<string> path,
            ThingsBoardApi api,
            string tbDeviceId,
            string fallbackType,
            Counters c,
            Dictionary<string, string> visitedPaths,
            CancellationToken ct,
            string? leafDescription = null)
        {
            string? currentParentId = null;
            string currentPath = "";

            for (int i = 0; i < path.Count; i++)
            {
                string segment = path[i];
                currentPath = i == 0 ? segment : $"{currentPath} / {segment}";

                if (visitedPaths.TryGetValue(currentPath, out string? existingId))
                {
                    currentParentId = existingId;
                    continue;
                }

                // Try to derive a specific type for this path segment
                string effectiveType = Classify(segment);
                if (effectiveType == "default") effectiveType = fallbackType;

                // For the last segment in the path, use the provided description
                string? segmentDesc = (i == path.Count - 1) ? leafDescription : null;

                ct.ThrowIfCancellationRequested();
                string assetId = await api.EnsureAssetAsync(segment, effectiveType, segmentDesc);
                c.Assets++;
                visitedPaths[currentPath] = assetId;

                // Link to parent asset or device root
                if (currentParentId != null)
                {
                    await api.EnsureRelationAsync(currentParentId, "ASSET", assetId, "ASSET");
                    c.Relations++;
                }
                else
                {
                    // Root segments link to the device
                    await api.EnsureRelationAsync(tbDeviceId, "DEVICE", assetId, "ASSET");
                    c.Relations++;
                }

                currentParentId = assetId;
            }

            return currentParentId!;
        }

        // ── Helpers ──────────────────────────────────────────────────────────

        /// <summary>
        /// Extracts the 3-character technical abbreviation (e.g. BSK, RLT)
        /// from a technical Siemens ObjectName to determine its type.
        /// </summary>
        static string Classify(string technicalName)
        {
            if (string.IsNullOrEmpty(technicalName)) return "default";

            // Split into segments (G01'ASP01'RLT001'Status -> [G01, ASP01, RLT001, Status])
            var segments = technicalName.Split(new[] { '.', '\'' }, StringSplitOptions.RemoveEmptyEntries);

            // Iterate backwards to find the last segment that looks like a technical tag
            // (starts with at least 3 uppercase chars, e.g. BSK210 -> BSK)
            for (int i = segments.Length - 1; i >= 0; i--)
            {
                string s = segments[i];
                if (s.Length >= 3 && char.IsUpper(s[0]) && char.IsUpper(s[1]) && char.IsUpper(s[2]))
                {
                    return s.Substring(0, 3);
                }
            }

            return "default";
        }

        static string ShortType(BacnetObjectTypes t) => t switch
        {
            BacnetObjectTypes.OBJECT_ANALOG_INPUT => "ai",
            BacnetObjectTypes.OBJECT_ANALOG_OUTPUT => "ao",
            BacnetObjectTypes.OBJECT_ANALOG_VALUE => "av",
            BacnetObjectTypes.OBJECT_BINARY_INPUT => "bi",
            BacnetObjectTypes.OBJECT_BINARY_OUTPUT => "bo",
            BacnetObjectTypes.OBJECT_BINARY_VALUE => "bv",
            BacnetObjectTypes.OBJECT_MULTI_STATE_INPUT => "mi",
            BacnetObjectTypes.OBJECT_MULTI_STATE_OUTPUT => "mo",
            BacnetObjectTypes.OBJECT_MULTI_STATE_VALUE => "mv",
            BacnetObjectTypes.OBJECT_STRUCTURED_VIEW => "view",
            _ => t.ToString().Replace("OBJECT_", "").ToLowerInvariant(),
        };
    }
}
