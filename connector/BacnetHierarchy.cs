// BacnetHierarchy.cs – Deziko Structured View walker
//
//  BACnet Structured View objects (type 29) carry a PROP_SUBORDINATE_LIST (property 355)
//  that references child objects.  Children can be other views (sub-folders) or real
//  data-point objects (AIs, AVs, etc.).  The DEVICE object's PROP_STRUCTURED_OBJECT_LIST
//  (property 209) lists the top-level views.
//
//  This module reads the whole tree in one discovery pass and returns a DezikoTree that
//  the DezikoProvisioner then materialises as ThingsBoard Assets + Relations.

using System;
using System.Collections.Generic;
using System.IO.BACnet;
using System.Linq;

namespace Connector
{
    // =========================================================================
    //  Domain model
    // =========================================================================

    /// <summary>
    /// A single node in the Deziko object tree.
    /// View nodes (IsView=true) act as folders; leaf nodes are BACnet data-points.
    /// </summary>
    class DezikoNode
    {
        /// <summary>BACnet object identifier (e.g. OBJECT_STRUCTURED_VIEW:4).</summary>
        public BacnetObjectId ObjectId { get; init; }

        /// <summary>
        /// Raw PROP_OBJECT_NAME string, which in Deziko is a full dot-separated path
        /// (e.g. "Building.Floor2.Room201.TempSP").
        /// </summary>
        public string ObjectName { get; init; } = "";

        /// <summary>Last dot-segment of ObjectName – used as the TB Asset display name.</summary>
        public string ShortName  => string.IsNullOrWhiteSpace(ObjectName)
            ? ObjectId.ToString()
            : ObjectName.Contains('.')
                ? ObjectName[(ObjectName.LastIndexOf('.') + 1)..]
                : ObjectName;

        /// <summary>Human-readable description from PROP_DESCRIPTION (may be empty).</summary>
        public string Description { get; init; } = "";

        /// <summary>profile / point-type string from PROP_PROFILE_NAME (may be empty).</summary>
        public string ProfileName { get; init; } = "";

        /// <summary>Engineering unit string from PROP_UNITS (may be empty, for data-point leaves only).</summary>
        public string Units { get; init; } = "";

        /// <summary>True = this is an OBJECT_STRUCTURED_VIEW folder; False = it is a data-point leaf.</summary>
        public bool IsView { get; init; }

        /// <summary>Populated only for view nodes (IsView=true).</summary>
        public List<DezikoNode> Children { get; } = new();
    }

    /// <summary>Extracted hierarchy for one BACnet device.</summary>
    class DezikoTree
    {
        /// <summary>Top-level Structured View roots (may be empty if the device has none).</summary>
        public List<DezikoNode> Roots { get; } = new();
    }

    // =========================================================================
    //  Walker
    // =========================================================================

    static class BacnetHierarchy
    {
        // BACnet property IDs used below
        const BacnetPropertyIds PropStructuredObjectList = (BacnetPropertyIds)209; // PROP_STRUCTURED_OBJECT_LIST
        const BacnetPropertyIds PropSubordinateList      = (BacnetPropertyIds)355; // PROP_SUBORDINATE_LIST
        const BacnetPropertyIds PropProfileName          = (BacnetPropertyIds)168; // PROP_PROFILE_NAME

        // ── Public entry point ────────────────────────────────────────────────

