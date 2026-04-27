using System;
using System.IO.BACnet;

public class Program {
    public static void Main() {
        foreach (var val in Enum.GetValues(typeof(BacnetPropertyIds))) {
            if ((int)val == 355 || (int)val == 209) {
                Console.WriteLine($"{val} = {(int)val}");
            }
        }
        try {
            var p = Enum.Parse<BacnetPropertyIds>("PROP_SUBORDINATE_LIST");
            Console.WriteLine($"Parsed PROP_SUBORDINATE_LIST = {(int)p}");
        } catch {
            Console.WriteLine("Could not parse PROP_SUBORDINATE_LIST");
        }
    }
}
