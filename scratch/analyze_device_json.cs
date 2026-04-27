using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Linq;

var jsonPath = @"c:\src\thingsboard\lalatec-connector\testing\bacnet-sim\device.json";
if (!File.Exists(jsonPath)) {
    Console.WriteLine("File not found");
    return;
}

using var stream = File.OpenRead(jsonPath);
using var jsonDoc = JsonDocument.Parse(stream);
var root = jsonDoc.RootElement;

var types = new HashSet<string>();
var propIds = new HashSet<string>();
var tags = new HashSet<string>();
var propIdToTag = new Dictionary<string, HashSet<string>>();

foreach (var obj in root.GetProperty("objects").EnumerateArray()) {
    types.Add(obj.GetProperty("type").GetString());
    foreach (var prop in obj.GetProperty("properties").EnumerateArray()) {
        var id = prop.GetProperty("id").GetString();
        var tag = prop.GetProperty("tag").GetString();
        propIds.Add(id);
        tags.Add(tag);
        if (!propIdToTag.ContainsKey(id)) propIdToTag[id] = new HashSet<string>();
        propIdToTag[id].Add(tag);
    }
}

Console.WriteLine("--- Object Types ---");
foreach (var t in types.OrderBy(x => x)) Console.WriteLine(t);

Console.WriteLine("\n--- Property IDs (Top 50) ---");
foreach (var p in propIds.OrderBy(x => x).Take(50)) Console.WriteLine(p);
if (propIds.Count > 50) Console.WriteLine("...");

Console.WriteLine("\n--- Application Tags ---");
foreach (var t in tags.OrderBy(x => x)) Console.WriteLine(t);

Console.WriteLine("\n--- Numeric Property IDs Found ---");
foreach (var p in propIds.Where(p => int.TryParse(p, out _)).OrderBy(x => int.Parse(x))) {
    Console.WriteLine($"{p}: {string.Join(", ", propIdToTag[p])}");
}
