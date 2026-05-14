using System.Data.Common;
using System.Security.Claims;
using System.Text.Json;
using ClosedXML.Excel;
using EvaluacionDesempenoAB.Controllers;
using EvaluacionDesempenoAB.Models;
using EvaluacionDesempenoAB.Services;
using EvaluacionDesempenoAB.ViewModels;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.ViewFeatures;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.PowerPlatform.Dataverse.Client;

const string EvaluadorCorreo = "digital@aguasdebogota.com.co";
const string TestRunPrefix = "CODEXSMOKE20260514";

var repoRoot = ResolveRepositoryRoot();
var client = new ServiceClient(BuildConnectionString(repoRoot));
if (!client.IsReady)
{
    throw new InvalidOperationException($"Dataverse no esta listo: {client.LastError}");
}

var repo = new DataverseEvaluacionRepository(client);
var evaluador = await repo.GetUsuarioByCorreoAsync(EvaluadorCorreo)
    ?? throw new InvalidOperationException($"No se encontro el usuario evaluador {EvaluadorCorreo}.");
Assert(evaluador.EsSuperAdministrador, "El usuario evaluador debe ser superadministrador para probar descarga/importacion.");

var controller = BuildEvaluacionesController(repo, EvaluadorCorreo);
var plantilla = await controller.DescargarPlantillaUsuarios();
var plantillaFile = plantilla as FileContentResult
    ?? throw new InvalidOperationException($"La descarga de plantilla devolvio {plantilla.GetType().Name}.");
Assert(plantillaFile.FileContents.Length > 0, "La plantilla descargada debe tener contenido.");
Assert(plantillaFile.FileDownloadName.EndsWith(".xlsx", StringComparison.OrdinalIgnoreCase), "La plantilla debe descargarse como .xlsx.");

var excelBytes = BuildImportWorkbook(plantillaFile.FileContents, evaluador);
var importResult = await controller.ImportarUsuarios(BuildExcelFormFile(excelBytes));
Assert(importResult is RedirectToActionResult redirect && string.Equals(redirect.ActionName, "Index", StringComparison.Ordinal), "La importacion debe redirigir al Index.");

var importedUsers = new List<UsuarioEvaluado>();
for (var i = 1; i <= 5; i++)
{
    var cedula = BuildCedula(i);
    var usuario = await repo.GetUsuarioByCedulaAsync(cedula)
        ?? throw new InvalidOperationException($"No se encontro el usuario importado {cedula}.");

    Assert(usuario.Habilitado, $"El usuario {cedula} debe quedar habilitado.");
    Assert(string.Equals(usuario.EvaluadorNombre, EvaluadorCorreo, StringComparison.OrdinalIgnoreCase), $"El usuario {cedula} debe tener el evaluador principal por correo.");
    Assert(string.Equals(usuario.CorreoEvaluadorSst, EvaluadorCorreo, StringComparison.OrdinalIgnoreCase), $"El usuario {cedula} debe tener el evaluador SST por correo.");
    Assert(usuario.TipoFormulario == 433930002, $"El usuario {cedula} debe quedar con tipo de formulario Operativo.");
    importedUsers.Add(usuario);
}

var adminController = BuildAdminController(repo, EvaluadorCorreo);
var toggleTarget = importedUsers[4];
var disableResult = await adminController.ActualizarHabilitado(toggleTarget.Id, false, null);
Assert(disableResult is RedirectToActionResult, "Deshabilitar en Admin debe redirigir al Index.");
toggleTarget = await repo.GetUsuarioByCedulaAsync(toggleTarget.Cedula)
    ?? throw new InvalidOperationException("No se encontro el usuario despues de deshabilitar.");
Assert(!toggleTarget.Habilitado, "Admin debe cambiar Habilitado a No.");

