using System.Security.Claims;
using EvaluacionDesempenoAB.Controllers;
using EvaluacionDesempenoAB.Models;
using EvaluacionDesempenoAB.Services;
using EvaluacionDesempenoAB.ViewModels;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

var repo = new MockRepository();
var usuarios = await repo.GetUsuariosAsync();
var usuarioEvaluado = usuarios.Single(u => u.Cedula == "123456789");
var nivel = (await repo.GetNivelesActivosAsync()).Single(n => n.Codigo == "OPE");

var evaluadorNormal = "evaluador.demo@contoso.com";
var evaluadorSst = "evaluador.sst@contoso.com";

var normalController = BuildController(repo, evaluadorNormal);
var formularioNormal = await GetFormularioAsync(normalController, usuarioEvaluado.Id, nivel.Id);
Assert(formularioNormal.Competencias.Any(), "El evaluador normal ve competencias.");
Assert(formularioNormal.Competencias.All(c => !c.Nombre.Contains("SST", StringComparison.OrdinalIgnoreCase)), "El evaluador normal no ve CULTURA SST.");
CompletarFormulario(formularioNormal, 80);

var guardarNormal = await normalController.Guardar(formularioNormal, "guardar");
var redirectPlanNormal = AssertRedirect(guardarNormal, "PlanAccion", "Guardar normal redirige a plan de acción.");
var evaluacionId = (Guid)(redirectPlanNormal.RouteValues?["id"] ?? Guid.Empty);
Assert(evaluacionId != Guid.Empty, "La evaluación guardada debe devolver un id.");

var planNormalSinFirma = BuildPlanModel(evaluacionId, normalController);
var guardarPlanSinFirma = await normalController.GuardarPlanAccion(planNormalSinFirma, null);
Assert(guardarPlanSinFirma is ViewResult, "Guardar plan normal sin firma inicial queda bloqueado en la vista.");

var planNormalFirmaFalsa = BuildPlanModel(evaluacionId, normalController);
var guardarPlanFirmaFalsa = await normalController.GuardarPlanAccion(planNormalFirmaFalsa, BuildFakeJpgFormFile("firma-falsa.jpg"));
Assert(guardarPlanFirmaFalsa is ViewResult, "Guardar plan con archivo .jpg falso queda bloqueado en la vista.");

var planNormalConFirma = BuildPlanModel(evaluacionId, normalController);
var guardarPlanConFirma = await normalController.GuardarPlanAccion(planNormalConFirma, BuildJpgFormFile("firma-normal.jpg", "application/octet-stream"));
AssertRedirect(guardarPlanConFirma, "PlanAccion", "Guardar plan normal con JPG válido funciona aunque el content-type venga raro.");

var sstController = BuildController(repo, evaluadorSst);
var formularioSst = await GetEditarAsync(sstController, evaluacionId);
Assert(formularioSst.Competencias.Any(), "El evaluador SST ve competencias.");
Assert(formularioSst.Competencias.All(c => c.Nombre.Contains("SST", StringComparison.OrdinalIgnoreCase)), "El evaluador SST solo ve CULTURA SST.");
CompletarFormulario(formularioSst, 80);

var guardarSst = await sstController.Guardar(formularioSst, "guardar");
AssertRedirect(guardarSst, "PlanAccion", "Guardar SST redirige a plan de acción.");

var estadoCertificadoSinFirmaSstNormal = await normalController.EstadoCertificado(evaluacionId);
AssertJsonString(estadoCertificadoSinFirmaSstNormal, "status", "otherMissingSignature", "El evaluador normal ve aviso cuando falta la firma del evaluador SST.");

var estadoCertificadoSinFirmaSst = await sstController.EstadoCertificado(evaluacionId);
AssertJsonString(estadoCertificadoSinFirmaSst, "status", "currentMissingSignature", "El evaluador SST recibe solicitud de subir su propia firma.");

var certificadoSinFirmaSst = await normalController.ImprimirResultados(evaluacionId);
Assert(certificadoSinFirmaSst is BadRequestObjectResult, "El certificado se bloquea si falta la firma SST.");

var planSstSinFirma = BuildPlanModel(evaluacionId, sstController);
var guardarPlanSstSinFirma = await sstController.GuardarPlanAccion(planSstSinFirma, null);
Assert(guardarPlanSstSinFirma is ViewResult, "Guardar plan SST sin firma inicial queda bloqueado en la vista.");

var subirFirmaSstDesdeCertificado = await sstController.SubirFirmaEvaluador(evaluacionId, BuildPngFormFile("firma-sst-popup.png"));
Assert(subirFirmaSstDesdeCertificado is JsonResult, "El popup de certificado permite subir firma PNG del evaluador actual.");

var estadoCertificadoListoDesdePopup = await normalController.EstadoCertificado(evaluacionId);
AssertJsonString(estadoCertificadoListoDesdePopup, "status", "ready", "El certificado queda listo despues de subir la firma desde el popup.");

