using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Threading.Tasks;
using EvaluacionDesempenoAB.Helpers;
using EvaluacionDesempenoAB.Models;
using EvaluacionDesempenoAB.Services;
using EvaluacionDesempenoAB.ViewModels;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace EvaluacionDesempenoAB.Controllers
{
    [Authorize]
    public class UsuariosController : Controller
    {
        private readonly IEvaluacionRepository _repo;
        private readonly IConfiguration _configuration;
        private readonly IHttpClientFactory _httpClientFactory;
        private readonly ILogger<UsuariosController> _logger;

        private sealed class EvaluacionCoberturaInfo
        {
            public bool EvaluacionNormalCompleta { get; init; }
            public bool EvaluacionSstCompleta { get; init; }
            public decimal? TotalCalculado { get; init; }
            public bool AmbasPartesCompletas => EvaluacionNormalCompleta && EvaluacionSstCompleta;
        }

        public UsuariosController(
            IEvaluacionRepository repo,
            IConfiguration configuration,
            IHttpClientFactory httpClientFactory,
            ILogger<UsuariosController> logger)
        {
            _repo = repo;
            _configuration = configuration;
            _httpClientFactory = httpClientFactory;
            _logger = logger;
        }

        private string? GetUserEmail()
        {
            return User.FindFirst("preferred_username")?.Value
                   ?? User.FindFirst(ClaimTypes.Email)?.Value
                   ?? User.FindFirst(ClaimTypes.Upn)?.Value;
        }

        private string GetCorreoActual(UsuarioEvaluado evaluador)
            => evaluador.CorreoElectronico ?? GetUserEmail() ?? string.Empty;

        private async Task<UsuarioEvaluado?> GetEvaluadorActualAsync()
        {
            var email = GetUserEmail();
            if (string.IsNullOrWhiteSpace(email))
            {
                return null;
            }

            return await _repo.GetUsuarioByCorreoAsync(email);
        }

        private bool PuedeAccederAUsuario(UsuarioEvaluado evaluadorActual, UsuarioEvaluado usuarioObjetivo)
            => evaluadorActual.EsSuperAdministrador
               || EvaluacionRolesHelper.TieneAcceso(
                   EvaluacionRolesHelper.ResolveParte(
                       usuarioObjetivo,
                       GetCorreoActual(evaluadorActual),
                       evaluadorActual.EsSuperAdministrador));

        private static bool IsWithinWindow(DateTime? targetDate, int windowDays = 25)
        {
            if (!targetDate.HasValue)
            {
                return false;
            }

            var start = targetDate.Value.Date.AddDays(-windowDays);
            var end = targetDate.Value.Date;
            var today = DateTime.Today;
            return today >= start && today <= end;
        }

        private static bool IsWithinActivationWindow(DateTime? activationDate, int windowDays = 25)
        {
            if (!activationDate.HasValue)
            {
                return false;
            }

            var start = activationDate.Value.Date;
            var end = activationDate.Value.Date.AddDays(windowDays);
            var today = DateTime.Today;
            return today >= start && today <= end;
        }

        private static Dictionary<Guid, Competencia> BuildCompetenciasLookup(IEnumerable<Competencia> competencias)
            => competencias
                .GroupBy(c => c.Id)
                .ToDictionary(g => g.Key, g => g.First());

        private static EvaluacionCoberturaInfo BuildCobertura(
            IReadOnlyCollection<EvaluacionDetalle> detalles,
            IReadOnlyCollection<Competencia> competencias,
            IReadOnlyCollection<Comportamiento> comportamientos)
        {
            var competenciasById = BuildCompetenciasLookup(competencias);
            var comportamientosNormales = new HashSet<Guid>();
            var comportamientosSst = new HashSet<Guid>();

            foreach (var comportamiento in comportamientos)
            {
                competenciasById.TryGetValue(comportamiento.CompetenciaId, out var competencia);
                if (EvaluacionRolesHelper.EsCompetenciaSst(competencia?.Nombre))
                {
                    comportamientosSst.Add(comportamiento.Id);
                }
                else
                {
                    comportamientosNormales.Add(comportamiento.Id);
                }
            }

            var respondidos = detalles
                .Where(d => d.ComportamientoId != Guid.Empty)
                .Select(d => d.ComportamientoId)
                .ToHashSet();

            var evaluacionNormalCompleta = comportamientosNormales.Count == 0 || comportamientosNormales.All(respondidos.Contains);
            var evaluacionSstCompleta = comportamientosSst.Count == 0 || comportamientosSst.All(respondidos.Contains);

            decimal? totalCalculado = null;
            if (evaluacionNormalCompleta && evaluacionSstCompleta && detalles.Any())
            {
                totalCalculado = Math.Round(detalles.Average(d => (decimal)d.Puntaje), 2);
            }

            return new EvaluacionCoberturaInfo
            {
                EvaluacionNormalCompleta = evaluacionNormalCompleta,
                EvaluacionSstCompleta = evaluacionSstCompleta,
                TotalCalculado = totalCalculado
            };
        }

        // ================== ACCIONES ==================

        public async Task<IActionResult> Index(string? cedula)
        {
            var evaluador = await GetEvaluadorActualAsync();
            if (evaluador == null)
            {
                return Forbid();
            }

            var usuarios = await _repo.GetUsuariosByEvaluadorAsync(GetCorreoActual(evaluador));
            if (!string.IsNullOrWhiteSpace(cedula))
            {
                usuarios = usuarios
                    .Where(usuario => usuario.Cedula?.Contains(cedula, StringComparison.OrdinalIgnoreCase) == true)
                    .ToList();
            }

            var competencias = await _repo.GetCompetenciasAsync();
            var comportamientosPorNivel = new Dictionary<Guid, List<Comportamiento>>();
            var vm = new List<UsuarioPortalEvaluadorViewModel>();

            foreach (var usuario in usuarios)
            {
                var evaluaciones = await _repo.GetEvaluacionesByUsuarioAsync(usuario.Id);
                Evaluacion? evaluacionActual = null;
                EvaluacionCoberturaInfo? coberturaActual = null;

                foreach (var evaluacion in evaluaciones.OrderByDescending(e => e.FechaEvaluacion))
                {
                    if (!comportamientosPorNivel.TryGetValue(evaluacion.NivelId, out var comportamientos))
                    {
                        comportamientos = await _repo.GetComportamientosByNivelAsync(evaluacion.NivelId);
                        comportamientosPorNivel[evaluacion.NivelId] = comportamientos;
                    }

                    var detalles = await _repo.GetDetallesByEvaluacionAsync(evaluacion.Id);
                    var cobertura = BuildCobertura(detalles, competencias, comportamientos);

                    if (evaluacionActual == null)
                    {
                        evaluacionActual = evaluacion;
                        coberturaActual = cobertura;
                    }

                    if (!cobertura.AmbasPartesCompletas)
                    {
                        evaluacionActual = evaluacion;
                        coberturaActual = cobertura;
                        break;
                    }
                }

                vm.Add(new UsuarioPortalEvaluadorViewModel
                {
                    Id = usuario.Id,
                    NombreCompleto = usuario.NombreCompleto,
                    Cedula = usuario.Cedula,
                    Cargo = usuario.Cargo,
                    Gerencia = usuario.Gerencia,
                    CorreoElectronico = usuario.CorreoElectronico,
                    FechaInicioContrato = usuario.FechaInicioContrato,
                    FechaFinalizacionContrato = usuario.FechaFinalizacionContrato,
                    FechaFinalizacionPeriodoPrueba = usuario.FechaFinalizacionPeriodoPrueba,
                    FechaActivacionEvaluacion = usuario.FechaActivacionEvaluacion,
                    EvaluacionActualId = evaluacionActual?.Id,
                    EvaluacionNormalCompleta = coberturaActual?.EvaluacionNormalCompleta ?? false,
                    EvaluacionSstCompleta = coberturaActual?.EvaluacionSstCompleta ?? false,
                    ResultadoFinal = coberturaActual?.AmbasPartesCompletas == true
                        ? evaluacionActual?.Total ?? coberturaActual.TotalCalculado
                        : null
                });
            }

            ViewData["CedulaFiltro"] = cedula;
            return View(vm);
        }

        [HttpGet]
        public async Task<IActionResult> Detalle(Guid id)
        {
            var evaluador = await GetEvaluadorActualAsync();
            if (evaluador == null)
            {
                return Forbid();
            }

            var usuario = await _repo.GetUsuarioByIdAsync(id);
            if (usuario == null)
            {
                return NotFound();
            }

            if (!PuedeAccederAUsuario(evaluador, usuario))
            {
                return Forbid();
            }

            return RedirectToAction("CarpetaUsuario", "Evaluaciones", new { usuarioId = id });
        }

        public class SolicitudActivacionRequest
        {
            public Guid UsuarioId { get; set; }
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> SolicitarActivacion([FromBody] SolicitudActivacionRequest request)
        {
            if (request == null || request.UsuarioId == Guid.Empty)
            {
                return BadRequest("Usuario inválido.");
            }

            var evaluador = await GetEvaluadorActualAsync();
            if (evaluador == null)
            {
                return Forbid();
            }

            var usuario = await _repo.GetUsuarioByIdAsync(request.UsuarioId);
            if (usuario == null)
            {
                return NotFound();
            }

            if (!PuedeAccederAUsuario(evaluador, usuario))
            {
                return Forbid();
            }

            var flowUrl = _configuration["PowerAutomate:SolicitudActivacionEvaluacionUrl"];
            if (string.IsNullOrWhiteSpace(flowUrl))
            {
                return StatusCode(500, "No se encontró la URL del flujo de Power Automate.");
            }

            string Safe(string? value) => value ?? string.Empty;

            var payload = new
            {
                usuarioId = usuario.Id,
                usuarioNombre = Safe(usuario.NombreCompleto),
                usuarioCedula = Safe(usuario.Cedula),
                usuarioCorreo = Safe(usuario.CorreoElectronico),
                evaluadorNombre = Safe(evaluador.NombreCompleto),
                evaluadorCorreo = Safe(GetCorreoActual(evaluador)),
                fechaFinalizacionContrato = usuario.FechaFinalizacionContrato?.ToString("yyyy-MM-dd") ?? string.Empty,
                fechaFinalizacionPeriodoPrueba = usuario.FechaFinalizacionPeriodoPrueba?.ToString("yyyy-MM-dd") ?? string.Empty
            };

            var client = _httpClientFactory.CreateClient();
            var response = await client.PostAsJsonAsync(flowUrl, payload);
            var responseContent = await response.Content.ReadAsStringAsync();
            if (!response.IsSuccessStatusCode)
            {
                _logger.LogWarning(
                    "Power Automate respondió con error. Status: {StatusCode}. Body: {ResponseBody}",
                    (int)response.StatusCode,
                    responseContent);

                return StatusCode(
                    (int)response.StatusCode,
                    string.IsNullOrWhiteSpace(responseContent)
                        ? "No se pudo enviar la solicitud."
                        : responseContent);
            }

            _logger.LogInformation(
                "Solicitud de activación enviada a Power Automate. Status: {StatusCode}. Body: {ResponseBody}",
                (int)response.StatusCode,
                responseContent);

            return Ok(new { message = "Solicitud enviada." });
        }

        [HttpGet]
        public async Task<IActionResult> EstadoActivacion(Guid usuarioId)
        {
            if (usuarioId == Guid.Empty)
            {
                return BadRequest("Usuario inválido.");
            }

            var evaluador = await GetEvaluadorActualAsync();
            if (evaluador == null)
            {
                return Forbid();
            }

            var usuario = await _repo.GetUsuarioByIdAsync(usuarioId);
            if (usuario == null)
            {
                return NotFound();
            }

            if (!PuedeAccederAUsuario(evaluador, usuario))
            {
                return Forbid();
            }

            var habilitadoPorFechas =
                IsWithinWindow(usuario.FechaFinalizacionContrato) ||
                IsWithinWindow(usuario.FechaFinalizacionPeriodoPrueba);
            var habilitadoPorActivacion = IsWithinActivationWindow(usuario.FechaActivacionEvaluacion);
            var puedeIniciar = habilitadoPorFechas || habilitadoPorActivacion;

            return Json(new
            {
                puedeIniciar,
                puedeSolicitar = !puedeIniciar,
                fechaActivacionEvaluacion = usuario.FechaActivacionEvaluacion?.ToString("yyyy-MM-dd")
            });
        }
    }
}
