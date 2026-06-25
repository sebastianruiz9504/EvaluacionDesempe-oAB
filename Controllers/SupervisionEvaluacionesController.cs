using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net;
using System.Net.Http;
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
    public class SupervisionEvaluacionesController : Controller
    {
        private const string UsuarioAutorizadoCorreo = "jully.pinto@aguasdebogota.com.co";
        private readonly IEvaluacionRepository _repo;
        private readonly IConfiguration _configuration;
        private readonly IHttpClientFactory _httpClientFactory;
        private readonly ILogger<SupervisionEvaluacionesController> _logger;

        private sealed class EvaluacionCoberturaInfo
        {
            public bool EvaluacionNormalCompleta { get; init; }
            public bool EvaluacionSstCompleta { get; init; }
            public bool AmbasPartesCompletas => EvaluacionNormalCompleta && EvaluacionSstCompleta;
        }

        private sealed class EvaluadorAsignacion
        {
            public string Key { get; init; } = string.Empty;
            public string Nombre { get; init; } = string.Empty;
            public string? Correo { get; init; }
            public Guid UsuarioId { get; init; }
            public TipoParteEvaluacion Parte { get; init; }
        }

        public SupervisionEvaluacionesController(
            IEvaluacionRepository repo,
            IConfiguration configuration,
            IHttpClientFactory httpClientFactory,
            ILogger<SupervisionEvaluacionesController> logger)
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

        private async Task<bool> PuedeAccederSupervisionAsync()
        {
            var correoSesion = GetUserEmail();
            if (EsUsuarioAutorizado(correoSesion))
            {
                return true;
            }

            if (string.IsNullOrWhiteSpace(correoSesion))
            {
                return false;
            }

            var usuario = await _repo.GetUsuarioByCorreoAsync(correoSesion);
            return EsUsuarioAutorizado(usuario?.CorreoElectronico);
        }

        private static bool EsUsuarioAutorizado(string? correo)
            => !string.IsNullOrWhiteSpace(correo) &&
               string.Equals(correo.Trim(), UsuarioAutorizadoCorreo, StringComparison.OrdinalIgnoreCase);

        public async Task<IActionResult> Index()
        {
            if (!await PuedeAccederSupervisionAsync())
            {
                return Forbid();
            }

            var model = await BuildSupervisionAsync();
            return View(model);
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> EnviarRecordatorio(string evaluadorKey)
        {
            if (!await PuedeAccederSupervisionAsync())
            {
                return Forbid();
            }

            if (string.IsNullOrWhiteSpace(evaluadorKey))
            {
                TempData["SupervisionError"] = "Selecciona un evaluador valido.";
                return RedirectToAction(nameof(Index));
            }

            var model = await BuildSupervisionAsync();
            var evaluador = model.Evaluadores.FirstOrDefault(x =>
                string.Equals(x.EvaluadorKey, evaluadorKey, StringComparison.Ordinal));

            if (evaluador == null)
            {
                TempData["SupervisionError"] = "No se encontro el evaluador seleccionado.";
                return RedirectToAction(nameof(Index));
            }

            if (string.IsNullOrWhiteSpace(evaluador.EvaluadorCorreo))
            {
                TempData["SupervisionError"] = $"No se puede enviar recordatorio a {evaluador.EvaluadorNombre} porque no tiene correo asociado.";
                return RedirectToAction(nameof(Index));
            }

            var flowUrl = _configuration["PowerAutomate:RecordatorioEvaluacionesUrl"]
                          ?? _configuration["PowerAutomate:EnviarRecordatorioEvaluacionesUrl"];
            if (string.IsNullOrWhiteSpace(flowUrl))
            {
                TempData["SupervisionError"] = "No se encontro la URL del flujo de Power Automate para recordatorios.";
                return RedirectToAction(nameof(Index));
            }

            var appUrl = BuildAppUrl();
            var asunto = "Recordatorio de evaluaciones pendientes";
            var payload = new
            {
                destinatarioCorreo = evaluador.EvaluadorCorreo,
                destinatarioNombre = evaluador.EvaluadorNombre,
                asunto,
                appUrl,
                detalle = new
                {
                    evaluador = evaluador.EvaluadorNombre,
                    correo = evaluador.EvaluadorCorreo,
                    cantidadUsuariosAsignados = evaluador.CantidadUsuariosAsignados,
                    cantidadUsuariosEnVentanaActiva = evaluador.CantidadUsuariosEnVentanaActiva,
                    cantidadEvaluacionesRealizadas = evaluador.CantidadEvaluacionesRealizadas,
                    evaluacionesPendientesEnVentanaActiva = evaluador.EvaluacionesPendientesEnVentanaActiva
                },
                cuerpoTexto = BuildRecordatorioTexto(evaluador, appUrl),
                cuerpoHtml = BuildRecordatorioHtml(evaluador, appUrl)
            };

            try
            {
                var client = _httpClientFactory.CreateClient();
                var response = await client.PostAsJsonAsync(flowUrl, payload);
                var responseContent = await response.Content.ReadAsStringAsync();

                if (!response.IsSuccessStatusCode)
                {
                    _logger.LogWarning(
                        "Power Automate respondio con error al enviar recordatorio. Status: {StatusCode}. Body: {ResponseBody}",
                        (int)response.StatusCode,
                        responseContent);

                    TempData["SupervisionError"] = string.IsNullOrWhiteSpace(responseContent)
                        ? "No fue posible enviar el recordatorio."
                        : responseContent;
                    return RedirectToAction(nameof(Index));
                }

                TempData["SupervisionSuccess"] = $"Recordatorio enviado a {evaluador.EvaluadorNombre}.";
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "No fue posible enviar recordatorio al evaluador {EvaluadorCorreo}.", evaluador.EvaluadorCorreo);
                TempData["SupervisionError"] = $"No fue posible enviar el recordatorio: {ex.Message}";
            }

            return RedirectToAction(nameof(Index));
        }

        private async Task<SupervisionEvaluacionesViewModel> BuildSupervisionAsync()
        {
            await _repo.HabilitarUsuariosProgramadosAsync(DateTime.Today);

            var usuarios = await _repo.GetUsuariosAsync();
            var usuariosPorId = usuarios
                .Where(x => x.Id != Guid.Empty)
                .GroupBy(x => x.Id)
                .ToDictionary(g => g.Key, g => g.First());
            var usuariosPorCorreo = usuarios
                .Where(x => !string.IsNullOrWhiteSpace(x.CorreoElectronico))
                .GroupBy(x => NormalizarCorreo(x.CorreoElectronico!), StringComparer.OrdinalIgnoreCase)
                .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);

            var asignaciones = usuarios
                .SelectMany(usuario => BuildAsignaciones(usuario, usuariosPorCorreo))
                .GroupBy(x => new { x.Key, x.UsuarioId })
                .Select(g => new EvaluadorAsignacion
                {
                    Key = g.Key.Key,
                    UsuarioId = g.Key.UsuarioId,
                    Nombre = g.Select(x => x.Nombre).FirstOrDefault(x => !string.IsNullOrWhiteSpace(x)) ?? g.Key.Key,
                    Correo = g.Select(x => x.Correo).FirstOrDefault(x => !string.IsNullOrWhiteSpace(x)),
                    Parte = g.Aggregate(TipoParteEvaluacion.Ninguna, (partes, item) => partes | item.Parte)
                })
                .Where(x => x.UsuarioId != Guid.Empty && EvaluacionRolesHelper.TieneAcceso(x.Parte))
                .ToList();

            var evaluacionesIniciales = (await _repo.GetEvaluacionesAsync())
                .Where(EsEvaluacionInicial)
                .Where(x => x.UsuarioId != Guid.Empty && x.NivelId != Guid.Empty)
                .ToList();

            var coberturaPorEvaluacion = await BuildCoberturasAsync(evaluacionesIniciales);
            var evaluacionInicialIncompletaPorUsuario = evaluacionesIniciales
                .Where(x => coberturaPorEvaluacion.TryGetValue(x.Id, out var cobertura) && !cobertura.AmbasPartesCompletas)
                .OrderByDescending(x => x.FechaEvaluacion)
                .GroupBy(x => x.UsuarioId)
                .ToDictionary(g => g.Key, g => g.First());

            var evaluadores = new Dictionary<string, SupervisionEvaluadorViewModel>(StringComparer.OrdinalIgnoreCase);

            foreach (var asignacion in asignaciones)
            {
                if (!usuariosPorId.TryGetValue(asignacion.UsuarioId, out var usuario))
                {
                    continue;
                }

                if (!evaluadores.TryGetValue(asignacion.Key, out var evaluador))
                {
                    evaluador = new SupervisionEvaluadorViewModel
                    {
                        EvaluadorKey = asignacion.Key,
                        EvaluadorNombre = asignacion.Nombre,
                        EvaluadorCorreo = asignacion.Correo
                    };
                    evaluadores[asignacion.Key] = evaluador;
                }
                else
                {
                    if (string.IsNullOrWhiteSpace(evaluador.EvaluadorCorreo) && !string.IsNullOrWhiteSpace(asignacion.Correo))
                    {
                        evaluador.EvaluadorCorreo = asignacion.Correo;
                    }

                    if (string.Equals(evaluador.EvaluadorNombre, evaluador.EvaluadorKey, StringComparison.OrdinalIgnoreCase) &&
                        !string.IsNullOrWhiteSpace(asignacion.Nombre))
                    {
                        evaluador.EvaluadorNombre = asignacion.Nombre;
                    }
                }

                evaluador.CantidadUsuariosAsignados++;

                var tieneEvaluacionIncompleta = evaluacionInicialIncompletaPorUsuario.TryGetValue(usuario.Id, out var evaluacionIncompleta);
                var estaEnVentanaActiva = usuario.Habilitado || tieneEvaluacionIncompleta;

                if (!estaEnVentanaActiva)
                {
                    continue;
                }

                evaluador.CantidadUsuariosEnVentanaActiva++;

                var realizada = tieneEvaluacionIncompleta &&
                                evaluacionIncompleta != null &&
                                coberturaPorEvaluacion.TryGetValue(evaluacionIncompleta.Id, out var cobertura) &&
                                EstaParteCompleta(cobertura, asignacion.Parte);

                if (realizada)
                {
                    evaluador.CantidadEvaluacionesRealizadas++;
                }
                else
                {
                    evaluador.EvaluacionesPendientesEnVentanaActiva++;
                }
            }

            return new SupervisionEvaluacionesViewModel
            {
                Evaluadores = evaluadores.Values
                    .OrderByDescending(x => x.EvaluacionesPendientesEnVentanaActiva)
                    .ThenByDescending(x => x.CantidadUsuariosEnVentanaActiva)
                    .ThenBy(x => x.EvaluadorNombre)
                    .ToList()
            };
        }

        private async Task<Dictionary<Guid, EvaluacionCoberturaInfo>> BuildCoberturasAsync(IReadOnlyCollection<Evaluacion> evaluaciones)
        {
            var coberturas = new Dictionary<Guid, EvaluacionCoberturaInfo>();
            if (evaluaciones.Count == 0)
            {
                return coberturas;
            }

            var competencias = await _repo.GetCompetenciasAsync();
            var comportamientosPorNivel = (await _repo.GetComportamientosByNivelesAsync(evaluaciones.Select(x => x.NivelId)))
                .Where(x => x.NivelId != Guid.Empty)
                .GroupBy(x => x.NivelId)
                .ToDictionary(g => g.Key, g => (IReadOnlyCollection<Comportamiento>)g.ToList());
            var detallesPorEvaluacion = (await _repo.GetDetallesByEvaluacionesAsync(evaluaciones.Select(x => x.Id)))
                .Where(x => x.EvaluacionId != Guid.Empty)
                .GroupBy(x => x.EvaluacionId)
                .ToDictionary(g => g.Key, g => (IReadOnlyCollection<EvaluacionDetalle>)g.ToList());

            foreach (var evaluacion in evaluaciones)
            {
                comportamientosPorNivel.TryGetValue(evaluacion.NivelId, out var comportamientos);
                detallesPorEvaluacion.TryGetValue(evaluacion.Id, out var detalles);

                coberturas[evaluacion.Id] = BuildCobertura(
                    detalles ?? Array.Empty<EvaluacionDetalle>(),
                    competencias,
                    comportamientos ?? Array.Empty<Comportamiento>());
            }

            return coberturas;
        }

        private static IEnumerable<EvaluadorAsignacion> BuildAsignaciones(
            UsuarioEvaluado usuario,
            IReadOnlyDictionary<string, UsuarioEvaluado> usuariosPorCorreo)
        {
            var normal = BuildAsignacion(
                usuario,
                TipoParteEvaluacion.Normal,
                new[] { usuario.CorreoEvaluador, usuario.EvaluadorNombre },
                new[] { usuario.CorreoEvaluador, usuario.EvaluadorNombre },
                usuariosPorCorreo);

            if (normal != null)
            {
                yield return normal;
            }

            var sst = BuildAsignacion(
                usuario,
                TipoParteEvaluacion.Sst,
                new[] { usuario.CorreoEvaluadorSst, usuario.NombreEvaluadorSst },
                new[] { usuario.NombreEvaluadorSst, usuario.CorreoEvaluadorSst },
                usuariosPorCorreo);

            if (sst != null)
            {
                yield return sst;
            }
        }

        private static EvaluadorAsignacion? BuildAsignacion(
            UsuarioEvaluado usuario,
            TipoParteEvaluacion parte,
            IEnumerable<string?> correoCandidates,
            IEnumerable<string?> nombreCandidates,
            IReadOnlyDictionary<string, UsuarioEvaluado> usuariosPorCorreo)
        {
            var correo = correoCandidates
                .Select(Limpiar)
                .FirstOrDefault(LuceComoCorreo);
            var nombre = nombreCandidates
                .Select(Limpiar)
                .FirstOrDefault(value => !string.IsNullOrWhiteSpace(value) && !LuceComoCorreo(value));

            if (!string.IsNullOrWhiteSpace(correo) &&
                usuariosPorCorreo.TryGetValue(NormalizarCorreo(correo), out var usuarioEvaluador) &&
                !string.IsNullOrWhiteSpace(usuarioEvaluador.NombreCompleto))
            {
                nombre = usuarioEvaluador.NombreCompleto;
            }

            nombre ??= correoCandidates
                .Concat(nombreCandidates)
                .Select(Limpiar)
                .FirstOrDefault(value => !string.IsNullOrWhiteSpace(value));

            if (string.IsNullOrWhiteSpace(correo) && string.IsNullOrWhiteSpace(nombre))
            {
                return null;
            }

            var key = !string.IsNullOrWhiteSpace(correo)
                ? NormalizarCorreo(correo)
                : NormalizarTextoClave(nombre!);

            return new EvaluadorAsignacion
            {
                Key = key,
                Nombre = nombre ?? correo ?? key,
                Correo = correo,
                UsuarioId = usuario.Id,
                Parte = parte
            };
        }

        private static EvaluacionCoberturaInfo BuildCobertura(
            IReadOnlyCollection<EvaluacionDetalle> detalles,
            IReadOnlyCollection<Competencia> competencias,
            IReadOnlyCollection<Comportamiento> comportamientos)
        {
            var competenciasById = competencias
                .GroupBy(c => c.Id)
                .ToDictionary(g => g.Key, g => g.First());
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

            return new EvaluacionCoberturaInfo
            {
                EvaluacionNormalCompleta = comportamientosNormales.Count == 0 || comportamientosNormales.All(respondidos.Contains),
                EvaluacionSstCompleta = comportamientosSst.Count == 0 || comportamientosSst.All(respondidos.Contains)
            };
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

        private static bool EsEvaluacionInicial(Evaluacion evaluacion)
            => evaluacion.Id != Guid.Empty &&
               !evaluacion.EvaluacionOrigenId.HasValue &&
               string.Equals(evaluacion.TipoEvaluacion, "Inicial", StringComparison.OrdinalIgnoreCase);

        private string BuildAppUrl()
        {
            var usuariosUrl = Url.Action("Index", "Usuarios", values: null, protocol: Request.Scheme);
            if (!string.IsNullOrWhiteSpace(usuariosUrl))
            {
                return usuariosUrl;
            }

            return $"{Request.Scheme}://{Request.Host}/Usuarios";
        }

        private static string BuildRecordatorioTexto(SupervisionEvaluadorViewModel evaluador, string appUrl)
        {
            var builder = new StringBuilder();
            builder.AppendLine($"Hola {evaluador.EvaluadorNombre},");
            builder.AppendLine();
            builder.AppendLine("Tienes evaluaciones pendientes por realizar.");
            builder.AppendLine();
            builder.AppendLine($"Evaluador: {evaluador.EvaluadorNombre}");
            builder.AppendLine($"Cantidad usuarios asignados: {evaluador.CantidadUsuariosAsignados}");
            builder.AppendLine($"Cantidad usuarios en ventana activa de evaluacion: {evaluador.CantidadUsuariosEnVentanaActiva}");
            builder.AppendLine($"Cantidad de evaluaciones realizadas: {evaluador.CantidadEvaluacionesRealizadas}");
            builder.AppendLine($"Evaluaciones pendientes en ventana activa por realizar: {evaluador.EvaluacionesPendientesEnVentanaActiva}");
            builder.AppendLine();
            builder.AppendLine($"Ingresa a la app para realizar tus evaluaciones: {appUrl}");
            return builder.ToString();
        }

        private static string BuildRecordatorioHtml(SupervisionEvaluadorViewModel evaluador, string appUrl)
        {
            static string Encode(string? value) => WebUtility.HtmlEncode(value ?? string.Empty);

            var filas = new[]
            {
                ("Evaluador", evaluador.EvaluadorNombre),
                ("Cantidad usuarios asignados", evaluador.CantidadUsuariosAsignados.ToString(CultureInfo.InvariantCulture)),
                ("Cantidad usuarios en ventana activa de evaluacion", evaluador.CantidadUsuariosEnVentanaActiva.ToString(CultureInfo.InvariantCulture)),
                ("Cantidad de evaluaciones realizadas", evaluador.CantidadEvaluacionesRealizadas.ToString(CultureInfo.InvariantCulture)),
                ("Evaluaciones pendientes en ventana activa por realizar", evaluador.EvaluacionesPendientesEnVentanaActiva.ToString(CultureInfo.InvariantCulture))
            };

            var builder = new StringBuilder();
            builder.Append("<p>Hola ");
            builder.Append(Encode(evaluador.EvaluadorNombre));
            builder.Append(",</p>");
            builder.Append("<p>Tienes evaluaciones pendientes por realizar.</p>");
            builder.Append("<table style=\"border-collapse:collapse;width:100%;max-width:640px;\">");

            foreach (var (label, value) in filas)
            {
                builder.Append("<tr>");
                builder.Append("<th style=\"text-align:left;border:1px solid #d0d7de;padding:8px;background:#f6f8fa;\">");
                builder.Append(Encode(label));
                builder.Append("</th>");
                builder.Append("<td style=\"border:1px solid #d0d7de;padding:8px;\">");
                builder.Append(Encode(value));
                builder.Append("</td>");
                builder.Append("</tr>");
            }

            builder.Append("</table>");
            builder.Append("<p><a href=\"");
            builder.Append(Encode(appUrl));
            builder.Append("\">Ingresar a la app para realizar las evaluaciones</a></p>");
            return builder.ToString();
        }

        private static string? Limpiar(string? value)
            => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

        private static bool LuceComoCorreo(string? value)
            => !string.IsNullOrWhiteSpace(value) &&
               value.Contains('@', StringComparison.Ordinal) &&
               value.IndexOf('@') > 0;

        private static string NormalizarCorreo(string correo)
            => correo.Trim().ToLowerInvariant();

        private static string NormalizarTextoClave(string value)
        {
            var normalized = value.Trim().Normalize(NormalizationForm.FormD);
            var builder = new StringBuilder(normalized.Length);

            foreach (var ch in normalized)
            {
                if (CharUnicodeInfo.GetUnicodeCategory(ch) != UnicodeCategory.NonSpacingMark)
                {
                    builder.Append(char.ToLowerInvariant(ch));
                }
            }

            return string.Join(
                " ",
                builder
                    .ToString()
                    .Normalize(NormalizationForm.FormC)
                    .Split(' ', StringSplitOptions.RemoveEmptyEntries));
        }
    }
}