var enableResult = await adminController.ActualizarHabilitado(toggleTarget.Id, true, null);
Assert(enableResult is RedirectToActionResult, "Habilitar en Admin debe redirigir al Index.");
toggleTarget = await repo.GetUsuarioByCedulaAsync(toggleTarget.Cedula)
    ?? throw new InvalidOperationException("No se encontro el usuario despues de habilitar.");
Assert(toggleTarget.Habilitado, "Admin debe cambiar Habilitado a Si.");

var usuarioEvaluado = importedUsers[0];
var niveles = await repo.GetNivelesActivosAsync();
var nivel = niveles.FirstOrDefault(n => string.Equals(n.Codigo, "OPE", StringComparison.OrdinalIgnoreCase))
    ?? throw new InvalidOperationException("No se encontro el nivel Operativo.");

var formularioResult = await controller.Formulario(usuarioEvaluado.Id, nivel.Id);
var formulario = AssertViewModel<EvaluacionFormularioViewModel>(formularioResult, "Formulario");
Assert(formulario.Competencias.Any(c => c.Nombre.Contains("SST", StringComparison.OrdinalIgnoreCase)), "El formulario debe incluir SST cuando el mismo usuario es evaluador SST.");
Assert(formulario.Competencias.Any(c => !c.Nombre.Contains("SST", StringComparison.OrdinalIgnoreCase)), "El formulario debe incluir competencias normales cuando el mismo usuario es evaluador principal.");
CompletarFormulario(formulario, 92);

var guardarResult = await controller.Guardar(formulario, "guardar");
var planRedirect = guardarResult as RedirectToActionResult
    ?? throw new InvalidOperationException($"Guardar devolvio {guardarResult.GetType().Name}.");
Assert(string.Equals(planRedirect.ActionName, "PlanAccion", StringComparison.Ordinal), "Guardar debe ir a PlanAccion.");
var evaluacionId = (Guid)(planRedirect.RouteValues?["id"] ?? Guid.Empty);
Assert(evaluacionId != Guid.Empty, "La evaluacion guardada debe tener id.");

var planModel = await BuildPlanModel(controller, evaluacionId);
var firmaExistente = await repo.DownloadFirmaUsuarioAsync(evaluador.Id);
var firmaArchivo = firmaExistente == null || firmaExistente.Contenido.Length == 0
    ? BuildPngFormFile("firma-smoke.png", BuildOnePixelPng())
    : BuildFormFile(
        "firma-smoke-existente",
        DetectFirmaContentType(firmaExistente.Contenido),
        firmaExistente.Contenido);

var guardarPlanResult = await controller.GuardarPlanAccion(planModel, firmaArchivo);
Assert(guardarPlanResult is RedirectToActionResult guardarPlanRedirect &&
       string.Equals(guardarPlanRedirect.ActionName, "PlanAccion", StringComparison.Ordinal),
    $"Guardar plan con firma debe redirigir a PlanAccion. Resultado: {guardarPlanResult.GetType().Name}");

var certificadoResult = await controller.ImprimirResultados(evaluacionId);
var certificadoView = certificadoResult as ViewResult
    ?? throw new InvalidOperationException($"Emitir certificado devolvio {certificadoResult.GetType().Name}.");
Assert(string.Equals(certificadoView.ViewName, "ReporteImpresion", StringComparison.Ordinal), "El certificado debe emitir la vista ReporteImpresion.");

Console.WriteLine("OK - smoke Dataverse completo.");
Console.WriteLine($"Plantilla descargada: {plantillaFile.FileContents.Length} bytes");
Console.WriteLine($"Usuarios importados: {string.Join(", ", importedUsers.Select(u => u.Cedula))}");
Console.WriteLine($"Toggle Admin validado: {toggleTarget.Cedula}");
Console.WriteLine($"Usuario evaluado: {usuarioEvaluado.Cedula} / Evaluacion: {evaluacionId}");
Console.WriteLine($"Certificado: {certificadoView.ViewName}");

