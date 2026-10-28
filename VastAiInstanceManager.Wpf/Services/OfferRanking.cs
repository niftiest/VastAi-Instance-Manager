using VastAiInstanceManager.Models;

namespace VastAiInstanceManager.Services;

/// <summary>
/// Pure functions that pick the three "best of" offers for the picker links. Lifted out of
/// MainViewModel so the ranking math (and the throughput-weighted value formula) can be tested
/// in isolation without spinning up a WPF dispatcher.
/// </summary>
public static class OfferRanking
{
    public record Picks(VastOffer? BestPrice, VastOffer? Fastest, VastOffer? BestValue);

    /// <summary>Session length the one-time model download is spread over when comparing prices.
    /// Bandwidth varies ~$0-$40+/TB across hosts, so a 15 GB pull can cost as much as an hour of GPU.</summary>
    public const double AmortizeHours = 1.0;

    /// <summary>Effective $/hr for the first <see cref="AmortizeHours"/>: hourly rate plus the model pull cost spread over it.</summary>
    public static double EffectiveHourlyCost(VastOffer o, ModelEntry model)
        => o.DphTotal + o.EstimatePullCost(model.ModelSizeGb) / AmortizeHours;

    public static Picks Compute(IReadOnlyList<VastOffer> fitting, ModelEntry model)
    {
        if (fitting == null || fitting.Count == 0) return new(null, null, null);

        var cheapest = fitting.OrderBy(o => EffectiveHourlyCost(o, model)).First();
        var fastest = fitting.OrderByDescending(o => o.EstimateTokensPerSec(model.ModelSizeGb)).First();
        // Throughput-weighted value: t/s² / effective $/hr. Plain t/s ÷ $/hr was too easily won by
        // dirt-cheap slow GPUs (price has a wider spread than throughput), making BEST VALUE
        // collapse onto BEST PRICE. Squaring t/s rewards real throughput while still penalising cost.
        var bestVal = fitting
            .Where(o => o.DphTotal > 0 && o.EstimateTokensPerSec(model.ModelSizeGb) > 0)
            .OrderByDescending(o =>
            {
                var tps = o.EstimateTokensPerSec(model.ModelSizeGb);
                return (double)tps * tps / EffectiveHourlyCost(o, model);
            })
            .FirstOrDefault() ?? cheapest;

        return new(cheapest, fastest, bestVal);
    }
}
