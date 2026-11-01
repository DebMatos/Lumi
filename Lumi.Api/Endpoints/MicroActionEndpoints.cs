
using Lumi.Api.Services;

namespace Lumi.Api.Endpoints;

public static class MicroActionEndpoints
{
    public static IEndpointRouteBuilder MapMicroActionEndpoints(
        this IEndpointRouteBuilder app)
    {
        app.MapPost("/microactions", async (
            CreateMicroActionRequest request,
            MicroActionService service,
            CancellationToken cancellationToken) =>
        {
            if (string.IsNullOrWhiteSpace(request.Description)
                || request.FollowUpMinutes < 1
                || request.FollowUpMinutes > 1440)
            {
                return Results.BadRequest(new
                {
                    error = "Descrição ou intervalo inválido."
                });
            }

            var action = await service.StartAsync(
                request.Description,
                request.FollowUpMinutes,
                cancellationToken);

            return Results.Created(
                $"/microactions/{action.Id}",
                new
                {
                    action.Id,
                    action.Description,
                    action.Status,
                    action.StartedAt,
                    FollowUp = action.FollowUps.Select(f => new
                    {
                        f.Id,
                        f.DueAt,
                        f.IntervalMinutes,
                        f.Status
                    }).Single()
                });
        });

        app.MapPost("/microactions/{id:guid}/complete", async (
            Guid id,
            MicroActionService service,
            CancellationToken cancellationToken) =>
        {
            var completed = await service.CompleteAsync(
                id, cancellationToken);

            return completed
                ? Results.NoContent()
                : Results.NotFound();
        });
        
        return app;
    }
}

public record CreateMicroActionRequest(
    string Description,
    int FollowUpMinutes);