static EvaluacionesController BuildEvaluacionesController(IEvaluacionRepository repo, string email)
{
    var controller = new EvaluacionesController(repo, NullLogger<EvaluacionesController>.Instance);
    var httpContext = new DefaultHttpContext
    {
        User = new ClaimsPrincipal(new ClaimsIdentity(
            new[] { new Claim("preferred_username", email), new Claim(ClaimTypes.Email, email) },
            "DataverseSmoke"))
    };

    controller.ControllerContext = new ControllerContext
    {
        HttpContext = httpContext
    };
    controller.TempData = new TempDataDictionary(httpContext, new InMemoryTempDataProvider());
    return controller;
}

static AdminController BuildAdminController(IEvaluacionRepository repo, string email)
{
    var controller = new AdminController(repo);
    var httpContext = new DefaultHttpContext
    {
        User = new ClaimsPrincipal(new ClaimsIdentity(
            new[] { new Claim("preferred_username", email), new Claim(ClaimTypes.Email, email) },
            "DataverseSmoke"))
    };

    controller.ControllerContext = new ControllerContext
    {
        HttpContext = httpContext
    };
    controller.TempData = new TempDataDictionary(httpContext, new InMemoryTempDataProvider());
    return controller;
}

static byte[] BuildImportWorkbook(byte[] templateBytes, UsuarioEvaluado evaluador)
{
    using var input = new MemoryStream(templateBytes);
    using var workbook = new XLWorkbook(input);
    var worksheet = workbook.Worksheet("Usuarios");

    for (var i = 1; i <= 5; i++)
    {
        var row = i + 1;
        worksheet.Cell(row, 1).Value = BuildCedula(i);
        worksheet.Cell(row, 2).Value = $"Usuario Test Codex {i}";
        worksheet.Cell(row, 3).Value = "Analista de prueba";
        worksheet.Cell(row, 4).Value = evaluador.NombreCompleto;
        worksheet.Cell(row, 5).Value = evaluador.Cargo ?? "Externo";
        worksheet.Cell(row, 6).Value = EvaluadorCorreo;
        worksheet.Cell(row, 7).Value = evaluador.NombreCompleto;
        worksheet.Cell(row, 8).Value = EvaluadorCorreo;
        worksheet.Cell(row, 9).Value = evaluador.Cargo ?? "Externo";
        worksheet.Cell(row, 10).Value = DateTime.Today.AddYears(-1).AddDays(-i);
        worksheet.Cell(row, 11).Value = DateTime.Today.AddMonths(8).AddDays(i);
        worksheet.Cell(row, 12).Value = DateTime.Today.AddMonths(-9).AddDays(i);
        worksheet.Cell(row, 13).Value = string.Empty;
        worksheet.Cell(row, 14).Value = "Gerencia Test Codex";
        worksheet.Cell(row, 15).Value = "Proyecto Test Codex";
        worksheet.Cell(row, 16).Value = "Operativo";
    }

    using var output = new MemoryStream();
    workbook.SaveAs(output);
    return output.ToArray();
}

static string BuildCedula(int index)
    => $"{TestRunPrefix}{index:00}";

static IFormFile BuildExcelFormFile(byte[] bytes)
    => BuildFormFile(
        "usuarios-smoke.xlsx",
        "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet",
        bytes,
        "archivo");

static IFormFile BuildPngFormFile(string fileName, byte[] bytes)
    => BuildFormFile(fileName, "image/png", bytes);

static IFormFile BuildFormFile(string fileName, string contentType, byte[] bytes, string formName = "firmaArchivo")
{
    var stream = new MemoryStream(bytes);
    return new FormFile(stream, 0, stream.Length, formName, fileName)
    {
        Headers = new HeaderDictionary(),
        ContentType = contentType
    };
}

