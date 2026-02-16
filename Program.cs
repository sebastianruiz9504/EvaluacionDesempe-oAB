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

    var validationErrors = ValidateDataverseConnectionString(connString);
    if (validationErrors.Count > 0)
    {
        logger.LogError(
            "Cadena de conexión de Dataverse inválida. Problemas detectados: {ValidationErrors}. Se usará MockRepository.",
            string.Join(" | ", validationErrors));
        return null;
    }

    logger.LogInformation("Resumen de cadena de conexión Dataverse: {ConnectionStringSummary}", SummarizeConnectionString(connString));

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
        logger.LogError(
            ex,
            "No fue posible conectar a Dataverse. Diagnóstico: {DataverseErrorClassification}. Se usará MockRepository.",
            ClassifyDataverseException(ex));
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
    DbConnectionStringBuilder builder;

    try
    {
        builder = new DbConnectionStringBuilder
        {
            ConnectionString = connectionString
        };
    }
    catch (Exception ex)
    {
        logger.LogError(ex, "La cadena de conexión de Dataverse no tiene formato válido.");
        return null;
    }

    var originalAuthType = builder.TryGetValue("AuthType", out var authTypeValue)
        ? authTypeValue?.ToString()?.Trim()
        : null;

    logger.LogInformation(
        "Configuración inicial de Dataverse: AuthType={AuthType}, tiene Url={HasUrl}, tiene ClientId={HasClientId}, tiene TenantId={HasTenantId}, tiene ClientSecret={HasClientSecret}",
        string.IsNullOrWhiteSpace(originalAuthType) ? "(vacío)" : originalAuthType,
        builder.ContainsKey("Url"),
        builder.ContainsKey("ClientId"),
        builder.ContainsKey("TenantId"),
        builder.ContainsKey("ClientSecret") || !string.IsNullOrWhiteSpace(clientSecret));

    var authType = originalAuthType;

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

    if (builder.TryGetValue("Url", out var rawUrlValue))
    {
        var normalizedUrl = NormalizeDataverseUrl(rawUrlValue?.ToString());

        if (!string.IsNullOrWhiteSpace(normalizedUrl))
        {
            builder["Url"] = normalizedUrl;
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

static List<string> ValidateDataverseConnectionString(string connectionString)
{
    var errors = new List<string>();
    DbConnectionStringBuilder builder;

    try
    {
        builder = new DbConnectionStringBuilder { ConnectionString = connectionString };
    }
    catch (Exception ex)
    {
        errors.Add($"Formato inválido de cadena de conexión: {ex.Message}");
        return errors;
    }

    string? GetValue(string key)
        => builder.TryGetValue(key, out var value) ? value?.ToString()?.Trim() : null;

    var authType = GetValue("AuthType");
    var url = GetValue("Url");

    if (string.IsNullOrWhiteSpace(authType))
    {
        errors.Add("Falta AuthType.");
    }

    if (string.IsNullOrWhiteSpace(url))
    {
        errors.Add("Falta Url.");
    }
    else if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || string.IsNullOrWhiteSpace(uri.Host))
    {
        var suggestedUrl = NormalizeDataverseUrl(url);
        errors.Add($"Url no es una URI absoluta válida. Valor recibido: '{url}'. Sugerencia: '{suggestedUrl ?? "sin sugerencia"}'.");
    }

    if (string.Equals(authType, "ClientSecret", StringComparison.OrdinalIgnoreCase))
    {
        if (string.IsNullOrWhiteSpace(GetValue("ClientId")))
        {
            errors.Add("Falta ClientId para AuthType=ClientSecret.");
        }

        if (string.IsNullOrWhiteSpace(GetValue("TenantId")))
        {
            errors.Add("Falta TenantId para AuthType=ClientSecret.");
        }

        if (string.IsNullOrWhiteSpace(GetValue("ClientSecret")))
        {
            errors.Add("Falta ClientSecret para AuthType=ClientSecret.");
        }
    }

    return errors;
}

static string? NormalizeDataverseUrl(string? rawUrl)
{
    if (string.IsNullOrWhiteSpace(rawUrl))
    {
        return null;
    }

    var normalized = rawUrl.Trim();

    // Algunos proveedores inyectan comillas en variables de entorno.
    if ((normalized.StartsWith('"') && normalized.EndsWith('"'))
        || (normalized.StartsWith('\'') && normalized.EndsWith('\'')))
    {
        normalized = normalized[1..^1].Trim();
    }

    // Dataverse requiere URI absoluta; si viene sólo host, asumimos https.
    if (!normalized.Contains("://", StringComparison.Ordinal))
    {
        normalized = $"https://{normalized}";
    }

    if (!Uri.TryCreate(normalized, UriKind.Absolute, out var uri) || string.IsNullOrWhiteSpace(uri.Host))
    {
        return null;
    }

    return uri.GetLeftPart(UriPartial.Authority);
}

static string SummarizeConnectionString(string connectionString)
{
    try
    {
        var builder = new DbConnectionStringBuilder { ConnectionString = connectionString };

        string? GetValue(string key)
            => builder.TryGetValue(key, out var value) ? value?.ToString()?.Trim() : null;

        var authType = GetValue("AuthType") ?? "(vacío)";
        var url = GetValue("Url") ?? "(vacío)";
        var clientId = GetValue("ClientId");
        var tenantId = GetValue("TenantId");

        return $"AuthType={authType}; Url={url}; ClientId={MaskIdentifier(clientId)}; TenantId={MaskIdentifier(tenantId)}; ClientSecret={(string.IsNullOrWhiteSpace(GetValue("ClientSecret")) ? "No" : "Sí")}";
    }
    catch (Exception)
    {
        return "No se pudo resumir la cadena de conexión.";
    }
}

static string MaskIdentifier(string? value)
{
    if (string.IsNullOrWhiteSpace(value))
    {
        return "(vacío)";
    }

    if (value.Length <= 8)
    {
        return "********";
    }

    return $"{value[..4]}...{value[^4..]}";
}

static string ClassifyDataverseException(Exception ex)
{
    Exception? current = ex;

    while (current != null)
    {
        var message = current.Message?.ToLowerInvariant() ?? string.Empty;

        if (message.Contains("unauthorized") || message.Contains("forbidden") || message.Contains("permission") || message.Contains("access denied"))
        {
            return "Posible problema de permisos/roles en Dataverse o en la App Registration.";
        }

        if (message.Contains("aad") || message.Contains("tenant") || message.Contains("clientsecret") || message.Contains("invalid_client") || message.Contains("invalid_grant"))
        {
            return "Posible error de autenticación AAD (TenantId/ClientId/ClientSecret/AuthType).";
        }

        if (message.Contains("dns") || message.Contains("host") || message.Contains("name or service not known") || message.Contains("timed out") || message.Contains("socket"))
        {
            return "Posible error de red o URL de Dataverse inválida/no accesible.";
        }

        current = current.InnerException;
    }

    return "Error no clasificado. Revise stack trace e InnerException para detalle.";
}
