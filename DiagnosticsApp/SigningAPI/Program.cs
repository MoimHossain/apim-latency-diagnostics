using Microsoft.AspNetCore.Mvc.Formatters;
using System.Linq;
using System.Reflection.PortableExecutable;
using System.Diagnostics;
using System.Globalization;

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
    var stopwatch = Stopwatch.StartNew();
    var startTime = DateTime.UtcNow;

    var client = httpClientFactory.CreateClient();

    var apiUrl = "https://solarapimuat.azure-api.net/k8s-transaction/transaction";
    var response = await client.PostAsJsonAsync(apiUrl, request);
    
    // Measure time when headers arrive
    var headersArrivedTime = stopwatch.ElapsedMilliseconds;
    
    // make sure the full response is read
    string body = string.Empty;
    if(response.IsSuccessStatusCode)
    {
        body = await response.Content.ReadAsStringAsync();
    }

    // Measure time when full body is read
    stopwatch.Stop();
    var fullBodyReadTime = stopwatch.ElapsedMilliseconds;
    var endTime = DateTime.UtcNow;
    var totalDuration = (endTime - startTime).TotalMilliseconds;

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
    reponseProperties.Add("x-signin-duration", totalDuration.ToString());
    reponseProperties.Add("x-signin-headers-arrived-time", headersArrivedTime.ToString());
    reponseProperties.Add("x-signin-full-body-read-time", fullBodyReadTime.ToString());

    // add our own metrics
    headerMetrics["x-signin-starttime"] = startTime.ToString("o");
    headerMetrics["x-signin-endtime"] = endTime.ToString("o");
    headerMetrics["x-signin-duration"] = totalDuration.ToString();
    headerMetrics["x-signin-headers-arrived-time"] = headersArrivedTime.ToString();
    headerMetrics["x-signin-full-body-read-time"] = fullBodyReadTime.ToString();

    // Validate APIM duration calculation
    if (headerMetrics.TryGetValue("x-apim-tx-starttime", out var apimStartStr) &&
        headerMetrics.TryGetValue("x-apim-tx-endtime", out var apimEndStr) &&
        headerMetrics.TryGetValue("x-apim-tx-duration", out var apimDurationStr))
    {
        if (DateTime.TryParse(apimStartStr, null, DateTimeStyles.RoundtripKind, out var apimStart) &&
            DateTime.TryParse(apimEndStr, null, DateTimeStyles.RoundtripKind, out var apimEnd) &&
            double.TryParse(apimDurationStr, out var apimReportedDuration))
        {
            var apimCalculatedDuration = (apimEnd - apimStart).TotalMilliseconds;
            var apimDurationDiff = Math.Abs(apimCalculatedDuration - apimReportedDuration);
            
            headerMetrics["x-apim-calculated-duration"] = apimCalculatedDuration.ToString();
            headerMetrics["x-apim-duration-diff"] = apimDurationDiff.ToString();
            
            reponseProperties.Add("x-apim-calculated-duration", apimCalculatedDuration.ToString());
            reponseProperties.Add("x-apim-duration-diff", apimDurationDiff.ToString());
        }
    }

    // Validate Casper duration calculation
    if (headerMetrics.TryGetValue("x-casper-tx-starttime", out var casperStartStr) &&
        headerMetrics.TryGetValue("x-casper-tx-endtime", out var casperEndStr) &&
        headerMetrics.TryGetValue("x-casper-tx-duration", out var casperDurationStr))
    {
        if (DateTime.TryParse(casperStartStr, null, DateTimeStyles.RoundtripKind, out var casperStart) &&
            DateTime.TryParse(casperEndStr, null, DateTimeStyles.RoundtripKind, out var casperEnd) &&
            double.TryParse(casperDurationStr, out var casperReportedDuration))
        {
            var casperCalculatedDuration = (casperEnd - casperStart).TotalMilliseconds;
            var casperDurationDiff = Math.Abs(casperCalculatedDuration - casperReportedDuration);
            
            headerMetrics["x-casper-calculated-duration"] = casperCalculatedDuration.ToString();
            headerMetrics["x-casper-duration-diff"] = casperDurationDiff.ToString();
            
            reponseProperties.Add("x-casper-calculated-duration", casperCalculatedDuration.ToString());
            reponseProperties.Add("x-casper-duration-diff", casperDurationDiff.ToString());
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
        if (headerMetrics.TryGetValue("x-apim-tx-duration", out var apimDurationStr2) &&
            double.TryParse(apimDurationStr2, out var apimDuration))
        {
            telemetryClient.TrackMetric("ApimTxDuration", apimDuration);
        }

        if (headerMetrics.TryGetValue("x-casper-tx-duration", out var casperDurationStr2) &&
            double.TryParse(casperDurationStr2, out var casperDuration))
        {
            telemetryClient.TrackMetric("CasperTxDuration", casperDuration);
        }

        if (headerMetrics.TryGetValue("x-signin-duration", out var signinDurationStr) &&
            double.TryParse(signinDurationStr, out var signinDuration))
        {
            telemetryClient.TrackMetric("SigninDuration", signinDuration);
        }

        // Track new timing metrics
        telemetryClient.TrackMetric("SigninHeadersArrivedTime", headersArrivedTime);
        telemetryClient.TrackMetric("SigninFullBodyReadTime", fullBodyReadTime);

        // Track validation metrics
        if (headerMetrics.TryGetValue("x-apim-calculated-duration", out var apimCalcStr) &&
            double.TryParse(apimCalcStr, out var apimCalc))
        {
            telemetryClient.TrackMetric("ApimCalculatedDuration", apimCalc);
        }

        if (headerMetrics.TryGetValue("x-apim-duration-diff", out var apimDiffStr) &&
            double.TryParse(apimDiffStr, out var apimDiff))
        {
            telemetryClient.TrackMetric("ApimDurationDiff", apimDiff);
        }

        if (headerMetrics.TryGetValue("x-casper-calculated-duration", out var casperCalcStr) &&
            double.TryParse(casperCalcStr, out var casperCalc))
        {
            telemetryClient.TrackMetric("CasperCalculatedDuration", casperCalc);
        }

        if (headerMetrics.TryGetValue("x-casper-duration-diff", out var casperDiffStr) &&
            double.TryParse(casperDiffStr, out var casperDiff))
        {
            telemetryClient.TrackMetric("CasperDurationDiff", casperDiff);
        }
    }
    // return reponseProperties
    return Results.Ok(new { reponseProperties, request });

}).WithOpenApi();