static async Task<EvaluacionReporteViewModel> BuildPlanModel(EvaluacionesController controller, Guid evaluacionId)
{
    var result = await controller.PlanAccion(evaluacionId);
    var vm = AssertViewModel<EvaluacionReporteViewModel>(result, "Reporte");
    var opciones = vm.OpcionesPlanAccion.Take(1).ToList();

    vm.PlanAccion = opciones.Any()
        ? opciones.Select(o => new PlanAccionItemVm
            {
                ComportamientoId = o.ComportamientoId,
                Comportamiento = o.Comportamiento,
                Descripcion = "Plan de accion generado por smoke test Dataverse"
            })
            .ToList()
        : new List<PlanAccionItemVm>();

    return vm;
}

static void CompletarFormulario(EvaluacionFormularioViewModel vm, int puntaje)
{
    foreach (var comportamiento in vm.Competencias.SelectMany(c => c.Comportamientos))
    {
        comportamiento.Puntaje = puntaje;
        comportamiento.Comentario = "Respuesta generada por smoke test Dataverse";
    }
}

static T AssertViewModel<T>(IActionResult result, string expectedViewName)
{
    var view = result as ViewResult
        ?? throw new InvalidOperationException($"Se esperaba ViewResult {expectedViewName}, llego {result.GetType().Name}.");
    if (!string.Equals(view.ViewName, expectedViewName, StringComparison.Ordinal))
    {
        throw new InvalidOperationException($"Se esperaba vista {expectedViewName}, llego {view.ViewName}.");
    }

    return view.Model is T model
        ? model
        : throw new InvalidOperationException($"Modelo inesperado en {expectedViewName}.");
}

static string ResolveRepositoryRoot()
{
    var current = new DirectoryInfo(AppContext.BaseDirectory);
    while (current != null && !File.Exists(Path.Combine(current.FullName, "appsettings.json")))
    {
        current = current.Parent;
    }

    return current?.FullName
        ?? throw new InvalidOperationException("No se encontro appsettings.json.");
}

static string BuildConnectionString(string repoRoot)
{
    using var document = JsonDocument.Parse(File.ReadAllText(Path.Combine(repoRoot, "appsettings.json")));
    var root = document.RootElement;
    var raw = root.GetProperty("ConnectionStrings").GetProperty("Dataverse").GetString()
        ?? throw new InvalidOperationException("No hay ConnectionStrings:Dataverse.");
    var builder = new DbConnectionStringBuilder { ConnectionString = raw };

    if (!builder.ContainsKey("ClientSecret") &&
        root.TryGetProperty("AzureAd", out var azureAd) &&
        azureAd.TryGetProperty("ClientSecret", out var secret) &&
        !string.IsNullOrWhiteSpace(secret.GetString()))
    {
        builder["ClientSecret"] = secret.GetString();
    }

    builder.Remove("RedirectUri");
    builder.Remove("LoginPrompt");
    return builder.ConnectionString;
}

static byte[] BuildOnePixelPng()
    => Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAwMCAO+/p9sAAAAASUVORK5CYII=");

static string DetectFirmaContentType(byte[] bytes)
{
    if (bytes.Length >= 8 &&
        bytes[0] == 0x89 &&
        bytes[1] == 0x50 &&
        bytes[2] == 0x4E &&
        bytes[3] == 0x47)
    {
        return "image/png";
    }

    return "image/jpeg";
}

static void Assert(bool condition, string message)
{
    if (!condition)
    {
        throw new InvalidOperationException(message);
    }
}

sealed class InMemoryTempDataProvider : ITempDataProvider
{
    private readonly Dictionary<string, object> _data = new(StringComparer.OrdinalIgnoreCase);

    public IDictionary<string, object> LoadTempData(HttpContext context)
        => new Dictionary<string, object>(_data, StringComparer.OrdinalIgnoreCase);

    public void SaveTempData(HttpContext context, IDictionary<string, object> values)
    {
        _data.Clear();
        foreach (var item in values)
        {
            _data[item.Key] = item.Value;
        }
    }
}
