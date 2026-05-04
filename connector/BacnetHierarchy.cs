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
        /// Raw PROP_OBJECT_NAME string, which in Deziko is a technical dot-separated path
        /// (e.g. "G01'ASP01'RLT001'BSK'BSK210'FbClsd").
        /// </summary>
        public string ObjectName { get; init; } = "";

        /// <summary>
        /// Friendly hierarchy segments from proprietary property 4397
        /// (e.g. ["Gebäude", "Floor 1", "Room 101", "Brandschutzklappe 210"]).
        /// </summary>
        public List<string> NamingPath { get; init; } = new();

        /// <summary>Friendly alias from proprietary property 4438 (e.g. "BSK210").</summary>
        public string NameExtension { get; init; } = "";

        /// <summary>The friendly display name for this node.</summary>
        public string FriendlyName
        {
            get
            {
                if (NamingPath.Any()) return NamingPath.Last();
                if (!string.IsNullOrEmpty(NameExtension)) return NameExtension;
                return ShortName;
            }
        }

        /// <summary>Last segment of technical ObjectName (handles '.' or ''' separators).</summary>
        public string ShortName => string.IsNullOrWhiteSpace(ObjectName)
            ? ObjectId.ToString()
            : ObjectName.Split(new[] { '.', '\'' }, StringSplitOptions.RemoveEmptyEntries).Last();

        /// <summary>Human-readable description from PROP_DESCRIPTION (may be empty).</summary>
        public string Description { get; init; } = "";

        /// <summary>profile / point-type string from PROP_PROFILE_NAME (may be empty).</summary>
        public string ProfileName { get; init; } = "";

        /// <summary>Engineering unit string from PROP_UNITS (may be empty, for data-point leaves only).</summary>
        public string Units { get; init; } = "";

        /// <summary>True = this is an OBJECT_STRUCTURED_VIEW folder; False = it is a data-point leaf.</summary>
        public bool IsView { get; init; }

        /// <summary>Optional associated Trend Log object (Deziko prop 4452).</summary>
        public BacnetObjectId? LogObjectId { get; init; }

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
        const BacnetPropertyIds PropDezikoSubordinateList = (BacnetPropertyIds)4398; // Deziko-specific Structured List
        const BacnetPropertyIds PropProfileName          = (BacnetPropertyIds)168; // PROP_PROFILE_NAME
        const BacnetPropertyIds PropNamingPath           = (BacnetPropertyIds)4397; // Naming Path (Array of strings)
        const BacnetPropertyIds PropNameExtension        = (BacnetPropertyIds)4438; // Name Extension (String)
        const BacnetPropertyIds PropTrendLogReference    = (BacnetPropertyIds)4452; // Trend Log Reference (ObjectId)

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

            // Fallback for Deziko controllers that use 4398 for the root list
            if (topLevelIds.Count == 0)
            {
                topLevelIds = ReadObjectIdList(client, address, deviceObjId,
                                               PropDezikoSubordinateList);
            }

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

            var namingPath = ReadStringListProp(client, address, viewId, PropNamingPath);
            string nameExt = ReadStringProp(client, address, viewId, PropNameExtension) ?? "";

            var node = new DezikoNode
            {
                ObjectId      = viewId,
                ObjectName    = objectName,
                NamingPath    = namingPath,
                NameExtension = nameExt,
                Description   = description,
                ProfileName   = profileName,
                IsView        = true,
            };

            // Read PROP_SUBORDINATE_LIST
            var subordinates = ReadSubordinateList(client, address, viewId);
            string indent    = new string(' ', depth * 2 + 4);
            Console.WriteLine($"{indent}[{objectName}]  ({subordinates.Count} children)");

            foreach (var childId in subordinates)
            {
                try
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
                catch { /* skip broken child */ }
            }

            return node;
        }

        static DezikoNode? ReadLeafNode(
            BacnetClient client, BacnetAddress address,
            BacnetObjectId oid,
            HashSet<BacnetObjectId> visited)
        {
            if (!visited.Add(oid)) return null;

            try
            {
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
                
                ReadObjectIdProp(client, address, oid, PropTrendLogReference, out var logId);

                var namingPath = ReadStringListProp(client, address, oid, PropNamingPath);
                string nameExt = ReadStringProp(client, address, oid, PropNameExtension) ?? "";

                return new DezikoNode
                {
                    ObjectId      = oid,
                    ObjectName    = objectName,
                    NamingPath    = namingPath,
                    NameExtension = nameExt,
                    Description   = description,
                    ProfileName   = profileName,
                    Units         = units,
                    IsView        = false,
                    LogObjectId   = logId,
                };
            }
            catch
            {
                return null;
            }
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

        static bool ReadObjectIdProp(
            BacnetClient client, BacnetAddress address,
            BacnetObjectId oid, BacnetPropertyIds propId, out BacnetObjectId? result)
        {
            result = null;
            try
            {
                if (client.ReadPropertyRequest(address, oid, propId,
                        out IList<BacnetValue> vals) && vals.Count > 0)
                {
                    if (vals[0].Value is BacnetObjectId rid)
                    {
                        result = rid;
                        return true;
                    }
                }
            }
            catch { /* property not available */ }
            return false;
        }

        static List<string> ReadStringListProp(
            BacnetClient client, BacnetAddress address,
            BacnetObjectId oid, BacnetPropertyIds propId)
        {
            var result = new List<string>();
            try
            {
                if (client.ReadPropertyRequest(address, oid, propId,
                        out IList<BacnetValue> vals))
                {
                    foreach (var v in vals)
                    {
                        string? s = v.Value?.ToString();
                        if (!string.IsNullOrEmpty(s)) result.Add(s);
                    }
                }
            }
            catch { /* property not available or bulk read failed */ }
            return result;
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

            // 1. Read count first
            uint count = 0;
            try
            {
                if (!client.ReadPropertyRequest(address, targetObj,
                        propId, out IList<BacnetValue> countVal, arrayIndex: 0)
                    || countVal.Count == 0)
                {
                    return result;
                }
                count = Convert.ToUInt32(countVal[0].Value);
            }
            catch { return result; }

            // 2. Small lists: try bulk read
            if (count < 50)
            {
                try
                {
                    if (client.ReadPropertyRequest(address, targetObj, propId, out IList<BacnetValue> vals))
                    {
                        foreach (var v in vals)
                            if (v.Value is BacnetObjectId oid)
                                result.Add(oid);
                        
                        if (result.Count >= count) return result;
                        result.Clear();
                    }
                }
                catch { /* fallback */ }
            }

            // 3. Large lists: reliable index read
            for (uint i = 1; i <= count; i++)
            {
                try
                {
                    if (client.ReadPropertyRequest(address, targetObj,
                            propId, out IList<BacnetValue> entry, arrayIndex: i)
                        && entry.Count > 0
                        && entry[0].Value is BacnetObjectId eid)
                    {
                        result.Add(eid);
                    }
                }
                catch { /* skip missing index */ }
            }

            return result;
        }

        /// <summary>
        /// Reads PROP_SUBORDINATE_LIST from a Structured View object.
        /// Each entry is a BacnetDeviceObjectPropertyReference whose objectIdentifier
        /// is the child's BacnetObjectId.
        /// Falls back to index-by-index reading when bulk read fails.
        /// </summary>
        static List<BacnetObjectId> ReadSubordinateList(
            BacnetClient client, BacnetAddress address, BacnetObjectId viewId,
            BacnetPropertyIds propId = PropSubordinateList)
        {
            var result = new List<BacnetObjectId>();

            // 1. Read count first
            uint count = 0;
            try
            {
                if (!client.ReadPropertyRequest(address, viewId,
                        propId, out IList<BacnetValue> countVal, arrayIndex: 0)
                    || countVal.Count == 0)
                {
                    // If we are checking the standard property and it failed, try the Deziko property
                    if (propId == PropSubordinateList)
                        return ReadSubordinateList(client, address, viewId, PropDezikoSubordinateList);

                    return result;
                }
                count = Convert.ToUInt32(countVal[0].Value);
            }
            catch
            {
                if (propId == PropSubordinateList)
                    return ReadSubordinateList(client, address, viewId, PropDezikoSubordinateList);
                return result;
            }

            // 2. Small lists: try bulk
            if (count < 50)
            {
                try
                {
                    if (client.ReadPropertyRequest(address, viewId, propId, out IList<BacnetValue> vals))
                    {
                        foreach (var v in vals) ExtractObjectId(v, result);
                        if (result.Count >= count) return result;
                        result.Clear();
                    }
                }
                catch { /* fallback */ }
            }

            // 3. Reliable index-based read
            for (uint i = 1; i <= count; i++)
            {
                try
                {
                    if (client.ReadPropertyRequest(address, viewId,
                            propId, out IList<BacnetValue> entry, arrayIndex: i)
                        && entry.Count > 0)
                    {
                        ExtractObjectId(entry[0], result);
                    }
                }
                catch { /* skip missing index */ }
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
