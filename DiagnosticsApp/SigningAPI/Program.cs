using Microsoft.AspNetCore.Mvc.Formatters;
using System.Linq;
using System.Reflection.PortableExecutable;

var builder = WebApplication.CreateBuilder(args);
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();
builder.Services.AddHttpClient();

// Add Application Insights
builder.Services.AddApplicationInsightsTelemetry(options =>
{
    // Hardcoded instrumentation key - replace with actual key in production
    options.ConnectionString = "InstrumentationKey=d6ed2722-7b8c-4350-ba39-e3cdc43e7ebb;IngestionEndpoint=https://westeurope-5.in.applicationinsights.azure.com/;LiveEndpoint=https://westeurope.livediagnostics.monitor.azure.com/;ApplicationId=2ce6b7ee-0cea-4cdd-9f1a-25402734997a";
});

var app = builder.Build();

app.UseSwagger();
app.UseSwaggerUI();

app.MapGet("/hello", () => "Hello world!").WithOpenApi();

app.MapPost("/transaction", async (TransactionRequest request, HttpContext context, IHttpClientFactory httpClientFactory, Microsoft.ApplicationInsights.TelemetryClient telemetryClient) =>
{

    var startTime = DateTime.UtcNow;

    var client = httpClientFactory.CreateClient();

    var apiUrl = "https://solarapimuat.azure-api.net/casper/transaction";
    var response = await client.PostAsJsonAsync(apiUrl, request);


    var endTime = DateTime.UtcNow;
    var duration = (endTime - startTime).TotalMilliseconds;

    var reponseProperties = new Dictionary<string, object>();

    // Headers we want to track
    var headerKeysToTrack = new[]
    {
        "x-apim-tx-duration",
        "x-apim-tx-endtime",
        "x-apim-tx-starttime",
        "x-casper-tx-duration",
        "x-casper-tx-endtime",
        "x-casper-tx-starttime"
    };

    // Dictionary to store header values we want to track
    var headerMetrics = new Dictionary<string, string>();

    // Forward the response headers from Casper API
    foreach (var header in response.Headers)
    {
        if (!context.Response.Headers.ContainsKey(header.Key))
        {
            reponseProperties.Add(header.Key, header.Value.ToArray());
        }

        // Store header values for metrics if they match our list of headers to track
        if (headerKeysToTrack.Contains(header.Key.ToLower()))
        {
            headerMetrics[header.Key.ToLowerInvariant()] = string.Join(",", header.Value);
        }
    }

    reponseProperties.Add("x-signin-startTime", startTime.ToString("o"));
    reponseProperties.Add("x-signin-endTime", endTime.ToString("o"));
    reponseProperties.Add("x-signin-duration", duration.ToString());

    // add our own metrics
    headerMetrics["x-signin-starttime"] = startTime.ToString("o");
    headerMetrics["x-signin-endtime"] = endTime.ToString("o");
    headerMetrics["x-signin-duration"] = duration.ToString();


    // Track headers as metrics
    if (headerMetrics.Any())
    {
        // Track transaction ID for correlation
        var properties = new Dictionary<string, string>
        {
            ["TransactionId"] = request.TransactionId.ToString()
        };

        // Add all headers to properties
        foreach (var header in headerMetrics)
        {
            properties[header.Key] = header.Value;
        }

        // Track as event with all properties
        telemetryClient.TrackEvent("ApiHeaderMetrics", properties);

        // Track specific numeric values as metrics
        if (headerMetrics.TryGetValue("x-apim-tx-duration", out var apimDurationStr) &&
            double.TryParse(apimDurationStr, out var apimDuration))
        {
            telemetryClient.TrackMetric("ApimTxDuration", apimDuration);
        }

        if (headerMetrics.TryGetValue("x-casper-tx-duration", out var casperDurationStr) &&
            double.TryParse(casperDurationStr, out var casperDuration))
        {
            telemetryClient.TrackMetric("CasperTxDuration", casperDuration);
        }

        if (headerMetrics.TryGetValue("x-signin-duration", out var signinDurationStr) &&
            double.TryParse(signinDurationStr, out var signinDuration))
        {
            telemetryClient.TrackMetric("SigninDuration", signinDuration);
        }
    }
    // return reponseProperties
    return Results.Ok(new { reponseProperties, request });

}).WithOpenApi();

app.Run();


public record TransactionRequest(Guid TransactionId);