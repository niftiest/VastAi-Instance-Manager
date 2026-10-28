using System.Text.Json;

namespace VastAiInstanceManager.Models;

/// <summary>Billed totals for one instance, summed across the daily buckets returned by GET /charges/.</summary>
public class InstanceCharges
{
    public record Item(string Type, string Description, double Amount);

    public long InstanceId { get; init; }
    public double Total { get; set; }
    public List<Item> Items { get; } = new();

    /// <summary>Multi-line breakdown for the Spent tooltip, one line per charge item.</summary>
    public string Breakdown
    {
        get
        {
            var lines = Items.Select(i => $"{TypeLabel(i.Type)}: {i.Description} = ${i.Amount:F3}");
            return string.Join("\n", lines.Append($"Total billed: ${Total:F3}"));
        }
    }

    private static string TypeLabel(string type) => type switch
    {
        "gpu" => "GPU",
        "disk" => "Disk",
        "bwd" => "Download",
        "bwu" => "Upload",
        _ => type,
    };

    /// <summary>Parses a /charges/ response page into per-instance totals, merging into <paramref name="into"/>.
    /// Results are keyed by source "instance-&lt;id&gt;"; non-instance rows are ignored.</summary>
    public static void MergePage(JsonElement doc, Dictionary<long, InstanceCharges> into)
    {
        if (!doc.TryGetProperty("results", out var results) || results.ValueKind != JsonValueKind.Array) return;
        foreach (var r in results.EnumerateArray())
        {
            var source = r.TryGetProperty("source", out var s) && s.ValueKind == JsonValueKind.String ? s.GetString() : null;
            if (source == null || !source.StartsWith("instance-")) continue;
            if (!long.TryParse(source.AsSpan("instance-".Length), out var id)) continue;

            if (!into.TryGetValue(id, out var c)) into[id] = c = new InstanceCharges { InstanceId = id };
            c.Total += GetAmount(r);
            if (r.TryGetProperty("items", out var items) && items.ValueKind == JsonValueKind.Array)
            {
                foreach (var it in items.EnumerateArray())
                {
                    var type = it.TryGetProperty("type", out var t) ? t.GetString() ?? "" : "";
                    var desc = it.TryGetProperty("description", out var d) ? d.GetString() ?? "" : "";
                    c.Items.Add(new Item(type, desc, GetAmount(it)));
                }
            }
        }
    }

    private static double GetAmount(JsonElement e)
        => e.TryGetProperty("amount", out var a) && a.ValueKind == JsonValueKind.Number ? a.GetDouble() : 0;
}
