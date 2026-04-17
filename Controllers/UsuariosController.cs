using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text;
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

        private async Task<Evaluacion?> SelectPreferredEvaluacionAsync(IEnumerable<Evaluacion> candidates)
        {
            var candidatos = candidates
                .Where(x => x.Id != Guid.Empty)
                .ToList();

            if (candidatos.Count == 0)
            {
                return null;
            }

            if (candidatos.Count == 1)
            {
                return candidatos[0];
            }

            var detalles = await _repo.GetDetallesByEvaluacionesAsync(candidatos.Select(x => x.Id));
            var conteoDetalles = detalles
                .Where(x => x.EvaluacionId != Guid.Empty)
                .GroupBy(x => x.EvaluacionId)
                .ToDictionary(g => g.Key, g => g.Count());

            return candidatos
                .OrderByDescending(x => conteoDetalles.TryGetValue(x.Id, out var totalDetalles) ? totalDetalles : 0)
                .ThenBy(x => x.FechaEvaluacion)
                .First();
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

        private static bool EsEvaluacionInicial(Evaluacion evaluacion)
            => evaluacion.Id != Guid.Empty &&
               !evaluacion.EvaluacionOrigenId.HasValue &&
               string.Equals(evaluacion.TipoEvaluacion, "Inicial", StringComparison.OrdinalIgnoreCase);

        private async Task<EvaluacionCoberturaInfo> GetCoberturaAsync(
            Evaluacion evaluacion,
            IReadOnlyCollection<Competencia> competencias,
            IDictionary<Guid, List<Comportamiento>> comportamientosPorNivel,
            IDictionary<Guid, EvaluacionCoberturaInfo> coberturasPorEvaluacion)
        {
            if (coberturasPorEvaluacion.TryGetValue(evaluacion.Id, out var coberturaCacheada))
            {
                return coberturaCacheada;
            }

            if (!comportamientosPorNivel.TryGetValue(evaluacion.NivelId, out var comportamientos))
            {
                comportamientos = await _repo.GetComportamientosByNivelAsync(evaluacion.NivelId);
                comportamientosPorNivel[evaluacion.NivelId] = comportamientos;
            }

            var detalles = await _repo.GetDetallesByEvaluacionAsync(evaluacion.Id);
            var cobertura = BuildCobertura(detalles, competencias, comportamientos);
            coberturasPorEvaluacion[evaluacion.Id] = cobertura;
            return cobertura;
        }

        private static bool EstaParteCompleta(EvaluacionCoberturaInfo cobertura, TipoParteEvaluacion parte)
        {
            if (parte == (TipoParteEvaluacion.Normal | TipoParteEvaluacion.Sst))
            {
                return cobertura.AmbasPartesCompletas;
            }

            if (EvaluacionRolesHelper.TieneParte(parte, TipoParteEvaluacion.Normal))
            {
                return cobertura.EvaluacionNormalCompleta;
            }

            if (EvaluacionRolesHelper.TieneParte(parte, TipoParteEvaluacion.Sst))
            {
                return cobertura.EvaluacionSstCompleta;
            }

            return false;
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
            var coberturasPorEvaluacion = new Dictionary<Guid, EvaluacionCoberturaInfo>();
            var vm = new List<UsuarioPortalEvaluadorViewModel>();

            foreach (var usuario in usuarios)
            {
                var parteActual = EvaluacionRolesHelper.ResolveParte(
                    usuario,
                    GetCorreoActual(evaluador),
                    evaluador.EsSuperAdministrador);
                var evaluaciones = await _repo.GetEvaluacionesByUsuarioAsync(usuario.Id);
                var ventanaActiva = EvaluacionCicloHelper.ResolveVentanaActiva(usuario);
                var evaluacionGuardadaEnVentana = ventanaActiva == null
                    ? null
                    : await SelectPreferredEvaluacionAsync(
                        evaluaciones.Where(x => EvaluacionCicloHelper.PerteneceAVentanaInicial(x, ventanaActiva)));
                Evaluacion? evaluacionActual = null;
                EvaluacionCoberturaInfo? coberturaActual = null;
                Evaluacion? evaluacionPendienteParaParte = null;
                var encontroEvaluacionPendiente = false;

                foreach (var evaluacion in evaluaciones.OrderByDescending(e => e.FechaEvaluacion))
                {
                    var cobertura = await GetCoberturaAsync(
                        evaluacion,
                        competencias,
                        comportamientosPorNivel,
                        coberturasPorEvaluacion);

                    if (evaluacionActual == null)
                    {
                        evaluacionActual = evaluacion;
                        coberturaActual = cobertura;
                    }

                    if (evaluacionPendienteParaParte == null &&
                        EsEvaluacionInicial(evaluacion) &&
                        !EstaParteCompleta(cobertura, parteActual))
                    {
                        evaluacionPendienteParaParte = evaluacion;
                    }

                    if (!cobertura.AmbasPartesCompletas)
                    {
                        if (!encontroEvaluacionPendiente)
                        {
                            evaluacionActual = evaluacion;
                            coberturaActual = cobertura;
                            encontroEvaluacionPendiente = true;
                        }
                    }
                }

                var tieneEvaluacionActiva = evaluacionPendienteParaParte != null;
                var puedeCrearNueva = ventanaActiva != null && evaluacionGuardadaEnVentana == null;
                var puedeIniciarOContinuar = tieneEvaluacionActiva || puedeCrearNueva;

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
                    EvaluacionActualId = evaluacionPendienteParaParte?.Id,
                    EvaluacionNormalCompleta = coberturaActual?.EvaluacionNormalCompleta ?? false,
                    EvaluacionSstCompleta = coberturaActual?.EvaluacionSstCompleta ?? false,
                    PuedeIniciarEvaluacion = puedeIniciarOContinuar,
                    PuedeSolicitarActivacion = !puedeIniciarOContinuar && ventanaActiva == null,
                    TieneEvaluacionActiva = tieneEvaluacionActiva,
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

        [HttpGet]
        public async Task<IActionResult> Foto(Guid id)
        {
            var evaluador = await GetEvaluadorActualAsync();
            if (evaluador == null)
            {
                return Forbid();
            }

            var usuario = await _repo.GetUsuarioByIdAsync(id);
            if (usuario == null)
            {
                SetNoCacheHeaders();
                return File(BuildPlaceholderPhotoSvg("SF"), "image/svg+xml");
            }

            if (!PuedeAccederAUsuario(evaluador, usuario))
            {
                return Forbid();
            }

            SetNoCacheHeaders();

            var foto = await _repo.DownloadFotoUsuarioAsync(id);
            if (foto == null || foto.Contenido.Length == 0)
            {
                return File(BuildPlaceholderPhotoSvg(GetUserInitials(usuario.NombreCompleto)), "image/svg+xml");
            }

            var contentType = string.IsNullOrWhiteSpace(foto.TipoContenido)
                ? "application/octet-stream"
                : foto.TipoContenido;

            return File(foto.Contenido, contentType);
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

            var ventanaActiva = EvaluacionCicloHelper.ResolveVentanaActiva(usuario);
            var parteActual = EvaluacionRolesHelper.ResolveParte(
                usuario,
                GetCorreoActual(evaluador),
                evaluador.EsSuperAdministrador);
            var evaluaciones = await _repo.GetEvaluacionesByUsuarioAsync(usuario.Id);
            var evaluacionGuardadaEnVentana = ventanaActiva == null
                ? null
                : await SelectPreferredEvaluacionAsync(
                    evaluaciones.Where(x => EvaluacionCicloHelper.PerteneceAVentanaInicial(x, ventanaActiva)));
            var competencias = await _repo.GetCompetenciasAsync();
            var comportamientosPorNivel = new Dictionary<Guid, List<Comportamiento>>();
            var coberturasPorEvaluacion = new Dictionary<Guid, EvaluacionCoberturaInfo>();
            Evaluacion? evaluacionPendienteParaParte = null;

            foreach (var evaluacion in evaluaciones
                         .Where(EsEvaluacionInicial)
                         .OrderByDescending(x => x.FechaEvaluacion))
            {
                var cobertura = await GetCoberturaAsync(
                    evaluacion,
                    competencias,
                    comportamientosPorNivel,
                    coberturasPorEvaluacion);

                if (!EstaParteCompleta(cobertura, parteActual))
                {
                    evaluacionPendienteParaParte = evaluacion;
                    break;
                }
            }

            var tieneEvaluacionActiva = evaluacionPendienteParaParte != null;
            var puedeCrearNueva = ventanaActiva != null && evaluacionGuardadaEnVentana == null;
            var puedeIniciar = tieneEvaluacionActiva || puedeCrearNueva;

            return Json(new
            {
                puedeIniciar,
                puedeSolicitar = !puedeIniciar && ventanaActiva == null,
                tieneEvaluacionActiva,
                evaluacionActualId = evaluacionPendienteParaParte?.Id,
                fechaActivacionEvaluacion = usuario.FechaActivacionEvaluacion?.ToString("yyyy-MM-dd")
            });
        }

        private void SetNoCacheHeaders()
        {
            Response.Headers["Cache-Control"] = "no-store, no-cache, max-age=0";
            Response.Headers["Pragma"] = "no-cache";
            Response.Headers["Expires"] = "0";
        }

        private static byte[] BuildPlaceholderPhotoSvg(string initials)
        {
            var safeInitials = WebUtility.HtmlEncode(string.IsNullOrWhiteSpace(initials) ? "SF" : initials);
            var svg = $"""
<svg xmlns="http://www.w3.org/2000/svg" width="240" height="240" viewBox="0 0 240 240" role="img" aria-label="Foto de usuario no disponible">
  <rect width="240" height="240" rx="28" fill="#e9ecef" />
  <circle cx="120" cy="92" r="42" fill="#c6d0da" />
  <path d="M48 208c10-42 40-64 72-64s62 22 72 64" fill="#c6d0da" />
  <text x="120" y="222" text-anchor="middle" font-family="Arial, sans-serif" font-size="30" font-weight="700" fill="#5c6773">{safeInitials}</text>
</svg>
""";

            return Encoding.UTF8.GetBytes(svg);
        }

        private static string GetUserInitials(string? fullName)
        {
            if (string.IsNullOrWhiteSpace(fullName))
            {
                return "SF";
            }

            var initials = string.Concat(
                fullName
                    .Split(' ', StringSplitOptions.RemoveEmptyEntries)
                    .Take(2)
                    .Select(part => char.ToUpperInvariant(part[0])));

            return string.IsNullOrWhiteSpace(initials) ? "SF" : initials;
        }
    }
}
