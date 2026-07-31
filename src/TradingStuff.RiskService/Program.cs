using System.Collections.Concurrent;
using TradingStuff.Contracts;
using TradingStuff.RiskService;
using TradingStuff.ServiceDefaults;

var builder = WebApplication.CreateBuilder(args);

builder.AddServiceDefaults();
builder.Services.AddSingleton(RiskLimitFactory.FromConfiguration(builder.Configuration));
builder.Services.AddSingleton<PortfolioRiskEvaluator>();
builder.Services.AddSingleton<DuplicateOrderGuard>();

var app = builder.Build();

app.UseAuthentication();
app.UseAuthorization();

app.MapPost("/risk/evaluate-order", (
        RiskEvaluationRequest request,
        PortfolioRiskEvaluator evaluator,
        DuplicateOrderGuard duplicateGuard) =>
    {
        if (duplicateGuard.IsDuplicate(request.Order))
        {
            var duplicateResult = new RiskEvaluationResult(
                Guid.NewGuid(),
                RiskDecision.Rejected,
                [new RiskLimitBreach("DUPLICATE_ORDER", "Client order id has already been evaluated.", 1m, 0m)],
                GreeksVector.Zero,
                0m,
                0m,
                DateTimeOffset.UtcNow);

            return Results.Ok(duplicateResult);
        }

        var result = evaluator.Evaluate(request);
        if (result.Decision == RiskDecision.Approved)
        {
            duplicateGuard.Remember(request.Order);
        }

        return Results.Ok(result);
    })
    .RequireAuthorization();

app.MapGet("/risk/limits", (RiskLimits limits) => Results.Ok(limits))
    .RequireAuthorization();

app.MapDefaultEndpoints();

app.Run();

public sealed class DuplicateOrderGuard
{
    private readonly ConcurrentDictionary<string, byte> _seenClientOrderIds = new(StringComparer.Ordinal);

    public bool IsDuplicate(SubmitOrderRequest order)
    {
        if (order.ClientOrderId is not { } clientOrderId)
        {
            return false;
        }

        return _seenClientOrderIds.ContainsKey(Key(order.AccountId, clientOrderId));
    }

    public void Remember(SubmitOrderRequest order)
    {
        if (order.ClientOrderId is not { } clientOrderId)
        {
            return;
        }

        _seenClientOrderIds.TryAdd(Key(order.AccountId, clientOrderId), 0);
    }

    private static string Key(string accountId, Guid clientOrderId) => $"{accountId}:{clientOrderId:N}";
}
