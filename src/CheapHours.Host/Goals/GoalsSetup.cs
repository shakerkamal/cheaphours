using CheapHours.Host.Prices;
using CheapHours.Planning;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;

namespace CheapHours.Host.Goals;

/// <summary>Registers goal planning, which the API and the worker share.</summary>
internal static class GoalsSetup
{
    /// <summary>Registers <see cref="PlannerOptions"/> and the price settings (validated at start) and <see cref="GoalPlanningService"/>.</summary>
    /// <param name="services">The service collection.</param>
    /// <returns>The same service collection.</returns>
    public static IServiceCollection AddGoalPlanning(this IServiceCollection services)
    {
        services.AddOptions<PlannerOptions>().BindConfiguration(PlannerOptions.SectionName).ValidateOnStart();
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IValidateOptions<PlannerOptions>, PlannerOptionsHostValidator>());
        services.AddPriceOptions();
        services.TryAddSingleton<GoalPlanningService>();
        return services;
    }
}