app.MapPost("/transaction-direct", async (TransactionRequest request, HttpContext context, IHttpClientFactory httpClientFactory, Microsoft.ApplicationInsights.TelemetryClient telemetryClient) =>
{
    var stopwatch = Stopwatch.StartNew();
    var startTime = DateTime.UtcNow;

    var client = httpClientFactory.CreateClient();

    var apiUrl = "https://casper-providerapp-hxb6c9ged6hfapgy.westeurope-01.azurewebsites.net/transaction";
    var response = await client.PostAsJsonAsync(apiUrl, request);

    // Measure time when headers arrive
    var headersArrivedTime = stopwatch.ElapsedMilliseconds;

    // make sure the full response is read
    string body = string.Empty;
    if (response.IsSuccessStatusCode)
    {
        body = await response.Content.ReadAsStringAsync();
    }

    // Measure time when full body is read
    stopwatch.Stop();
    var fullBodyReadTime = stopwatch.ElapsedMilliseconds;
    var endTime = DateTime.UtcNow;
    var totalDuration = (endTime - startTime).TotalMilliseconds;

    var reponseProperties = new Dictionary<string, object>();

    // Headers we want to track
    var headerKeysToTrack = new[]
    {
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
    reponseProperties.Add("x-signin-duration", totalDuration.ToString());
    reponseProperties.Add("x-signin-headers-arrived-time", headersArrivedTime.ToString());
    reponseProperties.Add("x-signin-full-body-read-time", fullBodyReadTime.ToString());

    // add our own metrics
    headerMetrics["x-signin-starttime"] = startTime.ToString("o");
    headerMetrics["x-signin-endtime"] = endTime.ToString("o");
    headerMetrics["x-signin-duration"] = totalDuration.ToString();
    headerMetrics["x-signin-headers-arrived-time"] = headersArrivedTime.ToString();
    headerMetrics["x-signin-full-body-read-time"] = fullBodyReadTime.ToString();



    // Validate Casper duration calculation
    if (headerMetrics.TryGetValue("x-casper-tx-starttime", out var casperStartStr) &&
        headerMetrics.TryGetValue("x-casper-tx-endtime", out var casperEndStr) &&
        headerMetrics.TryGetValue("x-casper-tx-duration", out var casperDurationStr))
    {
        if (DateTime.TryParse(casperStartStr, null, DateTimeStyles.RoundtripKind, out var casperStart) &&
            DateTime.TryParse(casperEndStr, null, DateTimeStyles.RoundtripKind, out var casperEnd) &&
            double.TryParse(casperDurationStr, out var casperReportedDuration))
        {
            var casperCalculatedDuration = (casperEnd - casperStart).TotalMilliseconds;
            var casperDurationDiff = Math.Abs(casperCalculatedDuration - casperReportedDuration);

            headerMetrics["x-casper-calculated-duration"] = casperCalculatedDuration.ToString();
            headerMetrics["x-casper-duration-diff"] = casperDurationDiff.ToString();

            reponseProperties.Add("x-casper-calculated-duration", casperCalculatedDuration.ToString());
            reponseProperties.Add("x-casper-duration-diff", casperDurationDiff.ToString());
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



        if (headerMetrics.TryGetValue("x-casper-tx-duration", out var casperDurationStr2) &&
            double.TryParse(casperDurationStr2, out var casperDuration))
        {
            telemetryClient.TrackMetric("CasperTxDuration", casperDuration);
        }

        if (headerMetrics.TryGetValue("x-signin-duration", out var signinDurationStr) &&
            double.TryParse(signinDurationStr, out var signinDuration))
        {
            telemetryClient.TrackMetric("SigninDuration", signinDuration);
        }

        // Track new timing metrics
        telemetryClient.TrackMetric("SigninHeadersArrivedTime", headersArrivedTime);
        telemetryClient.TrackMetric("SigninFullBodyReadTime", fullBodyReadTime);


        if (headerMetrics.TryGetValue("x-casper-calculated-duration", out var casperCalcStr) &&
            double.TryParse(casperCalcStr, out var casperCalc))
        {
            telemetryClient.TrackMetric("CasperCalculatedDuration", casperCalc);
        }

        if (headerMetrics.TryGetValue("x-casper-duration-diff", out var casperDiffStr) &&
            double.TryParse(casperDiffStr, out var casperDiff))
        {
            telemetryClient.TrackMetric("CasperDurationDiff", casperDiff);
        }
    }
    // return reponseProperties
    return Results.Ok(new { reponseProperties, request });

}).WithOpenApi();


app.MapPost("/transaction/{requestId}", async (string requestId, TransactionRequest request, HttpContext context, IHttpClientFactory httpClientFactory, Microsoft.ApplicationInsights.TelemetryClient telemetryClient) =>
{
    var stopwatch = Stopwatch.StartNew();
    var startTime = DateTime.UtcNow;

    var client = httpClientFactory.CreateClient();

    var apiUrl = $"https://solarapimuat.azure-api.net/k8s-transaction/transaction/{requestId}";
    var response = await client.PostAsJsonAsync(apiUrl, request);

    // Measure time when headers arrive
    var headersArrivedTime = stopwatch.ElapsedMilliseconds;

    // make sure the full response is read
    string body = string.Empty;
    if (response.IsSuccessStatusCode)
    {
        body = await response.Content.ReadAsStringAsync();
    }

    // Measure time when full body is read
    stopwatch.Stop();
    var fullBodyReadTime = stopwatch.ElapsedMilliseconds;
    var endTime = DateTime.UtcNow;
    var totalDuration = (endTime - startTime).TotalMilliseconds;

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
    reponseProperties.Add("x-signin-duration", totalDuration.ToString());
    reponseProperties.Add("x-signin-headers-arrived-time", headersArrivedTime.ToString());
    reponseProperties.Add("x-signin-full-body-read-time", fullBodyReadTime.ToString());

    // add our own metrics
    headerMetrics["x-signin-starttime"] = startTime.ToString("o");
    headerMetrics["x-signin-endtime"] = endTime.ToString("o");
    headerMetrics["x-signin-duration"] = totalDuration.ToString();
    headerMetrics["x-signin-headers-arrived-time"] = headersArrivedTime.ToString();
    headerMetrics["x-signin-full-body-read-time"] = fullBodyReadTime.ToString();

    // Validate APIM duration calculation
    if (headerMetrics.TryGetValue("x-apim-tx-starttime", out var apimStartStr) &&
        headerMetrics.TryGetValue("x-apim-tx-endtime", out var apimEndStr) &&
        headerMetrics.TryGetValue("x-apim-tx-duration", out var apimDurationStr))
    {
        if (DateTime.TryParse(apimStartStr, null, DateTimeStyles.RoundtripKind, out var apimStart) &&
            DateTime.TryParse(apimEndStr, null, DateTimeStyles.RoundtripKind, out var apimEnd) &&
            double.TryParse(apimDurationStr, out var apimReportedDuration))
        {
            var apimCalculatedDuration = (apimEnd - apimStart).TotalMilliseconds;
            var apimDurationDiff = Math.Abs(apimCalculatedDuration - apimReportedDuration);

            headerMetrics["x-apim-calculated-duration"] = apimCalculatedDuration.ToString();
            headerMetrics["x-apim-duration-diff"] = apimDurationDiff.ToString();

            reponseProperties.Add("x-apim-calculated-duration", apimCalculatedDuration.ToString());
            reponseProperties.Add("x-apim-duration-diff", apimDurationDiff.ToString());
        }
    }

    // Validate Casper duration calculation
    if (headerMetrics.TryGetValue("x-casper-tx-starttime", out var casperStartStr) &&
        headerMetrics.TryGetValue("x-casper-tx-endtime", out var casperEndStr) &&
        headerMetrics.TryGetValue("x-casper-tx-duration", out var casperDurationStr))
    {
        if (DateTime.TryParse(casperStartStr, null, DateTimeStyles.RoundtripKind, out var casperStart) &&
            DateTime.TryParse(casperEndStr, null, DateTimeStyles.RoundtripKind, out var casperEnd) &&
            double.TryParse(casperDurationStr, out var casperReportedDuration))
        {
            var casperCalculatedDuration = (casperEnd - casperStart).TotalMilliseconds;
            var casperDurationDiff = Math.Abs(casperCalculatedDuration - casperReportedDuration);

            headerMetrics["x-casper-calculated-duration"] = casperCalculatedDuration.ToString();
            headerMetrics["x-casper-duration-diff"] = casperDurationDiff.ToString();

            reponseProperties.Add("x-casper-calculated-duration", casperCalculatedDuration.ToString());
            reponseProperties.Add("x-casper-duration-diff", casperDurationDiff.ToString());
        }
    }

    // Track headers as metrics
    if (headerMetrics.Any())
    {
        // Track transaction ID for correlation
        var properties = new Dictionary<string, string>
        {
            ["TransactionId"] = request.TransactionId.ToString(),
            ["RequestId"] = requestId
        };

        // Add all headers to properties
        foreach (var header in headerMetrics)
        {
            properties[header.Key] = header.Value;
        }

        // Track as event with all properties
        telemetryClient.TrackEvent("ApiHeaderMetrics", properties);

        // Track specific numeric values as metrics
        if (headerMetrics.TryGetValue("x-apim-tx-duration", out var apimDurationStr2) &&
            double.TryParse(apimDurationStr2, out var apimDuration))
        {
            telemetryClient.TrackMetric("ApimTxDuration", apimDuration);
        }

        if (headerMetrics.TryGetValue("x-casper-tx-duration", out var casperDurationStr2) &&
            double.TryParse(casperDurationStr2, out var casperDuration))
        {
            telemetryClient.TrackMetric("CasperTxDuration", casperDuration);
        }

        if (headerMetrics.TryGetValue("x-signin-duration", out var signinDurationStr) &&
            double.TryParse(signinDurationStr, out var signinDuration))
        {
            telemetryClient.TrackMetric("SigninDuration", signinDuration);
        }

        // Track new timing metrics
        telemetryClient.TrackMetric("SigninHeadersArrivedTime", headersArrivedTime);
        telemetryClient.TrackMetric("SigninFullBodyReadTime", fullBodyReadTime);

        // Track validation metrics
        if (headerMetrics.TryGetValue("x-apim-calculated-duration", out var apimCalcStr) &&
            double.TryParse(apimCalcStr, out var apimCalc))
        {
            telemetryClient.TrackMetric("ApimCalculatedDuration", apimCalc);
        }

        if (headerMetrics.TryGetValue("x-apim-duration-diff", out var apimDiffStr) &&
            double.TryParse(apimDiffStr, out var apimDiff))
        {
            telemetryClient.TrackMetric("ApimDurationDiff", apimDiff);
        }

        if (headerMetrics.TryGetValue("x-casper-calculated-duration", out var casperCalcStr) &&
            double.TryParse(casperCalcStr, out var casperCalc))
        {
            telemetryClient.TrackMetric("CasperCalculatedDuration", casperCalc);
        }

        if (headerMetrics.TryGetValue("x-casper-duration-diff", out var casperDiffStr) &&
            double.TryParse(casperDiffStr, out var casperDiff))
        {
            telemetryClient.TrackMetric("CasperDurationDiff", casperDiff);
        }
    }
    // return reponseProperties
    return Results.Ok(new { reponseProperties, request });

}).WithOpenApi();

app.Run();


public record TransactionRequest(Guid TransactionId);