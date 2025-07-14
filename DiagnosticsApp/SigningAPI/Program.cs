var builder = WebApplication.CreateBuilder(args);
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();
builder.Services.AddHttpClient();

// Add Application Insights
builder.Services.AddApplicationInsightsTelemetry(options =>
{
    // Hardcoded instrumentation key - replace with actual key in production
    options.ConnectionString = "";
});

var app = builder.Build();

app.UseSwagger();
app.UseSwaggerUI();

app.MapGet("/hello", () => "Hello world!").WithOpenApi();

app.MapPost("/transaction", async (TransactionRequest request, HttpContext context, IHttpClientFactory httpClientFactory, Microsoft.ApplicationInsights.TelemetryClient telemetryClient) =>
{
    var client = httpClientFactory.CreateClient();
    
    var apiUrl = "https://solarapimuat.azure-api.net/casper/transaction";
    var response = await client.PostAsJsonAsync(apiUrl, request);
    
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
            context.Response.Headers.Append(header.Key, header.Value.ToArray());
        }
        
        // Store header values for metrics if they match our list of headers to track
        if (headerKeysToTrack.Contains(header.Key.ToLower()))
        {
            headerMetrics[header.Key.ToLowerInvariant()] = string.Join(",", header.Value);
        }
    }
    
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
    }
    
    // Return the response from the external API
    if (response.IsSuccessStatusCode)
    {
        var result = await response.Content.ReadFromJsonAsync<object>();
        return Results.Ok(result);
    }
    else
    {
        return Results.StatusCode((int)response.StatusCode);
    }
}).WithOpenApi();

app.Run();


public record TransactionRequest(Guid TransactionId);