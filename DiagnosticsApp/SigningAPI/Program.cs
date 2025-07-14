var builder = WebApplication.CreateBuilder(args);
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();
builder.Services.AddHttpClient();


var app = builder.Build();

app.UseSwagger();
app.UseSwaggerUI();

app.MapGet("/hello", () => "Hello world!").WithOpenApi();

app.MapPost("/transaction", async (TransactionRequest request, HttpContext context, IHttpClientFactory httpClientFactory) =>
{
    var client = httpClientFactory.CreateClient();
    
    var apiUrl = "https://solarapimuat.azure-api.net/casper/transaction";
    var response = await client.PostAsJsonAsync(apiUrl, request);
    
    // Forward the response headers from Casper API
    foreach (var header in response.Headers)
    {
        if (!context.Response.Headers.ContainsKey(header.Key))
        {
            context.Response.Headers.Append(header.Key, header.Value.ToArray());
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