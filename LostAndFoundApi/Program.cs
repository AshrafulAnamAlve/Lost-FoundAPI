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
