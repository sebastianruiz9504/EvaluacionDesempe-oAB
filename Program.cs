using System;
using System.Data.Common;
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
    var loggerFactory = sp.GetRequiredService<ILoggerFactory>();
    var logger = loggerFactory.CreateLogger("DataverseConnection");

    var connString = config.GetConnectionString("Dataverse");

    if (string.IsNullOrWhiteSpace(connString))
    {
        // No hay cadena de conexión: trabajamos con datos en memoria (MockRepository)
        return null;
    }

    var dataverseClientSecret = ResolveDataverseClientSecret(config);
    connString = BuildDataverseConnectionString(connString, dataverseClientSecret, logger);

    if (string.IsNullOrWhiteSpace(connString))
    {
        return null;
    }

    try
    {
        var client = new ServiceClient(connString, logger);

        if (!client.IsReady)
        {
            logger.LogWarning("Dataverse no quedó listo. Se usará MockRepository. Error: {LastError}", client.LastError);
            return null;
        }

        return client;
    }
    catch (Exception ex)
    {
        logger.LogError(ex, "No fue posible conectar a Dataverse. Se usará MockRepository.");
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


static string? BuildDataverseConnectionString(string connectionString, string? clientSecret, ILogger logger)
{
    var builder = new DbConnectionStringBuilder
    {
        ConnectionString = connectionString
    };

    var authType = builder.TryGetValue("AuthType", out var authTypeValue)
        ? authTypeValue?.ToString()?.Trim()
        : null;

    if (string.IsNullOrWhiteSpace(authType) && !string.IsNullOrWhiteSpace(clientSecret))
    {
        builder["AuthType"] = "ClientSecret";
    }

    var currentSecret = builder.TryGetValue("ClientSecret", out var existingSecret)
        ? existingSecret?.ToString()
        : null;

    if (string.IsNullOrWhiteSpace(currentSecret) && !string.IsNullOrWhiteSpace(clientSecret))
    {
        builder["ClientSecret"] = clientSecret;
    }

    var effectiveAuthType = builder.TryGetValue("AuthType", out var effectiveAuthTypeValue)
        ? effectiveAuthTypeValue?.ToString()?.Trim()
        : null;

    if (string.Equals(effectiveAuthType, "ClientSecret", StringComparison.OrdinalIgnoreCase))
    {
        var effectiveSecret = builder.TryGetValue("ClientSecret", out var clientSecretValue)
            ? clientSecretValue?.ToString()
            : null;

        if (string.IsNullOrWhiteSpace(effectiveSecret))
        {
            logger.LogError("Dataverse está configurado con AuthType=ClientSecret, pero no hay ClientSecret. Configure Dataverse__ClientSecret o ConnectionStrings__Dataverse con ClientSecret.");
            return null;
        }

        // Estos parámetros son propios de flujos interactivos y pueden forzar errores loopback_redirect_uri.
        if (builder.ContainsKey("RedirectUri"))
        {
            builder.Remove("RedirectUri");
        }

        if (builder.ContainsKey("LoginPrompt"))
        {
            builder.Remove("LoginPrompt");
        }
    }

    return builder.ConnectionString;
}

static string? ResolveDataverseClientSecret(IConfiguration configuration)
{
    return configuration["Dataverse:ClientSecret"]
        ?? configuration["ConnectionStrings:DataverseClientSecret"]
        ?? configuration["AzureAd:ClientSecret"];
}
