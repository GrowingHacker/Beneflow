using System.Net.Http.Json;
using System;
using System.Threading.Tasks;

var client = new HttpClient();
client.DefaultRequestHeaders.UserAgent.ParseAdd("Beneflow/1.0 (contact@beneflow.local)");
var url = "https://world.openfoodfacts.org/api/v0/product/3017620422003.json";
Console.WriteLine("Hitting OFF...");
var json = await client.GetStringAsync(url);
Console.WriteLine("RAW JSON LEN=" + json.Length);
Console.WriteLine("  snippet: " + json.Substring(0, Math.Min(600, json.Length)));
