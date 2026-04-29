using System;
using System.IO;
using System.Text;

class Program {
    static void Main() {
        string path = @"c:\src\thingsboard\lalatec-connector\testing\bacnet-sim\device.json";
        if (!File.Exists(path)) return;

        string text = File.ReadAllText(path);
        
        // Final aggressive cleanup
        text = text.Replace("\uFFFDä", "ä");
        text = text.Replace("\uFFFDü", "ü");
        text = text.Replace("\uFFFDö", "ö");
        text = text.Replace("\uFFFDß", "ß");
        text = text.Replace("\uFFFDÄ", "Ä");
        text = text.Replace("\uFFFDÖ", "Ö");
        text = text.Replace("\uFFFDÜ", "Ü");

        File.WriteAllText(path, text, new UTF8Encoding(false));
        Console.WriteLine("Aggressive string repair complete.");
    }
}
