using System;
using EvaluacionDesempenoAB.Services;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.Identity.Web;
using Microsoft.PowerPlatform.Dataverse.Client;

var builder = WebApplication.CreateBuilder(args);

// MVC
builder.Services.AddControllersWithViews();

builder.Services.AddHttpClient();

// HttpContextAccessor
builder.Services.AddHttpContextAccessor();

//
// 🔐 Autenticación con Microsoft (Entra ID)
//  Usa la sección "AzureAd" de appsettings.json
//  OJO: NO llamamos AddCookie aquí porque
//  AddMicrosoftIdentityWebApp ya registra el esquema Cookies.
//
builder.Services
    .AddAuthentication(OpenIdConnectDefaults.AuthenticationScheme)
    .AddMicrosoftIdentityWebApp(builder.Configuration.GetSection("AzureAd"));

builder.Services.AddAuthorization(options =>
{
    // Todo requiere usuario autenticado por defecto
    options.FallbackPolicy = options.DefaultPolicy;
});

//
// 🔗 Conexión a Dataverse (con fallback a MockRepository si no hay cadena de conexión)
//
builder.Services.AddSingleton<ServiceClient?>(sp =>
{
    var config = sp.GetRequiredService<IConfiguration>();
    var logger = sp.GetRequiredService<ILogger<Program>>();
    var connString = config.GetConnectionString("Dataverse");

    if (string.IsNullOrWhiteSpace(connString))
    {
        // No hay cadena de conexión: trabajamos con datos en memoria (MockRepository)
        return null;
    }

    try
    {
        var client = new ServiceClient(connString, logger);

        if (!client.IsReady)
        {
            logger.LogError("No fue posible conectar con Dataverse. Se usará MockRepository. Detalle: {Error}", client.LastError);
            client.Dispose();
            return null;
        }

        return client;
    }
    catch (Exception ex)
    {
        logger.LogError(ex, "Error al crear ServiceClient de Dataverse. Se usará MockRepository.");
        return null;
    }
});

// Elegir repositorio según haya (o no) Dataverse
builder.Services.AddScoped<IEvaluacionRepository>(sp =>
{
    var client = sp.GetService<ServiceClient?>();
    if (client == null)
    {
        // Fallback a Mock
        return new MockRepository();
    }

    return new DataverseEvaluacionRepository(client);
});

var app = builder.Build();

// Manejo de errores y HSTS en producción
if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Home/Error");
    app.UseHsts();
}

//app.UseHttpsRedirection();
app.UseStaticFiles();

app.UseRouting();

// 🔐 Autenticación & autorización
app.UseAuthentication();
app.UseAuthorization();

// Ruta por defecto MVC
app.MapControllerRoute(
    name: "default",
    pattern: "{controller=Home}/{action=Index}/{id?}");

app.Run();