        /// <summary>
        /// Walks the OBJECT_STRUCTURED_VIEW tree of <paramref name="deviceId"/> and
        /// returns the full hierarchy.  Always returns a non-null DezikoTree; Roots is
        /// empty when the device has no Structured View objects.
        /// </summary>
        public static DezikoTree Walk(
            BacnetClient client, BacnetAddress address, uint deviceId)
        {
            var tree    = new DezikoTree();
            var visited = new HashSet<BacnetObjectId>();

            // Read PROP_STRUCTURED_OBJECT_LIST from the Device object
            var deviceObjId = new BacnetObjectId(BacnetObjectTypes.OBJECT_DEVICE, deviceId);
            var topLevelIds = ReadObjectIdList(client, address, deviceObjId,
                                               PropStructuredObjectList);

            Console.WriteLine($"  [Hierarchy] Top-level Structured Views: {topLevelIds.Count}");

            foreach (var svId in topLevelIds)
            {
                if (svId.type != BacnetObjectTypes.OBJECT_STRUCTURED_VIEW) continue;

                var node = WalkView(client, address, svId, visited, depth: 0);
                if (node is not null)
                    tree.Roots.Add(node);
            }

            return tree;
        }

        // ── Recursive view walker ─────────────────────────────────────────────

        static DezikoNode? WalkView(
            BacnetClient client, BacnetAddress address,
            BacnetObjectId viewId,
            HashSet<BacnetObjectId> visited,
            int depth)
        {
            // Guard against cycles
            if (!visited.Add(viewId)) return null;

            string objectName = ReadStringProp(client, address, viewId,
                                               BacnetPropertyIds.PROP_OBJECT_NAME)
                                ?? viewId.ToString();

            string description = ReadStringProp(client, address, viewId,
                                                BacnetPropertyIds.PROP_DESCRIPTION)
                                 ?? "";

            string profileName = ReadStringProp(client, address, viewId,
                                                PropProfileName)
                                 ?? "";

            var node = new DezikoNode
            {
                ObjectId    = viewId,
                ObjectName  = objectName,
                Description = description,
                ProfileName = profileName,
                IsView      = true,
            };

            // Read PROP_SUBORDINATE_LIST
            var subordinates = ReadSubordinateList(client, address, viewId);
            string indent    = new string(' ', depth * 2 + 4);
            Console.WriteLine($"{indent}[{objectName}]  ({subordinates.Count} children)");

            foreach (var childId in subordinates)
            {
                if (childId.type == BacnetObjectTypes.OBJECT_STRUCTURED_VIEW)
                {
                    // Sub-folder: recurse
                    var child = WalkView(client, address, childId, visited, depth + 1);
                    if (child is not null)
                        node.Children.Add(child);
                }
                else if (childId.type != BacnetObjectTypes.OBJECT_DEVICE)
                {
                    // Data-point leaf
                    var leaf = ReadLeafNode(client, address, childId, visited);
                    if (leaf is not null)
                        node.Children.Add(leaf);
                }
            }

            return node;
        }

        static DezikoNode? ReadLeafNode(
            BacnetClient client, BacnetAddress address,
            BacnetObjectId oid,
            HashSet<BacnetObjectId> visited)
        {
            if (!visited.Add(oid)) return null;

            string objectName = ReadStringProp(client, address, oid,
                                               BacnetPropertyIds.PROP_OBJECT_NAME)
                                ?? oid.ToString();

            string description = ReadStringProp(client, address, oid,
                                                BacnetPropertyIds.PROP_DESCRIPTION)
                                 ?? "";

            string profileName = ReadStringProp(client, address, oid,
                                                PropProfileName)
                                 ?? "";

            // PROP_UNITS is an enum value – read as raw and convert to string
            string units = ReadUnitsProp(client, address, oid);

            return new DezikoNode
            {
                ObjectId    = oid,
                ObjectName  = objectName,
                Description = description,
                ProfileName = profileName,
                Units       = units,
                IsView      = false,
            };
        }

        // ── Property read helpers ─────────────────────────────────────────────

        /// <summary>Reads a BACnet property that returns a string value. Returns null on failure.</summary>
        static string? ReadStringProp(
            BacnetClient client, BacnetAddress address,
            BacnetObjectId oid, BacnetPropertyIds propId)
        {
            try
            {
                if (client.ReadPropertyRequest(address, oid, propId,
                        out IList<BacnetValue> vals) && vals.Count > 0)
                    return vals[0].Value?.ToString();
            }
            catch { /* property not available */ }
            return null;
        }

