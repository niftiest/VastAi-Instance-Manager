using System.Text.Json;
using VastAiInstanceManager.Models;
using Xunit;

namespace VastAiInstanceManager.Tests;

public class InstanceChargesTests
{
    // Trimmed from a real GET /api/v0/charges/ response.
    private const string Page = """
    {
      "success": true,
      "results": [
        {
          "start": 1793145600, "type": "instance", "source": "instance-58056573", "amount": 0.56,
          "items": [
            { "type": "gpu",  "description": "0.044 hours at $0.633/hour",      "amount": 0.028 },
            { "type": "disk", "description": "0.105 hours at $0.012/hour",      "amount": 0.001 },
            { "type": "bwd",  "description": "13.5 GB Downloaded at $0.039/GB", "amount": 0.527 },
            { "type": "bwu",  "description": "0.1 GB Uploaded at $0.037/GB",    "amount": 0.004 }
          ]
        },
        { "start": 1793059200, "type": "instance", "source": "instance-58056573", "amount": 1.25, "items": [] },
        { "start": 1793145600, "type": "volume", "source": "volume-123", "amount": 9.99, "items": [] }
      ],
      "next_token": null
    }
    """;

    [Fact]
    public void MergePage_SumsDailyBucketsPerInstance()
    {
        var result = new Dictionary<long, InstanceCharges>();
        InstanceCharges.MergePage(JsonDocument.Parse(Page).RootElement, result);

        var c = Assert.Single(result).Value;
        Assert.Equal(58056573, c.InstanceId);
        Assert.Equal(1.81, c.Total, 3);
        Assert.Equal(4, c.Items.Count);
    }

    [Fact]
    public void MergePage_IgnoresNonInstanceSources()
    {
        var result = new Dictionary<long, InstanceCharges>();
        InstanceCharges.MergePage(JsonDocument.Parse(Page).RootElement, result);
        Assert.DoesNotContain(123, result.Keys);
    }

    [Fact]
    public void MergePage_MissingResults_IsNoOp()
    {
        var result = new Dictionary<long, InstanceCharges>();
        InstanceCharges.MergePage(JsonDocument.Parse("""{"success":false}""").RootElement, result);
        Assert.Empty(result);
    }

    [Fact]
    public void Breakdown_LabelsItemsAndTotal()
    {
        var result = new Dictionary<long, InstanceCharges>();
        InstanceCharges.MergePage(JsonDocument.Parse(Page).RootElement, result);
        var text = result[58056573].Breakdown;
        Assert.Contains("Download: 13.5 GB Downloaded at $0.039/GB = $0.527", text);
        Assert.EndsWith("Total billed: $1.810", text);
    }

    [Fact]
    public void Offer_BandwidthCost_UsesPerGbRate()
    {
        // $40/TB host, as seen on a real offer: inet_down_cost = 0.0390625 $/GB.
        var offer = new VastOffer { InetDownCostPerGb = 0.0390625 };
        Assert.Equal(40, offer.InetDownCostPerTb, 6);
        Assert.Equal(0.6015625, offer.EstimatePullCost(15.4), 6);
        Assert.Equal(0, offer.EstimatePullCost(0));
    }
}
