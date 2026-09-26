using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Spms.Persistence;
using Spms.SharedKernel;

namespace Spms.Web;

/// <summary>
/// One scoped transaction per request.
///
/// The filter sets the execution scope from the request context, opens the
/// transaction (whose first statements are SET LOCAL ROLE and
/// core.begin_scope), runs the endpoint, and then — BEFORE the result is
/// written — commits if the result is a success and rolls back otherwise.
/// Committing after the body had been sent would mean a client could read 200
/// for a write the commit then lost.
///
/// Any 4xx/5xx answer rolls back everything the request did. Domain services
/// open their own IUnitOfWork inside this, which becomes a savepoint, so their
/// early-return rollbacks still work at the finer grain.
/// </summary>
public sealed class RequestTransactionFilter : IEndpointFilter
{
    public async ValueTask<object?> InvokeAsync(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
    {
        var http = context.HttpContext;
        if (http.GetEndpoint()?.Metadata.GetMetadata<NoRequestTransaction>() is not null)
            return await next(context);

        var ctx = RequestContext.From(http);
        var scope = http.RequestServices.GetRequiredService<ExecutionScope>();
        if (ctx.Authenticated)
        {
            scope.Set(ctx.TenantId, ctx.PropertyIds, ctx.PropertyId, ctx.PrincipalId, ctx.ActorType, ctx.CorrelationId);
        }
        else
        {
            scope.SetAnonymous(ctx.CorrelationId);
        }

        var db = http.RequestServices.GetRequiredService<SpmsDbContext>();
        await using var tx = await db.Database.BeginTransactionAsync(http.RequestAborted);

        var result = await next(context);

        if (IsSuccess(result))
        {
            await db.SaveChangesAsync(http.RequestAborted);
            await tx.CommitAsync(http.RequestAborted);
        }
        else
        {
            await tx.RollbackAsync(CancellationToken.None);
            db.ChangeTracker.Clear();
        }
        return result;
    }

    private static bool IsSuccess(object? result) => result switch
    {
        IStatusCodeHttpResult s => (s.StatusCode ?? 200) < 400,
        _ => true,
    };
}

/// <summary>Endpoint metadata: this endpoint touches no tenant data (health probes).</summary>
public sealed class NoRequestTransaction;

public static class RequestTransactionExtensions
{
    public static TBuilder WithoutRequestTransaction<TBuilder>(this TBuilder b) where TBuilder : IEndpointConventionBuilder
    {
        b.Add(e => e.Metadata.Add(new NoRequestTransaction()));
        return b;
    }
}
