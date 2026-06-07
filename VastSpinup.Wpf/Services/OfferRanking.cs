using VastSpinup.Models;

namespace VastSpinup.Services;

/// <summary>
/// Pure functions that pick the three "best of" offers for the picker links. Lifted out of
/// MainViewModel so the ranking math (and the throughput-weighted value formula) can be tested
/// in isolation without spinning up a WPF dispatcher.
/// </summary>
public static class OfferRanking
{
    public record Picks(VastOffer? BestPrice, VastOffer? Fastest, VastOffer? BestValue);

    public static Picks Compute(IReadOnlyList<VastOffer> fitting, ModelEntry model)
    {
        if (fitting == null || fitting.Count == 0) return new(null, null, null);

        var cheapest = fitting.OrderBy(o => o.DphTotal).First();
        var fastest = fitting.OrderByDescending(o => o.EstimateTokensPerSec(model.ModelSizeGb)).First();
        // Throughput-weighted value: t/s² / $/hr. Plain t/s ÷ $/hr was too easily won by
        // dirt-cheap slow GPUs (price has a wider spread than throughput), making BEST VALUE
        // collapse onto BEST PRICE. Squaring t/s rewards real throughput while still penalising cost.
        var bestVal = fitting
            .Where(o => o.DphTotal > 0 && o.EstimateTokensPerSec(model.ModelSizeGb) > 0)
            .OrderByDescending(o =>
            {
                var tps = o.EstimateTokensPerSec(model.ModelSizeGb);
                return (double)tps * tps / o.DphTotal;
            })
            .FirstOrDefault() ?? cheapest;

        return new(cheapest, fastest, bestVal);
    }
}
