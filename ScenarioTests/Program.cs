using System.Security.Claims;
using EvaluacionDesempenoAB.Controllers;
using EvaluacionDesempenoAB.Models;
using EvaluacionDesempenoAB.Services;
using EvaluacionDesempenoAB.ViewModels;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
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

var certificadoSinFirmaSst = await normalController.ImprimirResultados(evaluacionId);
Assert(certificadoSinFirmaSst is BadRequestObjectResult, "El certificado se bloquea si falta la firma SST.");

var planSstSinFirma = BuildPlanModel(evaluacionId, sstController);
var guardarPlanSstSinFirma = await sstController.GuardarPlanAccion(planSstSinFirma, null);
Assert(guardarPlanSstSinFirma is ViewResult, "Guardar plan SST sin firma inicial queda bloqueado en la vista.");

var planSstConFirma = BuildPlanModel(evaluacionId, sstController);
var guardarPlanSst = await sstController.GuardarPlanAccion(planSstConFirma, BuildPngFormFile("firma-sst.png"));
AssertRedirect(guardarPlanSst, "PlanAccion", "Guardar plan SST con PNG válido funciona.");

var certificadoFinal = await normalController.ImprimirResultados(evaluacionId);
Assert(certificadoFinal is ViewResult view && string.Equals(view.ViewName, "ReporteImpresion", StringComparison.Ordinal), "El certificado se genera cuando ambas firmas existen.");

var actualizarFirmaTrasBloqueo = await normalController.SubirFirmaEvaluador(evaluacionId, BuildJpgFormFile("firma-normal-actualizada.jpg"));
Assert(actualizarFirmaTrasBloqueo is JsonResult, "La firma JPG puede actualizarse aunque el plan ya esté firmado.");

var actualizarFirmaFalsa = await normalController.SubirFirmaEvaluador(evaluacionId, BuildFakeJpgFormFile("firma-falsa-actualizada.jpg"));
Assert(actualizarFirmaFalsa is BadRequestObjectResult, "La actualización de firma rechaza un .jpg falso.");

var usuarioConFirmaPrevia = usuarios.Single(u => u.Cedula == "987654321");
usuarioConFirmaPrevia.FechaActivacionEvaluacion = DateTime.Today;
usuarioConFirmaPrevia.TipoFormulario = 433930002;
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

    return controller;
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

static void Assert(bool condition, string message)
{
    if (!condition)
    {
        throw new InvalidOperationException(message);
    }
}
