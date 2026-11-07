using System.Security.Claims;
using CheapHours.Host.Api.Devices;
using CheapHours.Host.Api.Dtos;
using CheapHours.Host.Api.Errors;
using CheapHours.Host.Api.Goals;
using CheapHours.Persistence.Goals;
using CheapHours.Persistence.Plans;

namespace CheapHours.Host.Api.Savings;

/// <summary>Handler for <c>GET /v1/savings</c> (planning-api spec).</summary>
internal static class SavingsEndpoints
{
    /// <summary>
    /// Savings over the owner's goals departing in [<paramref name="from"/>, <paramref name="to"/>): each goal's
    /// latest confirmed plan version counts when its prices were complete. Costs are summed from the amounts
    /// shown to the cent, so the totals match the plans a client has seen.
    /// </summary>
    /// <param name="from">The start of the range (UTC).</param>
    /// <param name="to">The end of the range (UTC).</param>
    /// <param name="user">The authenticated key.</param>
    /// <param name="goals">The goal store.</param>
    /// <param name="plans">The plan store.</param>
    /// <param name="cancellationToken">Cancels the request.</param>
    /// <returns>200 with the totals, or 400.</returns>
    public static async Task<IResult> GetAsync(
        DateTimeOffset from,
        DateTimeOffset to,
        ClaimsPrincipal user,
        IGoalStore goals,
        IPlanStore plans,
        CancellationToken cancellationToken)
    {
        var rules = new QueryRules();
        rules.Range(from, to);
        if (!rules.IsValid)
        {
            return ErrorCatalogue.ValidationFailed(rules.Errors);
        }

        var owner = DeviceEndpoints.Owner(user);
        var departing = await goals.ListDepartingAsync(owner, from, to, cancellationToken).ConfigureAwait(false);
        var confirmed = await plans.LatestConfirmedAsync(owner, [.. departing.Select(g => g.Goal.Id)], cancellationToken).ConfigureAwait(false);
        var counted = confirmed.Where(p => p.Plan.PricesComplete).ToList();

        var planned = counted.Sum(p => PlanMapping.Cents(p.Plan.ExpectedCostEur));
        var arrival = counted.Sum(p => PlanMapping.Cents(p.Plan.ArrivalCostEur));
        return TypedResults.Ok(new SavingsDto(from, to, counted.Count, planned, arrival, arrival - planned));
    }
}
