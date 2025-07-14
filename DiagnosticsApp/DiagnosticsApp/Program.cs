var builder = WebApplication.CreateBuilder(args);
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();

var app = builder.Build();

app.UseSwagger();
app.UseSwaggerUI();

app.MapGet("/hello", () => "Hello world!").WithOpenApi();

app.MapPost("/transaction", (TransactionRequest request, HttpContext context) =>
{
    var startTime = DateTime.UtcNow;
    
    // Process the transaction (in a real app, you'd do more here)
    

    var endTime = DateTime.UtcNow;
    var duration = (endTime - startTime).TotalMilliseconds;        
    context.Response.Headers.Append("X-CASPER-TX-STARTTIME", startTime.ToString("o"));
    context.Response.Headers.Append("X-CASPER-TX-ENDTIME", endTime.ToString("o"));
    context.Response.Headers.Append("X-CASPER-TX-DURATION", duration.ToString());
    return Results.Ok(request);
}).WithOpenApi();

app.Run();


public record TransactionRequest(Guid TransactionId);