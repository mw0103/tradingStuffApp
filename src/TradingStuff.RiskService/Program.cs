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
        Results.Ok(RiskEvaluationHandler.Evaluate(request, evaluator, duplicateGuard)))
    .RequireAuthorization();

app.MapGet("/risk/limits", (RiskLimits limits) => Results.Ok(limits))
    .RequireAuthorization();

// The breach codes are operator vocabulary: an audit surface reading a stored decision needs the
// whole set without grepping the evaluator for string literals.
app.MapGet("/risk/breach-codes", () => Results.Ok(RiskBreachCodes.All))
    .RequireAuthorization();

app.MapDefaultEndpoints();

app.Run();
