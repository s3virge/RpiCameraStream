using RpiCameraStream.Components;
using RpiCameraStream.Services;

var builder = WebApplication.CreateBuilder(args);

// Bind to all network interfaces so the app is accessible by IP
builder.WebHost.UseUrls("http://0.0.0.0:5000", "https://0.0.0.0:5001");

// Add services to the container.
builder.Services.AddRazorComponents()
    .AddInteractiveServerComponents();

builder.Services.AddControllers();

// Register the camera service as a singleton + hosted service
builder.Services.AddSingleton<CameraService>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<CameraService>());

// Configure response buffering for streaming
builder.Services.Configure<Microsoft.AspNetCore.Server.Kestrel.Core.KestrelServerOptions>(options =>
{
    options.AllowSynchronousIO = true;
});

var app = builder.Build();

// Configure the HTTP request pipeline.
if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Error", createScopeForErrors: true);
}
app.UseStatusCodePagesWithReExecute("/not-found", createScopeForStatusCodePages: true);

app.UseAntiforgery();

app.MapStaticAssets();

// Map API controllers (for the camera stream endpoint)
app.MapControllers();

app.MapRazorComponents<App>()
    .AddInteractiveServerRenderMode();

app.Run();
