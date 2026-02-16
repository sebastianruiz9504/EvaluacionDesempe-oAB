using System;
using System.Linq;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Threading.Tasks;
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

        // ================== HELPERS ==================

        private string? GetUserEmail()
        {
            // Distintos claims posibles según Azure AD
            return User.FindFirst("preferred_username")?.Value
                   ?? User.FindFirst(ClaimTypes.Email)?.Value
                   ?? User.FindFirst(ClaimTypes.Upn)?.Value;
        }

        private async Task<UsuarioEvaluado?> GetEvaluadorActualAsync()
        {
            var email = GetUserEmail();
            if (string.IsNullOrWhiteSpace(email))
                return null;

            return await _repo.GetUsuarioByCorreoAsync(email);
        }

        // ================== ACCIONES ==================

        // Lista de usuarios asignados al evaluador actual
        public async Task<IActionResult> Index(string? cedula)
        {
            var evaluador = await GetEvaluadorActualAsync();
            if (evaluador == null)
            {
                // El usuario autenticado no está en la tabla crfb7_usuario → no es evaluador
                return Forbid();
            }

            // Filtramos por el CORREO del evaluador, tal como está en crfb7_evaluadorid
            var usuarios = await _repo.GetUsuariosByEvaluadorAsync(evaluador.CorreoElectronico ?? GetUserEmail() ?? string.Empty);
            if (!string.IsNullOrWhiteSpace(cedula))
            {
                usuarios = usuarios
                    .Where(usuario => usuario.Cedula?.Contains(cedula, StringComparison.OrdinalIgnoreCase) == true)
                    .ToList();
            }

            ViewData["CedulaFiltro"] = cedula;

            return View(usuarios);
        }

        // Detalle de un usuario (carpeta / historial)
        [HttpGet]
        public async Task<IActionResult> Detalle(Guid id)
        {
            var evaluador = await GetEvaluadorActualAsync();
            if (evaluador == null)
                return Forbid();

            var usuario = await _repo.GetUsuarioByIdAsync(id);
            if (usuario == null)
                return NotFound();

            var evals = await _repo.GetEvaluacionesByUsuarioAsync(id);

            var nivelesDict = new System.Collections.Generic.Dictionary<Guid, NivelEvaluacion>();
            var lista = new System.Collections.Generic.List<EvaluacionListaViewModel>();

            foreach (var e in evals)
            {
                if (!nivelesDict.TryGetValue(e.NivelId, out var nivel))
                {
                    nivel = await _repo.GetNivelByIdAsync(e.NivelId) ?? new NivelEvaluacion();
                    nivelesDict[e.NivelId] = nivel;
                }

                lista.Add(new EvaluacionListaViewModel
                {
                    Id = e.Id,
                    UsuarioId = e.UsuarioId,
                    FechaEvaluacion = e.FechaEvaluacion,
                    ProximaEvaluacion = e.FechaProximaEvaluacion,
                    NombreUsuario = usuario.NombreCompleto,
                    CedulaUsuario = usuario.Cedula,
                    NivelNombre = nivel.Nombre,
                    NivelCodigo = nivel.Codigo,
                    TipoEvaluacion = e.TipoEvaluacion,
                    PuedeReevaluar = e.TipoEvaluacion == "Inicial"
                });
            }

            var vm = new CarpetaUsuarioViewModel
            {
                UsuarioId = usuario.Id,
                NombreUsuario = usuario.NombreCompleto,
                CedulaUsuario = usuario.Cedula,
                Cargo = usuario.Cargo,
                Evaluaciones = lista.OrderByDescending(x => x.FechaEvaluacion).ToList()
            };

            return View(vm);
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
                evaluadorCorreo = Safe(evaluador.CorreoElectronico ?? GetUserEmail()),
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
                return StatusCode((int)response.StatusCode,
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
    }
}
