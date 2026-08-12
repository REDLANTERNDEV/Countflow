using Countflow.SharedKernel.Contracts;
using Countflow.SharedKernel.Results;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace Countflow.Modules.Rules.Features.Traces;

/// <summary>
/// One stored decision, in full.
/// <para>
/// Open to every authenticated role, deliberately. ADR 0004's first argument is
/// that a supervisor who cannot see why the system sent someone to a bin starts
/// overriding it — and the operator standing at the bin has exactly the same
/// problem. Hiding the reasoning behind a supervisor role would ship the feature
/// and lose the benefit.
/// </para>
/// </summary>
public static class GetDecisionTrace
{
    public static void Map(IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGet("/rules/traces/{id:guid}", async (
                Guid id,
                IDecisionTraceReader traces,
                CancellationToken cancellationToken) =>
            {
                var trace = await traces.FindAsync(id, cancellationToken);

                return trace is null
                    ? Result.Failure<DecisionTraceView>(RulesErrors.TraceNotFound).ToHttpResult()
                    : Results.Ok(trace);
            })
            .RequireAuthorization()
            .WithTags("Rules")
            .WithName("GetDecisionTrace")
            .WithSummary("Why the rules chose what they chose — every candidate, score and exclusion.")
            .Produces<DecisionTraceView>()
            .ProducesProblem(StatusCodes.Status404NotFound);
    }
}
