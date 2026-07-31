using System.Net;
using TradingStuff.ServiceDefaults;

var builder = WebApplication.CreateBuilder(args);

builder.AddServiceDefaults();

var app = builder.Build();

app.UseAuthentication();
app.UseAuthorization();

app.MapGet("/", (IConfiguration configuration) =>
{
    var executionUrl = configuration["Dashboard:ExecutionBaseUrl"] ?? "http://executionservice";
    var riskUrl = configuration["Dashboard:RiskBaseUrl"] ?? "http://riskservice";
    var marketDataUrl = configuration["Dashboard:MarketDataBaseUrl"] ?? "http://marketdataservice";
    var rabbitUrl = configuration["Dashboard:RabbitManagementUrl"] ?? "http://rabbitmq:15672";
    var keycloakUrl = configuration["Dashboard:KeycloakUrl"] ?? "http://keycloak:8080";

    var html = $$"""
        <!doctype html>
        <html lang="en">
        <head>
            <meta charset="utf-8">
            <meta name="viewport" content="width=device-width, initial-scale=1">
            <title>Trading Audit</title>
            <style>
                :root {
                    color-scheme: light dark;
                    font-family: Inter, ui-sans-serif, system-ui, -apple-system, BlinkMacSystemFont, "Segoe UI", sans-serif;
                    background: #f5f7f9;
                    color: #17212b;
                }

                body {
                    margin: 0;
                    min-height: 100vh;
                    background: linear-gradient(180deg, #f5f7f9 0%, #e8edf2 100%);
                }

                main {
                    width: min(1080px, calc(100vw - 32px));
                    margin: 0 auto;
                    padding: 32px 0;
                }

                h1 {
                    margin: 0 0 8px;
                    font-size: 28px;
                    font-weight: 650;
                }

                .status {
                    margin: 0 0 24px;
                    color: #4d5b68;
                }

                table {
                    width: 100%;
                    border-collapse: collapse;
                    background: #fff;
                    border: 1px solid #d9e1e8;
                    border-radius: 8px;
                    overflow: hidden;
                }

                th, td {
                    padding: 14px 16px;
                    text-align: left;
                    border-bottom: 1px solid #e8edf2;
                    font-size: 14px;
                }

                th {
                    background: #edf2f6;
                    color: #33414f;
                    font-weight: 650;
                }

                tr:last-child td {
                    border-bottom: 0;
                }

                code {
                    font-family: "SFMono-Regular", Consolas, monospace;
                    color: #243447;
                }
            </style>
        </head>
        <body>
            <main>
                <h1>Trading Audit</h1>
                <p class="status">Local execution stack status and operator links.</p>
                <table>
                    <thead>
                        <tr>
                            <th>Service</th>
                            <th>Endpoint</th>
                            <th>Primary Use</th>
                        </tr>
                    </thead>
                    <tbody>
                        <tr>
                            <td>Execution</td>
                            <td><code>{{WebUtility.HtmlEncode(executionUrl)}}</code></td>
                            <td>Orders, fills, lifecycle events</td>
                        </tr>
                        <tr>
                            <td>Risk</td>
                            <td><code>{{WebUtility.HtmlEncode(riskUrl)}}</code></td>
                            <td>Portfolio and Greeks approval</td>
                        </tr>
                        <tr>
                            <td>Market Data</td>
                            <td><code>{{WebUtility.HtmlEncode(marketDataUrl)}}</code></td>
                            <td>IBKR-backed option quotes</td>
                        </tr>
                        <tr>
                            <td>RabbitMQ</td>
                            <td><code>{{WebUtility.HtmlEncode(rabbitUrl)}}</code></td>
                            <td>Execution event transport</td>
                        </tr>
                        <tr>
                            <td>Keycloak</td>
                            <td><code>{{WebUtility.HtmlEncode(keycloakUrl)}}</code></td>
                            <td>Local OIDC issuer</td>
                        </tr>
                    </tbody>
                </table>
            </main>
        </body>
        </html>
        """;

    return Results.Content(html, "text/html");
});

app.MapDefaultEndpoints();

app.Run();
