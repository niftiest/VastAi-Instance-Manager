using VastAiInstanceManager.Models;
using VastAiInstanceManager.Services;
using Xunit;

namespace VastAiInstanceManager.Tests;

public class OfferRankingTests
{
    private static ModelEntry Model(double modelSizeGb = 19) => new()
    {
        Key = "test",
        Name = "Test",
        OllamaTag = "test:latest",
        VramGb = 24,
        DiskGb = 35,
        ModelSizeGb = modelSizeGb,
    };

    /// <summary>Build an offer with enough VRAM to fit so EstimateTokensPerSec uses the
    /// "fits" branch (gives realistic t/s). gpuMemBwGbs is the primary driver.</summary>
    private static VastOffer Offer(long id, int numGpus, double price, int vramGb, double bwGbs)
        => new()
        {
            Id = id,
            NumGpus = numGpus,
            GpuName = $"GPU-{id}",
            GpuTotalRamMb = vramGb * 1024,
            GpuRamMb = (vramGb / Math.Max(1, numGpus)) * 1024,
            DphTotal = price,
            GpuMemBwGbs = bwGbs,
        };

    [Fact]
    public void Empty_input_returns_nulls()
    {
        var picks = OfferRanking.Compute(Array.Empty<VastOffer>(), Model());
        Assert.Null(picks.BestPrice);
        Assert.Null(picks.Fastest);
        Assert.Null(picks.BestValue);
    }

    [Fact]
    public void Single_offer_wins_everything()
    {
        var o = Offer(1, 1, 0.50, 24, 900);
        var picks = OfferRanking.Compute(new[] { o }, Model());
        Assert.Same(o, picks.BestPrice);
        Assert.Same(o, picks.Fastest);
        Assert.Same(o, picks.BestValue);
    }

    [Fact]
    public void BestPrice_is_cheapest()
    {
        var a = Offer(1, 1, 0.10, 24, 400);
        var b = Offer(2, 1, 0.50, 24, 1200);
        var picks = OfferRanking.Compute(new[] { a, b }, Model());
        Assert.Same(a, picks.BestPrice);
    }

    [Fact]
    public void Fastest_is_highest_tps()
    {
        var slow = Offer(1, 1, 0.10, 24, 400);
        var fast = Offer(2, 1, 0.50, 24, 2000);
        var picks = OfferRanking.Compute(new[] { slow, fast }, Model());
        Assert.Same(fast, picks.Fastest);
    }

    [Fact]
    public void BestValue_uses_squared_throughput()
    {
        // Mirrors the real scenario from the screenshot: a dirt-cheap slow GPU vs a pricier fast one.
        // Plain t/s ÷ $/hr would still favour the cheap option. t/s² ÷ $/hr must favour the fast one.
        var cheapSlow = Offer(1, 1, 0.245, 24, 250);   // ~12 t/s @ $0.245
        var pricyFast = Offer(2, 1, 2.417, 24, 1200);  // ~57 t/s @ $2.417
        var picks = OfferRanking.Compute(new[] { cheapSlow, pricyFast }, Model());
        Assert.Same(pricyFast, picks.BestValue);
        // Sanity check: BestPrice is still the cheap one - they're distinct picks now.
        Assert.Same(cheapSlow, picks.BestPrice);
        Assert.NotSame(picks.BestPrice, picks.BestValue);
    }

    [Fact]
    public void Zero_price_or_zero_tps_excluded_from_value_calc()
    {
        // An offer with price=0 (free-tier weirdness) shouldn't divide-by-zero or get value=infinity.
        var free = Offer(1, 1, 0.0, 24, 1500);
        var paid = Offer(2, 1, 1.0, 24, 800);
        var picks = OfferRanking.Compute(new[] { free, paid }, Model());
        Assert.Same(paid, picks.BestValue);   // free was filtered, paid wins by default
    }
}