        /// <summary>
        /// Reads PROP_UNITS and converts the BACnet engineering unit enum to a readable string.
        /// Returns "" when unavailable.
        /// </summary>
        static string ReadUnitsProp(
            BacnetClient client, BacnetAddress address, BacnetObjectId oid)
        {
            try
            {
                if (client.ReadPropertyRequest(address, oid,
                        BacnetPropertyIds.PROP_UNITS, out IList<BacnetValue> vals)
                    && vals.Count > 0)
                {
                    // BacnetUnitsId enum if available, otherwise raw number
                    var raw = vals[0].Value;
                    if (raw is BacnetUnitsId uid)
                        return uid.ToString().Replace("UNITS_", "").Replace("_", " ").ToLowerInvariant();
                    if (raw is uint u)
                        return ((BacnetUnitsId)u).ToString().Replace("UNITS_", "").Replace("_", " ").ToLowerInvariant();
                    return raw?.ToString() ?? "";
                }
            }
            catch { /* property not available on this object type */ }
            return "";
        }

        /// <summary>
        /// Reads an array property that contains BacnetObjectId entries
        /// (PROP_STRUCTURED_OBJECT_LIST on the Device object).
        /// </summary>
        static List<BacnetObjectId> ReadObjectIdList(
            BacnetClient client, BacnetAddress address,
            BacnetObjectId targetObj, BacnetPropertyIds propId)
        {
            var result = new List<BacnetObjectId>();
            try
            {
                if (client.ReadPropertyRequest(address, targetObj, propId,
                        out IList<BacnetValue> vals))
                {
                    foreach (var v in vals)
                        if (v.Value is BacnetObjectId oid)
                            result.Add(oid);
                }
            }
            catch { /* property may not be supported */ }
            return result;
        }

        /// <summary>
        /// Reads PROP_SUBORDINATE_LIST from a Structured View object.
        /// Each entry is a BacnetDeviceObjectPropertyReference whose objectIdentifier
        /// is the child's BacnetObjectId.
        /// Falls back to index-by-index reading when bulk read fails.
        /// </summary>
        static List<BacnetObjectId> ReadSubordinateList(
            BacnetClient client, BacnetAddress address, BacnetObjectId viewId)
        {
            var result = new List<BacnetObjectId>();

            try
            {
                // Attempt bulk read first
                if (client.ReadPropertyRequest(address, viewId,
                        PropSubordinateList, out IList<BacnetValue> vals))
                {
                    foreach (var v in vals)
                        ExtractObjectId(v, result);
                    return result;
                }

                // Fallback: read count (index 0) then each entry
                if (!client.ReadPropertyRequest(address, viewId,
                        PropSubordinateList, out IList<BacnetValue> countVal, arrayIndex: 0)
                    || countVal.Count == 0)
                    return result;

                uint count = Convert.ToUInt32(countVal[0].Value);
                for (uint i = 1; i <= count; i++)
                {
                    if (client.ReadPropertyRequest(address, viewId,
                            PropSubordinateList, out IList<BacnetValue> entry, arrayIndex: i)
                        && entry.Count > 0)
                        ExtractObjectId(entry[0], result);
                }
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"  [Hierarchy] WARN: Could not read SubordinateList of {viewId}: {ex.Message}");
            }

            return result;
        }

        static void ExtractObjectId(BacnetValue v, List<BacnetObjectId> list)
        {
            // PROP_SUBORDINATE_LIST values are BacnetDeviceObjectPropertyReference
            // The objectIdentifier field holds the child's ObjectId
            if (v.Value is BacnetDeviceObjectPropertyReference dpRef)
            {
                list.Add(dpRef.objectIdentifier);
                return;
            }

            // Some libraries return BacnetObjectId directly for same-device references
            if (v.Value is BacnetObjectId oid)
            {
                list.Add(oid);
            }
        }
    }
}
