using LostAndFoundApi.Hubs;
using LostAndFoundApi.Models;
using LostAndFoundApi.Services;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.EntityFrameworkCore;

// Uploaded photos are written to wwwroot/uploads and served as static files. The
// static file server only watches wwwroot if the folder exists when the app starts,
// so a deployment that ships without one would save photos it can never serve.
Directory.CreateDirectory(Path.Combine(Directory.GetCurrentDirectory(), "wwwroot", "uploads"));

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
// all-MiniLM-L6-v2 run inside this process (MLModels/text). First in the embedding
// provider chain, so semantic matching no longer depends on a metered API.
builder.Services.AddSingleton<ITextEmbeddingService, OnnxTextEmbeddingService>();
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

// Sign-in issues a signed token (see AuthTokenService); every endpoint that changes
// data reads the user from it instead of trusting a user id sent in the request.
builder.Services.AddSingleton<AuthTokenService>();
builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme).AddJwtBearer();
builder.Services.AddOptions<JwtBearerOptions>(JwtBearerDefaults.AuthenticationScheme)
    .Configure<AuthTokenService>((options, tokens) =>
    {
        options.MapInboundClaims = false;
        options.TokenValidationParameters = tokens.ValidationParameters;
        options.Events = new JwtBearerEvents
        {
            // Browsers cannot set headers on a WebSocket, so SignalR sends the token
            // as a query parameter. Accepted for the hub only.
            OnMessageReceived = context =>
            {
                var token = context.Request.Query["access_token"];
                if (!string.IsNullOrEmpty(token) && context.HttpContext.Request.Path.StartsWithSegments("/chatHub"))
                    context.Token = token;
                return Task.CompletedTask;
            },
        };
    });
builder.Services.AddAuthorization();

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
app.UseAuthentication();
app.UseAuthorization();

app.MapControllers();
app.MapHub<ChatHub>("/chatHub");

app.Run();
