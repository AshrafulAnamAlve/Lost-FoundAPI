using LostAndFoundApi.Hubs;
using LostAndFoundApi.Models;
using LostAndFoundApi.Services;
using Microsoft.EntityFrameworkCore;

var builder = WebApplication.CreateBuilder(args);

// Add services to the container.

builder.Services.AddControllers();
// Learn more about configuring OpenAPI at https://aka.ms/aspnet/openapi
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();

builder.Services.AddDbContext<AppDbContext>(option =>
{
    option.UseSqlServer(builder.Configuration.GetConnectionString("LAFConntection"));
});

builder.Services.AddHttpClient();
builder.Services.AddSingleton<IItemSimilarityService, ItemSimilarityService>();
// Two ways to run the same image model, picked once at startup.
//
// "onnx"    - load MLModels/model.onnx into this process (no Python, ships with
//             the publish, works on the .NET-only host the API actually lives on).
// "service" - POST it to /ml_service, the Python app. Right when the model is
//             hosted somewhere with more memory than shared hosting has.
// "auto"    - the default: prefer in-process when the model file is there, and
//             fall back to the HTTP service when it is not.
//
// Auto resolves the same way in development and production, so what is tested
// locally is what runs live. Both are singletons: each holds something expensive
// (an ONNX session / an HttpClient factory handle) that is meant to be shared.
builder.Services.AddSingleton<IItemClassificationService>(provider =>
{
    var mode = builder.Configuration["ImageClassification:Provider"];

    IItemClassificationService Onnx() =>
        ActivatorUtilities.CreateInstance<OnnxItemClassificationService>(provider);

    IItemClassificationService Service() =>
        ActivatorUtilities.CreateInstance<ItemClassificationService>(provider);

    if (string.Equals(mode, "service", StringComparison.OrdinalIgnoreCase)) return Service();
    if (string.Equals(mode, "onnx", StringComparison.OrdinalIgnoreCase)) return Onnx();

    var onnx = Onnx();
    return onnx.IsConfigured ? onnx : Service();
});
builder.Services.AddSignalR();

builder.Services.AddCors(option =>
{
    option.AddPolicy("AllowAll", policy =>
    {
        // SignalR is cross-origin (UI :4200 -> API :7124) and needs credentials,
        // so AllowAnyOrigin can't be used together with AllowCredentials.
        policy.SetIsOriginAllowed(_ => true)
              .AllowAnyMethod()
              .AllowAnyHeader()
              .AllowCredentials();
    });
});


var app = builder.Build();

// Configure the HTTP request pipeline.

app.UseSwagger();
app.UseSwaggerUI();

using var serviceScope = app.Services.CreateScope();
using var dbContext = serviceScope.ServiceProvider.GetService<AppDbContext>();
dbContext?.Database.Migrate();


app.UseHttpsRedirection();
app.UseStaticFiles();
app.UseCors("AllowAll");
app.UseAuthorization();

app.MapControllers();
app.MapHub<ChatHub>("/chatHub");

app.Run();
