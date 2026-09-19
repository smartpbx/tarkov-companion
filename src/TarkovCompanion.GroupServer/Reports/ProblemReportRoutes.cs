using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace TarkovCompanion.GroupServer;

public static class ProblemReportRoutes
{
    /// <summary>
    /// The operator's half of the report lifecycle: see every report and its state, mark one processed or
    /// failed, delete one. Every route authorises before it looks at the report, let alone changes it.
    /// </summary>
    /// <remarks>
    /// Kept out of <c>GET /reports</c> on purpose. That route is what the hourly workflow lists, and it
    /// validates the answer against a closed schema; a state field added there would make it refuse the
    /// whole listing. States live here, for the operator.
    /// </remarks>
    /// <param name="authorise">Whether a request is the operator's; the operator key in production.</param>
    public static IEndpointRouteBuilder MapProblemReportAdmin(
        this IEndpointRouteBuilder app,
        ProblemReports reports,
        Func<HttpRequest, bool>? authorise = null)
    {
        ArgumentNullException.ThrowIfNull(app);
        ArgumentNullException.ThrowIfNull(reports);
        authorise ??= RelayAdmin.IsAuthorised;

        app.MapGet("/admin/reports", IResult (HttpRequest request) =>
            authorise(request) ? Results.Ok(reports.Ledger()) : Results.Unauthorized());

        app.MapPost("/admin/reports/{reference}/processed", IResult (string reference, HttpRequest request) =>
        {
            if (!authorise(request))
            {
                return Results.Unauthorized();
            }

            return Answer(reference, reports.MarkProcessed(reference), "processed");
        });

        app.MapPost("/admin/reports/{reference}/failed", IResult (string reference, HttpRequest request) =>
        {
            if (!authorise(request))
            {
                return Results.Unauthorized();
            }

            return Answer(reference, reports.MarkFailed(reference), "failed");
        });

        app.MapDelete("/admin/reports/{reference}", IResult (string reference, HttpRequest request) =>
        {
            if (!authorise(request))
            {
                return Results.Unauthorized();
            }

            // Deleting what is already gone is not an error: the caller wanted it gone.
            return Results.Ok(new { reference, state = "deleted", removed = reports.Delete(reference) });
        });
        return app;
    }

    private static IResult Answer(string reference, ReportTransition transition, string state) => transition switch
    {
        ReportTransition.Applied or ReportTransition.Unchanged =>
            Results.Ok(new { reference, state, changed = transition == ReportTransition.Applied }),
        ReportTransition.Refused => Results.Conflict(new { reference, error = "That report is already processed." }),
        _ => Results.NotFound(),
    };
}