var planSstConFirma = BuildPlanModel(evaluacionId, sstController);
var guardarPlanSst = await sstController.GuardarPlanAccion(planSstConFirma, BuildPngFormFile("firma-sst.png"));
AssertRedirect(guardarPlanSst, "PlanAccion", "Guardar plan SST con PNG válido funciona.");

var estadoCertificadoListo = await normalController.EstadoCertificado(evaluacionId);
AssertJsonString(estadoCertificadoListo, "status", "ready", "El estado del certificado queda listo cuando ambas firmas existen.");

var certificadoFinal = await normalController.ImprimirResultados(evaluacionId);
Assert(certificadoFinal is ViewResult view && string.Equals(view.ViewName, "ReporteImpresion", StringComparison.Ordinal), "El certificado se genera cuando ambas firmas existen.");

var actualizarFirmaTrasBloqueo = await normalController.SubirFirmaEvaluador(evaluacionId, BuildJpgFormFile("firma-normal-actualizada.jpg"));
Assert(actualizarFirmaTrasBloqueo is JsonResult, "El botón Subir o actualizar firma puede guardar una firma JPG aunque el plan ya esté firmado.");

var actualizarFirmaFalsa = await normalController.SubirFirmaEvaluador(evaluacionId, BuildFakeJpgFormFile("firma-falsa-actualizada.jpg"));
Assert(actualizarFirmaFalsa is BadRequestObjectResult, "La actualización de firma rechaza un .jpg falso.");

var usuarioConFirmaPrevia = usuarios.Single(u => u.Cedula == "987654321");
usuarioConFirmaPrevia.TipoFormulario = 433930002;

var usuariosController = BuildUsuariosController(repo, evaluadorNormal);
usuarioConFirmaPrevia.FechaActivacionEvaluacion = DateTime.Today;
usuarioConFirmaPrevia.Habilitado = false;
var estadoSinHabilitar = await usuariosController.EstadoActivacion(usuarioConFirmaPrevia.Id);
AssertJsonBool(estadoSinHabilitar, "puedeIniciar", false, "El estado no habilita iniciar evaluación aunque exista fecha si Habilitado está en No.");
var nuevaSinHabilitar = await normalController.Nueva(usuarioConFirmaPrevia.Id);
Assert(nuevaSinHabilitar is BadRequestObjectResult, "Nueva evaluación queda bloqueada cuando Habilitado está en No.");

usuarioConFirmaPrevia.FechaActivacionProgramada = DateTime.Today;
var estadoHabilitado = await usuariosController.EstadoActivacion(usuarioConFirmaPrevia.Id);
Assert(usuarioConFirmaPrevia.Habilitado, "La fecha de activación programada vencida cambia Habilitado a Sí.");
AssertJsonBool(estadoHabilitado, "puedeIniciar", true, "El estado habilita iniciar evaluación cuando Habilitado está en Sí.");
var nuevaHabilitada = await normalController.Nueva(usuarioConFirmaPrevia.Id);
AssertRedirect(nuevaHabilitada, "Formulario", "Nueva evaluación redirige al formulario cuando Habilitado está en Sí.");

var formularioNormalConFirmaPrevia = await GetFormularioAsync(normalController, usuarioConFirmaPrevia.Id, nivel.Id);
CompletarFormulario(formularioNormalConFirmaPrevia, 80);
var guardarNormalConFirmaPrevia = await normalController.Guardar(formularioNormalConFirmaPrevia, "guardar");
var redirectPlanConFirmaPrevia = AssertRedirect(guardarNormalConFirmaPrevia, "PlanAccion", "Guardar normal con firma previa redirige a plan de acción.");
var evaluacionConFirmaPreviaId = (Guid)(redirectPlanConFirmaPrevia.RouteValues?["id"] ?? Guid.Empty);
Assert(evaluacionConFirmaPreviaId != Guid.Empty, "La evaluación con firma previa debe devolver un id.");
var planConFirmaPreviaSinArchivo = BuildPlanModel(evaluacionConFirmaPreviaId, normalController);
var guardarPlanConFirmaPreviaSinArchivo = await normalController.GuardarPlanAccion(planConFirmaPreviaSinArchivo, null);
AssertRedirect(guardarPlanConFirmaPreviaSinArchivo, "PlanAccion", "Si el evaluador ya tiene firma válida, puede guardar plan sin subirla de nuevo.");

Console.WriteLine("OK - escenarios reales simulados en memoria completados.");

static EvaluacionesController BuildController(IEvaluacionRepository repo, string email)
{
    var controller = new EvaluacionesController(repo, NullLogger<EvaluacionesController>.Instance);
    ApplyUser(controller, email);
    return controller;
}

static UsuariosController BuildUsuariosController(IEvaluacionRepository repo, string email)
{
    var configuration = new ConfigurationBuilder()
        .AddInMemoryCollection(new Dictionary<string, string?>())
        .Build();
    var controller = new UsuariosController(
        repo,
        configuration,
        new FakeHttpClientFactory(),
        NullLogger<UsuariosController>.Instance);

    ApplyUser(controller, email);
    return controller;
}

static void ApplyUser(Controller controller, string email)
{
    var identity = new ClaimsIdentity(
        new[] { new Claim("preferred_username", email), new Claim(ClaimTypes.Email, email) },
        "ScenarioTest");

    controller.ControllerContext = new ControllerContext
    {
        HttpContext = new DefaultHttpContext
        {
            User = new ClaimsPrincipal(identity)
        }
    };
}

static async Task<EvaluacionFormularioViewModel> GetFormularioAsync(EvaluacionesController controller, Guid usuarioId, Guid nivelId)
{
    var result = await controller.Formulario(usuarioId, nivelId);
    return AssertViewModel<EvaluacionFormularioViewModel>(result, "Formulario");
}

static async Task<EvaluacionFormularioViewModel> GetEditarAsync(EvaluacionesController controller, Guid evaluacionId)
{
    var result = await controller.Editar(evaluacionId);
    return AssertViewModel<EvaluacionFormularioViewModel>(result, "Formulario");
}

static EvaluacionReporteViewModel BuildPlanModel(Guid evaluacionId, EvaluacionesController controller)
{
    var result = controller.PlanAccion(evaluacionId).GetAwaiter().GetResult();
    var vm = AssertViewModel<EvaluacionReporteViewModel>(result, "Reporte");
    var opciones = vm.OpcionesPlanAccion.Take(1).ToList();

    vm.PlanAccion = opciones.Any()
        ? opciones.Select(o => new PlanAccionItemVm
            {
                ComportamientoId = o.ComportamientoId,
                Comportamiento = o.Comportamiento,
                Descripcion = "Plan temporal de validación automatizada"
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
    }
}

static IFormFile BuildJpgFormFile(string fileName, string contentType = "image/jpeg")
{
    var bytes = new byte[]
    {
        0xFF, 0xD8, 0xFF, 0xE0, 0x00, 0x10, 0x4A, 0x46,
        0x49, 0x46, 0x00, 0x01, 0x01, 0x01, 0x00, 0x48,
        0x00, 0x48, 0x00, 0x00, 0xFF, 0xD9
    };
    var stream = new MemoryStream(bytes);
    return new FormFile(stream, 0, stream.Length, "firmaArchivo", fileName)
    {
        Headers = new HeaderDictionary(),
        ContentType = contentType
    };
}

static IFormFile BuildPngFormFile(string fileName)
{
    var bytes = new byte[]
    {
        0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A,
        0x00, 0x00, 0x00, 0x0D
    };
    var stream = new MemoryStream(bytes);
    return new FormFile(stream, 0, stream.Length, "firmaArchivo", fileName)
    {
        Headers = new HeaderDictionary(),
        ContentType = "image/png"
    };
}

static IFormFile BuildFakeJpgFormFile(string fileName)
{
    var bytes = "esto-no-es-una-imagen"u8.ToArray();
    var stream = new MemoryStream(bytes);
    return new FormFile(stream, 0, stream.Length, "firmaArchivo", fileName)
    {
        Headers = new HeaderDictionary(),
        ContentType = "image/jpeg"
    };
}

static T AssertViewModel<T>(IActionResult result, string expectedViewName)
{
    if (result is not ViewResult view)
    {
        throw new InvalidOperationException($"Se esperaba ViewResult {expectedViewName}, llegó {result.GetType().Name}.");
    }

    if (!string.Equals(view.ViewName, expectedViewName, StringComparison.Ordinal))
    {
        throw new InvalidOperationException($"Se esperaba vista {expectedViewName}, llegó {view.ViewName}.");
    }

    if (view.Model is not T model)
    {
        throw new InvalidOperationException($"Modelo inesperado en {expectedViewName}.");
    }

    return model;
}

static RedirectToActionResult AssertRedirect(IActionResult result, string expectedAction, string message)
{
    if (result is not RedirectToActionResult redirect ||
        !string.Equals(redirect.ActionName, expectedAction, StringComparison.Ordinal))
    {
        throw new InvalidOperationException($"{message} Resultado: {result.GetType().Name}.");
    }

    return redirect;
}

static void AssertJsonBool(IActionResult result, string propertyName, bool expected, string message)
{
    if (result is not JsonResult { Value: not null } json)
    {
        throw new InvalidOperationException($"{message} Resultado: {result.GetType().Name}.");
    }

    var property = json.Value.GetType().GetProperty(propertyName);
    var value = property?.GetValue(json.Value);
    Assert(value is bool boolValue && boolValue == expected, message);
}

static void AssertJsonString(IActionResult result, string propertyName, string expected, string message)
{
    if (result is not JsonResult { Value: not null } json)
    {
        throw new InvalidOperationException($"{message} Resultado: {result.GetType().Name}.");
    }

    var property = json.Value.GetType().GetProperty(propertyName);
    var value = property?.GetValue(json.Value)?.ToString();
    Assert(string.Equals(value, expected, StringComparison.Ordinal), $"{message} Valor: {value ?? "(nulo)"}.");
}

static void Assert(bool condition, string message)
{
    if (!condition)
    {
        throw new InvalidOperationException(message);
    }
}

sealed class FakeHttpClientFactory : IHttpClientFactory
{
    public HttpClient CreateClient(string name) => new();
}
