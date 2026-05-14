using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Claims;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using ClosedXML.Excel;
using EvaluacionDesempenoAB.Helpers;
using EvaluacionDesempenoAB.Models;
using EvaluacionDesempenoAB.Services;
using EvaluacionDesempenoAB.ViewModels;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;

namespace EvaluacionDesempenoAB.Controllers
{
    [Authorize]
    public class EvaluacionesController : Controller
    {
        private readonly IEvaluacionRepository _repo;
        private readonly ILogger<EvaluacionesController> _logger;
        private static readonly ConcurrentDictionary<string, SemaphoreSlim> InicioEvaluacionLocks = new(StringComparer.OrdinalIgnoreCase);

        private static readonly Dictionary<int, (string Codigo, string Nombre)> TipoFormularioNiveles = new()
        {
            { 433930001, ("OPEADM", "Operativo Administrativo") },
            { 433930000, ("TACT", "Táctico") },
            { 433930003, ("ESTR", "Estratégico") },
            { 433930002, ("OPE", "Operativo") }
        };

        private static readonly string[] PlantillaImportacionUsuariosColumnas =
        {
            "Cedula",
            "Nombre completo",
            "Cargo",
            "Nombre evaluador principal",
            "Cargo evaluador principal",
            "Correo evaluador principal",
            "Nombre evaluador SST",
            "Correo evaluador SST",
            "Cargo evaluador SST",
            "Fecha ingreso",
            "Fecha finalización contrato",
            "Fecha Finalizacion Periodo de Prueba",
            "Fecha activacion programada",
            "Gerencia",
            "Proyecto",
            "Tipo de formulario"
        };

        private sealed class EvaluacionCoberturaInfo
        {
            public bool EvaluacionNormalCompleta { get; init; }
            public bool EvaluacionSstCompleta { get; init; }
            public decimal? TotalCalculado { get; init; }
            public decimal? TotalEvaluacionNormalCalculado { get; init; }
            public decimal? TotalEvaluacionSstCalculado { get; init; }
            public bool AmbasPartesCompletas => EvaluacionNormalCompleta && EvaluacionSstCompleta;
        }

        public sealed class ExportarExcelRequest
        {
            public List<Guid> Ids { get; init; } = new();
        }

        public sealed class EliminarEvaluacionesRequest
        {
            public List<Guid> Ids { get; init; } = new();
        }

        private const int OportunidadMejoraPuntajeMinimo = 70;
        private const int OportunidadMejoraPuntajeMaximo = 85;
        private const string DeleteAuthorizedEmail = "jully.pinto@aguasdebogota.com.co";

        public EvaluacionesController(IEvaluacionRepository repo, ILogger<EvaluacionesController> logger)
        {
            _repo = repo;
            _logger = logger;
        }

        private string? GetUserEmail()
        {
            return User.FindFirst("preferred_username")?.Value
                   ?? User.FindFirst(ClaimTypes.Email)?.Value
                   ?? User.FindFirst(ClaimTypes.Upn)?.Value;
        }

        private static bool EsUsuarioAutorizadoParaEliminar(string? correo)
            => !string.IsNullOrWhiteSpace(correo) &&
               string.Equals(
                   correo.Trim(),
                   DeleteAuthorizedEmail,
                   StringComparison.OrdinalIgnoreCase);

        private bool PuedeEliminarEvaluaciones(UsuarioEvaluado? evaluador = null)
        {
            if (!_repo.IsDataverseBacked)
            {
                return false;
            }

            return EsUsuarioAutorizadoParaEliminar(evaluador?.CorreoElectronico) ||
                   EsUsuarioAutorizadoParaEliminar(GetUserEmail());
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

        private async Task<UsuarioEvaluado?> ResolveUsuarioPorCorreosAsync(params string?[] posiblesCorreos)
        {
            foreach (var correo in posiblesCorreos)
            {
                if (string.IsNullOrWhiteSpace(correo) || !correo.Contains('@'))
                {
                    continue;
                }

                var usuario = await _repo.GetUsuarioByCorreoAsync(correo.Trim());
                if (usuario != null)
                {
                    return usuario;
                }
            }

            return null;
        }

        private static string? ConvertirArchivoADataUrl(ArchivoEvaluacion? archivo)
        {
            if (archivo == null || archivo.Contenido.Length == 0)
            {
                return null;
            }

            var tipoContenido = DetectarTipoContenidoFirma(archivo.Contenido);
            if (string.IsNullOrWhiteSpace(tipoContenido))
            {
                return null;
            }

            return $"data:{tipoContenido};base64,{Convert.ToBase64String(archivo.Contenido)}";
        }

        private static bool TieneFirmaValida(ArchivoEvaluacion? archivo)
            => archivo != null && !string.IsNullOrWhiteSpace(DetectarTipoContenidoFirma(archivo.Contenido));

        private async Task<ArchivoEvaluacion?> DownloadFirmaUsuarioOrNullAsync(Guid usuarioId, string? contexto = null)
        {
            try
            {
                return await _repo.DownloadFirmaUsuarioAsync(usuarioId);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(
                    ex,
                    "No fue posible cargar la firma del usuario {UsuarioId}. Contexto: {Contexto}. Se continuará sin mostrar firma.",
                    usuarioId,
                    contexto ?? "sin contexto");
                return null;
            }
        }

        private static bool EsPuntajeOportunidadMejora(int? puntaje)
            => puntaje.HasValue &&
               puntaje.Value >= OportunidadMejoraPuntajeMinimo &&
               puntaje.Value <= OportunidadMejoraPuntajeMaximo;

        private static bool EsFirmaImagenValida(IFormFile archivo)
            => !string.IsNullOrWhiteSpace(GetTipoContenidoFirma(archivo));

        private static string? GetTipoContenidoFirma(IFormFile archivo)
        {
            if (archivo.Length == 0)
            {
                return null;
            }

            using var stream = archivo.OpenReadStream();
            Span<byte> buffer = stackalloc byte[8];
            var bytesRead = stream.Read(buffer);
            var tipoContenido = DetectarTipoContenidoFirma(buffer[..bytesRead]);
            if (!string.IsNullOrWhiteSpace(tipoContenido))
            {
                return tipoContenido;
            }

            return null;
        }

        private static string? DetectarTipoContenidoFirma(ReadOnlySpan<byte> bytes)
        {
            if (bytes.Length >= 8 &&
                bytes[0] == 0x89 &&
                bytes[1] == 0x50 &&
                bytes[2] == 0x4E &&
                bytes[3] == 0x47 &&
                bytes[4] == 0x0D &&
                bytes[5] == 0x0A &&
                bytes[6] == 0x1A &&
                bytes[7] == 0x0A)
            {
                return "image/png";
            }

            if (bytes.Length >= 3 &&
                bytes[0] == 0xFF &&
                bytes[1] == 0xD8 &&
                bytes[2] == 0xFF)
            {
                return "image/jpeg";
            }

            return null;
        }

        private static string? GetEtiquetaFirmaParaParte(TipoParteEvaluacion parte)
        {
            if (parte == (TipoParteEvaluacion.Normal | TipoParteEvaluacion.Sst))
            {
                return "Evaluador y evaluador SST";
            }

            if (parte == TipoParteEvaluacion.Normal)
            {
                return "Evaluador";
            }

            if (parte == TipoParteEvaluacion.Sst)
            {
                return "Evaluador SST";
            }

            return null;
        }

        private async Task<IActionResult> ReturnReporteConErrorGuardadoPlanAsync(
            EvaluacionReporteViewModel model,
            UsuarioEvaluado evaluadorActual,
            string mensajeError)
        {
            var vm = await BuildReporteViewModelAsync(model.EvaluacionId, evaluadorActual);
            if (vm == null)
            {
                return NotFound();
            }

            vm.PlanAccion = model.PlanAccion?.Any() == true
                ? model.PlanAccion
                : new List<PlanAccionItemVm> { new() };
            vm.FechaProximaEvaluacion = model.FechaProximaEvaluacion;

            ViewBag.ModoPlanAccion = true;
            ViewBag.MostrarModalFirma = true;
            ViewBag.ErrorGuardadoPlan = mensajeError;

            return View("Reporte", vm);
        }

        private async Task<List<Evaluacion>> GetEvaluacionesVisiblesAsync(UsuarioEvaluado evaluador)
        {
            if (evaluador.EsSuperAdministrador)
            {
                return await _repo.GetEvaluacionesAsync();
            }

            return await _repo.GetEvaluacionesByEvaluadorAsync(GetCorreoActual(evaluador));
        }

        private TipoParteEvaluacion GetParteEvaluador(UsuarioEvaluado evaluadorActual, UsuarioEvaluado usuarioObjetivo)
            => EvaluacionRolesHelper.ResolveParte(
                usuarioObjetivo,
                GetCorreoActual(evaluadorActual));

        private bool PuedeAccederAUsuario(UsuarioEvaluado evaluadorActual, UsuarioEvaluado usuarioObjetivo)
            => evaluadorActual.EsSuperAdministrador || EvaluacionRolesHelper.TieneAcceso(GetParteEvaluador(evaluadorActual, usuarioObjetivo));

        private bool TieneRolDiligenciarUsuario(UsuarioEvaluado evaluadorActual, UsuarioEvaluado usuarioObjetivo)
            => EvaluacionRolesHelper.TieneAcceso(GetParteEvaluador(evaluadorActual, usuarioObjetivo));

        private bool PuedeDiligenciarUsuario(UsuarioEvaluado evaluadorActual, UsuarioEvaluado usuarioObjetivo)
            => usuarioObjetivo.Habilitado && TieneRolDiligenciarUsuario(evaluadorActual, usuarioObjetivo);

        private static BadRequestObjectResult EvaluadorSinBloqueAsignado()
            => new("Solo el evaluador asignado o el evaluador SST asignado pueden diligenciar esta evaluación.");

        private static BadRequestObjectResult UsuarioNoHabilitado()
            => new("Este usuario no está habilitado para evaluación en Dataverse.");

        private static string GetEtiquetaAlcance(TipoParteEvaluacion parte, bool esSuperAdministrador)
            => EvaluacionRolesHelper.TieneAcceso(parte)
                ? EvaluacionRolesHelper.GetEtiquetaParte(parte)
                : esSuperAdministrador
                    ? "Administrador (solo consulta)"
                    : EvaluacionRolesHelper.GetEtiquetaParte(parte);

        private async Task<bool> PuedeAccederAEvaluacionAsync(UsuarioEvaluado evaluadorActual, Evaluacion evaluacion)
        {
            if (evaluadorActual.EsSuperAdministrador)
            {
                return true;
            }

            var usuario = await _repo.GetUsuarioByIdAsync(evaluacion.UsuarioId);
            return usuario != null && PuedeAccederAUsuario(evaluadorActual, usuario);
        }

        private static NivelEvaluacion? ResolveNivelPorTipoFormulario(
            UsuarioEvaluado usuario,
            IEnumerable<NivelEvaluacion> niveles)
        {
            return usuario.TipoFormulario.HasValue
                ? ResolveNivelPorTipoFormulario(usuario.TipoFormulario.Value, niveles)
                : null;
        }

        private static NivelEvaluacion? ResolveNivelPorTipoFormulario(
            int tipoFormulario,
            IEnumerable<NivelEvaluacion> niveles)
        {
            if (!TipoFormularioNiveles.TryGetValue(tipoFormulario, out var nivelInfo))
            {
                return null;
            }

            return niveles.FirstOrDefault(n =>
                       string.Equals(n.Codigo, nivelInfo.Codigo, StringComparison.OrdinalIgnoreCase))
                   ?? niveles.FirstOrDefault(n =>
                       string.Equals(n.Nombre, nivelInfo.Nombre, StringComparison.OrdinalIgnoreCase));
        }

        private static List<TipoFormularioOpcionViewModel> BuildTipoFormularioOpciones()
            => TipoFormularioNiveles
                .Select(kvp => new TipoFormularioOpcionViewModel
                {
                    Valor = kvp.Key,
                    Nombre = kvp.Value.Nombre
                })
                .ToList();

        private static string? GetTipoFormularioNombre(int tipoFormulario)
            => TipoFormularioNiveles.TryGetValue(tipoFormulario, out var tipoInfo)
                ? tipoInfo.Nombre
                : null;

        private static Dictionary<Guid, Competencia> BuildCompetenciasLookup(IEnumerable<Competencia> competencias)
            => competencias
                .GroupBy(c => c.Id)
                .ToDictionary(g => g.Key, g => g.First());

        private static Dictionary<string, Comportamiento> BuildComportamientosPorDescripcion(IEnumerable<Comportamiento> comportamientos)
        {
            var dict = new Dictionary<string, Comportamiento>(StringComparer.OrdinalIgnoreCase);

            foreach (var comportamiento in comportamientos)
            {
                if (string.IsNullOrWhiteSpace(comportamiento.Descripcion) || dict.ContainsKey(comportamiento.Descripcion))
                {
                    continue;
                }

                dict[comportamiento.Descripcion] = comportamiento;
            }

            return dict;
        }

        private static HashSet<Guid> GetComportamientosPermitidos(
            TipoParteEvaluacion parte,
            IEnumerable<Comportamiento> comportamientos,
            IReadOnlyDictionary<Guid, Competencia> competenciasById)
        {
            if (parte == (TipoParteEvaluacion.Normal | TipoParteEvaluacion.Sst))
            {
                return comportamientos.Select(c => c.Id).ToHashSet();
            }

            return comportamientos
                .Where(c =>
                {
                    competenciasById.TryGetValue(c.CompetenciaId, out var competencia);
                    return EvaluacionRolesHelper.DebeVerCompetencia(parte, competencia?.Nombre);
                })
                .Select(c => c.Id)
                .ToHashSet();
        }

        private static List<Comportamiento> FilterComportamientosPermitidos(
            TipoParteEvaluacion parte,
            IEnumerable<Comportamiento> comportamientos,
            IReadOnlyDictionary<Guid, Competencia> competenciasById)
        {
            var permitidos = GetComportamientosPermitidos(parte, comportamientos, competenciasById);
            return comportamientos
                .Where(c => permitidos.Contains(c.Id))
                .OrderBy(c =>
                {
                    competenciasById.TryGetValue(c.CompetenciaId, out var competencia);
                    return competencia?.Orden ?? int.MaxValue;
                })
                .ThenBy(c => c.Orden)
                .ToList();
        }

        private static decimal? CalcularPromedioModulo(
            IReadOnlyDictionary<Guid, EvaluacionDetalle> detallesPorComportamiento,
            IReadOnlyCollection<Guid> comportamientosModulo,
            bool moduloCompleto)
        {
            if (!moduloCompleto || comportamientosModulo.Count == 0)
            {
                return null;
            }

            var puntajes = comportamientosModulo
                .Where(detallesPorComportamiento.ContainsKey)
                .Select(comportamientoId => (decimal)detallesPorComportamiento[comportamientoId].Puntaje)
                .ToList();

            return puntajes.Count == 0
                ? null
                : Math.Round(puntajes.Average(), 2);
        }

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

            var detallesPorComportamiento = detalles
                .Where(d => d.ComportamientoId != Guid.Empty)
                .GroupBy(d => d.ComportamientoId)
                .ToDictionary(g => g.Key, g => g.Last());
            var respondidos = detallesPorComportamiento.Keys.ToHashSet();

            var evaluacionNormalCompleta = comportamientosNormales.Count == 0 || comportamientosNormales.All(respondidos.Contains);
            var evaluacionSstCompleta = comportamientosSst.Count == 0 || comportamientosSst.All(respondidos.Contains);
            var totalEvaluacionNormalCalculado = CalcularPromedioModulo(
                detallesPorComportamiento,
                comportamientosNormales,
                evaluacionNormalCompleta);
            var totalEvaluacionSstCalculado = CalcularPromedioModulo(
                detallesPorComportamiento,
                comportamientosSst,
                evaluacionSstCompleta);

            decimal? totalCalculado = null;
            if (evaluacionNormalCompleta && evaluacionSstCompleta && detallesPorComportamiento.Count > 0)
            {
                totalCalculado = Math.Round(detallesPorComportamiento.Values.Average(d => (decimal)d.Puntaje), 2);
            }

            return new EvaluacionCoberturaInfo
            {
                EvaluacionNormalCompleta = evaluacionNormalCompleta,
                EvaluacionSstCompleta = evaluacionSstCompleta,
                TotalCalculado = totalCalculado,
                TotalEvaluacionNormalCalculado = totalEvaluacionNormalCalculado,
                TotalEvaluacionSstCalculado = totalEvaluacionSstCalculado
            };
        }

        private static bool EsEvaluacionInicial(Evaluacion evaluacion)
            => evaluacion.Id != Guid.Empty &&
               !evaluacion.EvaluacionOrigenId.HasValue &&
               string.Equals(evaluacion.TipoEvaluacion, "Inicial", StringComparison.OrdinalIgnoreCase);

        private async Task<EvaluacionCoberturaInfo> GetCoberturaAsync(
            Evaluacion evaluacion,
            IReadOnlyCollection<Competencia>? competencias = null,
            IDictionary<Guid, List<Comportamiento>>? comportamientosPorNivel = null,
            IDictionary<Guid, EvaluacionCoberturaInfo>? coberturasPorEvaluacion = null)
        {
            if (coberturasPorEvaluacion != null &&
                coberturasPorEvaluacion.TryGetValue(evaluacion.Id, out var coberturaCacheada))
            {
                return coberturaCacheada;
            }

            competencias ??= await _repo.GetCompetenciasAsync();
            comportamientosPorNivel ??= new Dictionary<Guid, List<Comportamiento>>();

            if (!comportamientosPorNivel.TryGetValue(evaluacion.NivelId, out var comportamientos))
            {
                comportamientos = await _repo.GetComportamientosByNivelAsync(evaluacion.NivelId);
                comportamientosPorNivel[evaluacion.NivelId] = comportamientos;
            }

            var detalles = await _repo.GetDetallesByEvaluacionAsync(evaluacion.Id);
            var cobertura = BuildCobertura(detalles, competencias, comportamientos);

            if (coberturasPorEvaluacion != null)
            {
                coberturasPorEvaluacion[evaluacion.Id] = cobertura;
            }

            return cobertura;
        }

        private async Task<Evaluacion?> GetEvaluacionInicialPendienteAsync(
            Guid usuarioId,
            IReadOnlyCollection<Competencia>? competencias = null,
            IDictionary<Guid, List<Comportamiento>>? comportamientosPorNivel = null,
            IDictionary<Guid, EvaluacionCoberturaInfo>? coberturasPorEvaluacion = null)
        {
            var evaluaciones = await _repo.GetEvaluacionesByUsuarioAsync(usuarioId);

            foreach (var evaluacion in evaluaciones
                         .Where(EsEvaluacionInicial)
                         .OrderByDescending(x => x.FechaEvaluacion))
            {
                var cobertura = await GetCoberturaAsync(
                    evaluacion,
                    competencias,
                    comportamientosPorNivel,
                    coberturasPorEvaluacion);

                if (!cobertura.AmbasPartesCompletas)
                {
                    return evaluacion;
                }
            }

            return null;
        }

        private static bool TieneFirmasCompletasParaCertificado(EvaluacionReporteViewModel vm)
            => !string.IsNullOrWhiteSpace(vm.FirmaEvaluadorDataUrl) &&
               !string.IsNullOrWhiteSpace(vm.FirmaEvaluadorSstDataUrl);

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

        private async Task<Evaluacion?> GetEvaluacionInicialPendienteParaParteAsync(
            Guid usuarioId,
            TipoParteEvaluacion parte,
            IReadOnlyCollection<Competencia>? competencias = null,
            IDictionary<Guid, List<Comportamiento>>? comportamientosPorNivel = null,
            IDictionary<Guid, EvaluacionCoberturaInfo>? coberturasPorEvaluacion = null)
        {
            var evaluaciones = await _repo.GetEvaluacionesByUsuarioAsync(usuarioId);

            foreach (var evaluacion in evaluaciones
                         .Where(EsEvaluacionInicial)
                         .OrderByDescending(x => x.FechaEvaluacion))
            {
                var cobertura = await GetCoberturaAsync(
                    evaluacion,
                    competencias,
                    comportamientosPorNivel,
                    coberturasPorEvaluacion);

                if (!EstaParteCompleta(cobertura, parte))
                {
                    return evaluacion;
                }
            }

            return null;
        }

        private static string? MergeObservaciones(string? existentes, string? nuevas)
        {
            if (string.IsNullOrWhiteSpace(nuevas))
            {
                return existentes;
            }

            if (string.IsNullOrWhiteSpace(existentes))
            {
                return nuevas;
            }

            return string.Equals(existentes.Trim(), nuevas.Trim(), StringComparison.Ordinal)
                ? existentes
                : $"{existentes}{Environment.NewLine}{Environment.NewLine}{nuevas}";
        }

        private void ValidarGuardadoUnicoPorParte(
            EvaluacionFormularioViewModel model,
            IReadOnlySet<Guid> comportamientosParteActual)
        {
            var puntajesPorComportamiento = model.Competencias
                .SelectMany(comp => comp.Comportamientos)
                .Where(c => comportamientosParteActual.Contains(c.ComportamientoId))
                .GroupBy(c => c.ComportamientoId)
                .ToDictionary(g => g.Key, g => g.Last().Puntaje);

            var faltantes = comportamientosParteActual.Count(id =>
                !puntajesPorComportamiento.TryGetValue(id, out var puntaje) || !puntaje.HasValue);

            if (faltantes > 0)
            {
                ModelState.AddModelError(
                    string.Empty,
                    "Debes responder todos los comportamientos de tu bloque en un solo guardado. Después de guardar, tu parte quedará bloqueada.");
            }
        }

        private async Task<EvaluacionFormularioViewModel> BuildFormularioViewModelAsync(
            UsuarioEvaluado evaluador,
            UsuarioEvaluado usuario,
            NivelEvaluacion nivel,
            Evaluacion? evaluacion = null,
            Guid? evaluacionOrigenId = null)
        {
            var detalles = evaluacion == null
                ? new List<EvaluacionDetalle>()
                : await _repo.GetDetallesByEvaluacionAsync(evaluacion.Id);

            var competencias = await _repo.GetCompetenciasAsync();
            var competenciasById = BuildCompetenciasLookup(competencias);
            var comportamientos = await _repo.GetComportamientosByNivelAsync(nivel.Id);
            var parteActual = GetParteEvaluador(evaluador, usuario);
            var comportamientosPermitidos = FilterComportamientosPermitidos(parteActual, comportamientos, competenciasById);
            var cobertura = BuildCobertura(detalles, competencias, comportamientos);

            var vm = new EvaluacionFormularioViewModel
            {
                Id = evaluacion?.Id,
                UsuarioId = usuario.Id,
                NivelId = nivel.Id,
                NombreUsuario = usuario.NombreCompleto,
                CedulaUsuario = usuario.Cedula,
                Cargo = usuario.Cargo,
                Gerencia = usuario.Gerencia,
                Proyecto = evaluacion?.Proyecto ?? usuario.Proyecto,
                NombreNivel = nivel.Nombre,
                AlcanceEvaluadorActual = GetEtiquetaAlcance(parteActual, evaluador.EsSuperAdministrador),
                EvaluacionNormalCompleta = cobertura.EvaluacionNormalCompleta,
                EvaluacionSstCompleta = cobertura.EvaluacionSstCompleta,
                ParteActualGuardada = evaluacion != null && EstaParteCompleta(cobertura, parteActual),
                FechaEvaluacion = evaluacion?.FechaEvaluacion ?? DateTime.Today,
                ObservacionesGenerales = evaluacion?.Observaciones,
                TipoEvaluacion = evaluacion?.TipoEvaluacion ?? (evaluacionOrigenId.HasValue ? "Seguimiento" : "Inicial"),
                EvaluacionOrigenId = evaluacion?.EvaluacionOrigenId ?? evaluacionOrigenId
            };

            foreach (var competencia in competencias.OrderBy(c => c.Orden))
            {
                if (!EvaluacionRolesHelper.DebeVerCompetencia(parteActual, competencia.Nombre))
                {
                    continue;
                }

                var compVm = new CompetenciaEvaluacionVm
                {
                    Nombre = competencia.Nombre
                };

                foreach (var comportamiento in comportamientosPermitidos
                             .Where(x => x.CompetenciaId == competencia.Id)
                             .OrderBy(x => x.Orden))
                {
                    var detalle = detalles.FirstOrDefault(d => d.ComportamientoId == comportamiento.Id);

                    compVm.Comportamientos.Add(new ComportamientoEvaluacionVm
                    {
                        ComportamientoId = comportamiento.Id,
                        Descripcion = comportamiento.Descripcion,
                        Puntaje = detalle?.Puntaje,
                        Comentario = detalle?.Comentario
                    });
                }

                if (compVm.Comportamientos.Any())
                {
                    vm.Competencias.Add(compVm);
                }
            }

            return vm;
        }

        private async Task<IActionResult> ReturnFormularioConErrorGuardadoAsync(
            EvaluacionFormularioViewModel model,
            UsuarioEvaluado evaluador,
            UsuarioEvaluado usuario,
            NivelEvaluacion nivel,
            string? mensajeError)
        {
            if (!string.IsNullOrWhiteSpace(mensajeError))
            {
                ModelState.AddModelError(string.Empty, mensajeError);
            }

            try
            {
                var evaluacion = model.Id.HasValue
                    ? await _repo.GetEvaluacionByIdAsync(model.Id.Value)
                    : null;
                var vm = await BuildFormularioViewModelAsync(
                    evaluador,
                    usuario,
                    nivel,
                    evaluacion,
                    model.EvaluacionOrigenId);

                vm.FechaEvaluacion = model.FechaEvaluacion == default
                    ? DateTime.Today
                    : model.FechaEvaluacion;
                vm.TipoEvaluacion = string.IsNullOrWhiteSpace(model.TipoEvaluacion)
                    ? vm.TipoEvaluacion
                    : model.TipoEvaluacion;
                vm.EvaluacionOrigenId = model.EvaluacionOrigenId;
                vm.ObservacionesGenerales = model.ObservacionesGenerales;
                vm.Gerencia = string.IsNullOrWhiteSpace(model.Gerencia) ? vm.Gerencia : model.Gerencia;
                vm.Proyecto = string.IsNullOrWhiteSpace(model.Proyecto) ? vm.Proyecto : model.Proyecto;

                AplicarRespuestasPublicadas(vm, model);
                return View("Formulario", vm);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(
                    ex,
                    "No fue posible reconstruir el formulario despues de un error de guardado para el usuario {UsuarioId}.",
                    model.UsuarioId);
                return View("Formulario", model);
            }
        }

        private static void AplicarRespuestasPublicadas(
            EvaluacionFormularioViewModel destino,
            EvaluacionFormularioViewModel publicado)
        {
            var respuestas = publicado.Competencias
                .SelectMany(comp => comp.Comportamientos)
                .Where(c => c.ComportamientoId != Guid.Empty)
                .GroupBy(c => c.ComportamientoId)
                .ToDictionary(g => g.Key, g => g.Last());

            foreach (var comportamiento in destino.Competencias.SelectMany(comp => comp.Comportamientos))
            {
                if (!respuestas.TryGetValue(comportamiento.ComportamientoId, out var respuesta))
                {
                    continue;
                }

                comportamiento.Puntaje = respuesta.Puntaje;
                comportamiento.Comentario = respuesta.Comentario;
            }
        }

        private string BuildMensajeErrorGuardado(Exception ex)
        {
            var detalle = GetMensajeTecnicoCorto(ex);
            return "No fue posible guardar la evaluación en Dataverse. " +
                   $"Código de soporte: {HttpContext.TraceIdentifier}. " +
                   $"Detalle técnico: {detalle}";
        }

        private static string GetMensajeTecnicoCorto(Exception ex)
        {
            var current = ex;
            while (current.InnerException != null &&
                   string.IsNullOrWhiteSpace(current.Message))
            {
                current = current.InnerException;
            }

            return string.IsNullOrWhiteSpace(current.Message)
                ? current.GetType().Name
                : current.Message;
        }

        private static List<EvaluacionDetalle> MergeDetalles(
            IEnumerable<EvaluacionDetalle> existentes,
            IEnumerable<EvaluacionDetalle> nuevos,
            IReadOnlySet<Guid> comportamientosParteActual)
        {
            return existentes
                .Where(d => !comportamientosParteActual.Contains(d.ComportamientoId))
                .Concat(nuevos)
                .GroupBy(d => d.ComportamientoId)
                .Select(g => g.Last())
                .ToList();
        }

        private static Guid? ResolveComportamientoId(
            PlanAccion plan,
            IReadOnlyDictionary<string, Comportamiento> comportamientosPorDescripcion)
        {
            if (plan.ComportamientoId.HasValue)
            {
                return plan.ComportamientoId.Value;
            }

            var nombre = plan.ComportamientoNombre ?? plan.Responsable;
            if (string.IsNullOrWhiteSpace(nombre))
            {
                return null;
            }

            return comportamientosPorDescripcion.TryGetValue(nombre, out var comportamiento)
                ? comportamiento.Id
                : null;
        }

        private static bool PlanPerteneceAParte(
            PlanAccion plan,
            IReadOnlySet<Guid> comportamientosParteActual,
            IReadOnlyDictionary<string, Comportamiento> comportamientosPorDescripcion)
        {
            var comportamientoId = ResolveComportamientoId(plan, comportamientosPorDescripcion);
            return comportamientoId.HasValue && comportamientosParteActual.Contains(comportamientoId.Value);
        }

        private static List<PlanAccion> MergePlanes(
            IEnumerable<PlanAccion> existentes,
            IEnumerable<PlanAccion> nuevos,
            IReadOnlySet<Guid> comportamientosParteActual,
            IReadOnlyDictionary<string, Comportamiento> comportamientosPorDescripcion)
        {
            var merged = existentes
                .Where(p => !PlanPerteneceAParte(p, comportamientosParteActual, comportamientosPorDescripcion))
                .ToList();

            merged.AddRange(nuevos);
            return merged;
        }

        private static bool TienePlanAccionRegistrado(
            IEnumerable<PlanAccion> planes,
            IReadOnlySet<Guid> comportamientosParteActual,
            IReadOnlyDictionary<string, Comportamiento> comportamientosPorDescripcion)
        {
            return planes.Any(p =>
                PlanPerteneceAParte(p, comportamientosParteActual, comportamientosPorDescripcion) &&
                !string.IsNullOrWhiteSpace(p.DescripcionAccion));
        }

        private static bool TienePlanAccionRegistrado(IEnumerable<PlanAccionItemVm> planes)
        {
            return planes.Any(p =>
                (p.ComportamientoId.HasValue || !string.IsNullOrWhiteSpace(p.Comportamiento)) &&
                !string.IsNullOrWhiteSpace(p.Descripcion));
        }

        private static List<PlanAccionOpcionVm> BuildPlanOptions(
            TipoParteEvaluacion parte,
            IEnumerable<Competencia> competencias,
            IEnumerable<Comportamiento> comportamientos,
            IEnumerable<EvaluacionDetalle> detalles)
        {
            var competenciasById = BuildCompetenciasLookup(competencias);
            var puntajesPorComportamiento = detalles
                .Where(d => d.ComportamientoId != Guid.Empty)
                .GroupBy(d => d.ComportamientoId)
                .ToDictionary(g => g.Key, g => g.Last().Puntaje);

            return comportamientos
                .Where(c =>
                {
                    competenciasById.TryGetValue(c.CompetenciaId, out var competencia);
                    return EvaluacionRolesHelper.DebeVerCompetencia(parte, competencia?.Nombre) &&
                           puntajesPorComportamiento.TryGetValue(c.Id, out var puntaje) &&
                           EsPuntajeOportunidadMejora(puntaje);
                })
                .OrderBy(c =>
                {
                    competenciasById.TryGetValue(c.CompetenciaId, out var competencia);
                    return competencia?.Orden ?? int.MaxValue;
                })
                .ThenBy(c => c.Orden)
                .ThenBy(c => c.Descripcion)
                .Select(c => new PlanAccionOpcionVm
                {
                    ComportamientoId = c.Id,
                    Competencia = competenciasById.TryGetValue(c.CompetenciaId, out var competencia)
                        ? competencia.Nombre
                        : string.Empty,
                    Comportamiento = c.Descripcion
                })
                .ToList();
        }

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

        private async Task<Evaluacion?> GetSeguimientoExistenteAsync(Guid usuarioId, Guid evaluacionOrigenId)
        {
            var evaluaciones = await _repo.GetEvaluacionesByUsuarioAsync(usuarioId);
            return await SelectPreferredEvaluacionAsync(
                evaluaciones.Where(x => EvaluacionCicloHelper.PerteneceASeguimiento(x, evaluacionOrigenId)));
        }

        private static SemaphoreSlim GetInicioEvaluacionLock(string lockKey)
            => InicioEvaluacionLocks.GetOrAdd(lockKey, _ => new SemaphoreSlim(1, 1));

        private static string BuildHabilitadoLockKey(Guid usuarioId)
            => $"inicial:{usuarioId:D}:habilitado";

        private async Task<Evaluacion> CreatePlaceholderEvaluacionAsync(
            UsuarioEvaluado evaluadorActual,
            UsuarioEvaluado usuarioObjetivo,
            NivelEvaluacion nivel,
            Guid? evaluacionOrigenId)
        {
            var evaluacion = new Evaluacion
            {
                Id = Guid.NewGuid(),
                UsuarioId = usuarioObjetivo.Id,
                NivelId = nivel.Id,
                FechaEvaluacion = DateTime.Today,
                TipoEvaluacion = evaluacionOrigenId.HasValue ? "Seguimiento" : "Inicial",
                EvaluacionOrigenId = evaluacionOrigenId,
                Estado = "Borrador",
                FechaProximaEvaluacion = evaluacionOrigenId.HasValue ? null : DateTime.Today.AddMonths(6),
                EvaluadorNombre = usuarioObjetivo.EvaluadorNombre ?? GetCorreoActual(evaluadorActual),
                Proyecto = usuarioObjetivo.Proyecto,
                Gerencia = usuarioObjetivo.Gerencia
            };

            await _repo.CreateEvaluacionAsync(evaluacion, new List<EvaluacionDetalle>(), new List<PlanAccion>());
            return evaluacion;
        }

        private async Task<Evaluacion?> GetOrCreateEvaluacionEditableAsync(
            UsuarioEvaluado evaluadorActual,
            UsuarioEvaluado usuarioObjetivo,
            NivelEvaluacion nivel,
            Guid? evaluacionOrigenId)
        {
            if (evaluacionOrigenId.HasValue)
            {
                var evaluacionOrigen = await _repo.GetEvaluacionByIdAsync(evaluacionOrigenId.Value);
                if (evaluacionOrigen == null ||
                    evaluacionOrigen.UsuarioId != usuarioObjetivo.Id ||
                    evaluacionOrigen.NivelId != nivel.Id)
                {
                    return null;
                }

                var seguimientoExistente = await GetSeguimientoExistenteAsync(usuarioObjetivo.Id, evaluacionOrigenId.Value);
                if (seguimientoExistente != null)
                {
                    return seguimientoExistente;
                }

                var seguimientoLock = GetInicioEvaluacionLock(EvaluacionCicloHelper.BuildLockKey(evaluacionOrigenId.Value));
                await seguimientoLock.WaitAsync();
                try
                {
                    seguimientoExistente = await GetSeguimientoExistenteAsync(usuarioObjetivo.Id, evaluacionOrigenId.Value);
                    if (seguimientoExistente != null)
                    {
                        return seguimientoExistente;
                    }

                    return await CreatePlaceholderEvaluacionAsync(
                        evaluadorActual,
                        usuarioObjetivo,
                        nivel,
                        evaluacionOrigenId);
                }
                finally
                {
                    seguimientoLock.Release();
                }
            }

            var competencias = await _repo.GetCompetenciasAsync();
            var comportamientosPorNivel = new Dictionary<Guid, List<Comportamiento>>();
            var coberturasPorEvaluacion = new Dictionary<Guid, EvaluacionCoberturaInfo>();
            var evaluacionInicialPendiente = await GetEvaluacionInicialPendienteAsync(
                usuarioObjetivo.Id,
                competencias,
                comportamientosPorNivel,
                coberturasPorEvaluacion);

            if (evaluacionInicialPendiente != null)
            {
                return evaluacionInicialPendiente;
            }

            if (!usuarioObjetivo.Habilitado)
            {
                return null;
            }

            var inicioLock = GetInicioEvaluacionLock(BuildHabilitadoLockKey(usuarioObjetivo.Id));
            await inicioLock.WaitAsync();
            try
            {
                evaluacionInicialPendiente = await GetEvaluacionInicialPendienteAsync(
                    usuarioObjetivo.Id,
                    competencias,
                    comportamientosPorNivel,
                    coberturasPorEvaluacion);

                if (evaluacionInicialPendiente != null)
                {
                    return evaluacionInicialPendiente;
                }

                return await CreatePlaceholderEvaluacionAsync(
                    evaluadorActual,
                    usuarioObjetivo,
                    nivel,
                    evaluacionOrigenId: null);
            }
            finally
            {
                inicioLock.Release();
            }
        }

        private void ConfigureIndexView(
            UsuarioEvaluado evaluador,
            string? cedula,
            string? proyecto,
            string? mensajeError = null,
            string? mensajeAdvertencia = null)
        {
            ViewData["CedulaFiltro"] = cedula;
            ViewData["ProyectoFiltro"] = proyecto;
            ViewBag.EsSuperAdmin = evaluador.EsSuperAdministrador;
            ViewBag.PuedeEliminarEvaluaciones = PuedeEliminarEvaluaciones(evaluador);
            ViewBag.TiposFormularioCertificado = evaluador.EsSuperAdministrador
                ? BuildTipoFormularioOpciones()
                : new List<TipoFormularioOpcionViewModel>();
            ViewBag.ErrorCargaEvaluaciones = mensajeError;
            ViewBag.AdvertenciaCargaEvaluaciones = mensajeAdvertencia;
        }

        // ================== LISTADO PRINCIPAL ==================

        public async Task<IActionResult> Index(string? cedula, string? proyecto)
        {
            var evaluador = await GetEvaluadorActualAsync();
            if (evaluador == null)
            {
                return Forbid();
            }

            try
            {
                var evaluaciones = await GetEvaluacionesVisiblesAsync(evaluador);
                var evaluacionesInvalidas = evaluaciones.Count(e =>
                    e.Id == Guid.Empty ||
                    e.UsuarioId == Guid.Empty ||
                    e.NivelId == Guid.Empty);
                var evals = evaluaciones
                    .Where(e =>
                        e.Id != Guid.Empty &&
                        e.UsuarioId != Guid.Empty &&
                        e.NivelId != Guid.Empty)
                    .ToList();

                var competencias = await _repo.GetCompetenciasAsync();
                var usuariosDict = (await _repo.GetUsuariosByIdsAsync(evals.Select(e => e.UsuarioId)))
                    .GroupBy(x => x.Id)
                    .ToDictionary(g => g.Key, g => g.First());
                var nivelesDict = (await _repo.GetNivelesByIdsAsync(evals.Select(e => e.NivelId)))
                    .GroupBy(x => x.Id)
                    .ToDictionary(g => g.Key, g => g.First());
                var comportamientosPorNivel = (await _repo.GetComportamientosByNivelesAsync(evals.Select(e => e.NivelId)))
                    .Where(x => x.NivelId != Guid.Empty)
                    .GroupBy(x => x.NivelId)
                    .ToDictionary(g => g.Key, g => g.ToList());
                var detallesPorEvaluacion = (await _repo.GetDetallesByEvaluacionesAsync(evals.Select(e => e.Id)))
                    .Where(x => x.EvaluacionId != Guid.Empty)
                    .GroupBy(x => x.EvaluacionId)
                    .ToDictionary(g => g.Key, g => (IReadOnlyCollection<EvaluacionDetalle>)g.ToList());

                var vm = new List<EvaluacionListaViewModel>(evals.Count);

                foreach (var evaluacion in evals)
                {
                    usuariosDict.TryGetValue(evaluacion.UsuarioId, out var usuario);
                    nivelesDict.TryGetValue(evaluacion.NivelId, out var nivel);
                    comportamientosPorNivel.TryGetValue(evaluacion.NivelId, out var comportamientos);
                    detallesPorEvaluacion.TryGetValue(evaluacion.Id, out var detalles);

                    usuario ??= new UsuarioEvaluado();
                    nivel ??= new NivelEvaluacion();
                    IReadOnlyCollection<Comportamiento> comportamientosActuales =
                        (IReadOnlyCollection<Comportamiento>?)comportamientos ?? Array.Empty<Comportamiento>();

                    var cobertura = BuildCobertura(
                        detalles ?? Array.Empty<EvaluacionDetalle>(),
                        competencias,
                        comportamientosActuales);

                    vm.Add(new EvaluacionListaViewModel
                    {
                        Id = evaluacion.Id,
                        UsuarioId = evaluacion.UsuarioId,
                        FechaEvaluacion = evaluacion.FechaEvaluacion,
                        ProximaEvaluacion = evaluacion.FechaProximaEvaluacion,
                        NombreUsuario = usuario.NombreCompleto,
                        CedulaUsuario = usuario.Cedula,
                        GerenciaUsuario = evaluacion.Gerencia ?? usuario.Gerencia ?? string.Empty,
                        ProyectoUsuario = evaluacion.Proyecto ?? usuario.Proyecto ?? string.Empty,
                        NivelNombre = nivel.Nombre,
                        NivelCodigo = nivel.Codigo,
                        Proyecto = evaluacion.Proyecto ?? usuario.Proyecto,
                        Gerencia = evaluacion.Gerencia ?? usuario.Gerencia,
                        TipoEvaluacion = evaluacion.TipoEvaluacion,
                        ResultadoFinal = cobertura.AmbasPartesCompletas
                            ? evaluacion.Total ?? cobertura.TotalCalculado
                            : null,
                        ResultadoEvaluacionNormal = cobertura.TotalEvaluacionNormalCalculado,
                        ResultadoEvaluacionSst = cobertura.TotalEvaluacionSstCalculado,
                        EvaluacionNormalCompleta = cobertura.EvaluacionNormalCompleta,
                        EvaluacionSstCompleta = cobertura.EvaluacionSstCompleta,
                        TieneReporteFirmado = evaluacion.TieneReporteFirmado,
                        ReporteFirmadoNombre = evaluacion.ReporteFirmadoNombre
                    });
                }

                if (!string.IsNullOrWhiteSpace(cedula))
                {
                    vm = vm
                        .Where(x => x.CedulaUsuario?.Contains(cedula, StringComparison.OrdinalIgnoreCase) == true)
                        .ToList();
                }

                if (!string.IsNullOrWhiteSpace(proyecto))
                {
                    vm = vm
                        .Where(x => x.ProyectoUsuario?.Contains(proyecto, StringComparison.OrdinalIgnoreCase) == true)
                        .ToList();
                }

                var advertencia = evaluacionesInvalidas > 0
                    ? $"Se omitieron {evaluacionesInvalidas} evaluaciones con datos incompletos para evitar errores al cargar el listado."
                    : null;

                ConfigureIndexView(evaluador, cedula, proyecto, mensajeAdvertencia: advertencia);
                return View(vm);
            }
            catch (Exception ex)
            {
                _logger.LogError(
                    ex,
                    "No fue posible cargar el listado de 'Mis evaluaciones' para el evaluador {CorreoEvaluador}.",
                    GetCorreoActual(evaluador));

                ConfigureIndexView(
                    evaluador,
                    cedula,
                    proyecto,
                    mensajeError: "No fue posible cargar las evaluaciones en este momento. Intenta nuevamente en unos segundos.");

                return View(new List<EvaluacionListaViewModel>());
            }
        }

        [HttpGet]
        public async Task<IActionResult> DescargarPlantillaUsuarios()
        {
            var evaluador = await GetEvaluadorActualAsync();
            if (evaluador == null)
            {
                return Forbid();
            }

            if (!evaluador.EsSuperAdministrador)
            {
                return Forbid();
            }

            using var workbook = new XLWorkbook();
            var worksheet = workbook.Worksheets.Add("Usuarios");
            for (var i = 0; i < PlantillaImportacionUsuariosColumnas.Length; i++)
            {
                var cell = worksheet.Cell(1, i + 1);
                cell.Value = PlantillaImportacionUsuariosColumnas[i];
                cell.Style.Font.Bold = true;
                cell.Style.Fill.BackgroundColor = XLColor.FromHtml("#E9ECEF");
            }

            worksheet.SheetView.FreezeRows(1);
            worksheet.Range(1, 1, 1, PlantillaImportacionUsuariosColumnas.Length).SetAutoFilter();
            worksheet.Columns().AdjustToContents();

            var opcionesWorksheet = workbook.Worksheets.Add("TiposFormulario");
            opcionesWorksheet.Cell(1, 1).Value = "Valor";
            opcionesWorksheet.Cell(1, 2).Value = "Nombre";
            opcionesWorksheet.Range(1, 1, 1, 2).Style.Font.Bold = true;

            var row = 2;
            foreach (var opcion in TipoFormularioNiveles.OrderBy(x => x.Value.Nombre))
            {
                opcionesWorksheet.Cell(row, 1).Value = opcion.Key;
                opcionesWorksheet.Cell(row, 2).Value = opcion.Value.Nombre;
                row++;
            }

            opcionesWorksheet.Columns().AdjustToContents();

            using var stream = new MemoryStream();
            workbook.SaveAs(stream);

            return File(
                stream.ToArray(),
                "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet",
                $"plantilla-importacion-usuarios-{DateTime.Today:yyyyMMdd}.xlsx");
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> ImportarUsuarios(IFormFile? archivo)
        {
            var evaluador = await GetEvaluadorActualAsync();
            if (evaluador == null)
            {
                return Forbid();
            }

            if (!evaluador.EsSuperAdministrador)
            {
                return Forbid();
            }

            if (archivo == null || archivo.Length == 0)
            {
                TempData["ErrorImportacionUsuarios"] = "Debes adjuntar un archivo Excel para importar.";
                return RedirectToAction(nameof(Index));
            }

            var creados = 0;
            var actualizados = 0;
            var programados = 0;
            var errores = new List<string>();

            try
            {
                using var stream = archivo.OpenReadStream();
                using var workbook = new XLWorkbook(stream);
                var worksheet = workbook.Worksheets.First();
                var headerMap = BuildHeaderMap(worksheet);
                var missingColumns = PlantillaImportacionUsuariosColumnas
                    .Where(column => !headerMap.ContainsKey(NormalizeImportKey(column)))
                    .ToList();

                if (missingColumns.Any())
                {
                    TempData["ErrorImportacionUsuarios"] =
                        "La plantilla no contiene todas las columnas requeridas: " +
                        string.Join(", ", missingColumns);
                    return RedirectToAction(nameof(Index));
                }

                var lastRow = worksheet.LastRowUsed()?.RowNumber() ?? 1;
                for (var row = 2; row <= lastRow; row++)
                {
                    var cedula = GetImportText(worksheet, headerMap, row, "Cedula");
                    if (string.IsNullOrWhiteSpace(cedula))
                    {
                        continue;
                    }

                    try
                    {
                        var existente = await _repo.GetUsuarioByCedulaAsync(cedula);
                        var fechaActivacionProgramada = GetImportDate(worksheet, headerMap, row, "Fecha activacion programada");
                        var habilitado = !fechaActivacionProgramada.HasValue ||
                                         fechaActivacionProgramada.Value.Date <= DateTime.Today;

                        var usuario = new UsuarioEvaluado
                        {
                            Id = existente?.Id ?? Guid.Empty,
                            Cedula = cedula,
                            NombreCompleto = GetImportText(worksheet, headerMap, row, "Nombre completo") ?? string.Empty,
                            Cargo = GetImportText(worksheet, headerMap, row, "Cargo"),
                            CorreoEvaluador = GetImportText(worksheet, headerMap, row, "Nombre evaluador principal"),
                            CargoJefeInmediato = GetImportText(worksheet, headerMap, row, "Cargo evaluador principal"),
                            EvaluadorNombre = GetImportText(worksheet, headerMap, row, "Correo evaluador principal"),
                            NombreEvaluadorSst = GetImportText(worksheet, headerMap, row, "Nombre evaluador SST"),
                            CorreoEvaluadorSst = GetImportText(worksheet, headerMap, row, "Correo evaluador SST"),
                            CargoEvaluadorSst = GetImportText(worksheet, headerMap, row, "Cargo evaluador SST"),
                            FechaIngreso = GetImportDate(worksheet, headerMap, row, "Fecha ingreso"),
                            FechaFinalizacionContrato = GetImportDate(worksheet, headerMap, row, "Fecha finalización contrato"),
                            FechaFinalizacionPeriodoPrueba = GetImportDate(worksheet, headerMap, row, "Fecha Finalizacion Periodo de Prueba"),
                            FechaActivacionProgramada = fechaActivacionProgramada,
                            Gerencia = GetImportText(worksheet, headerMap, row, "Gerencia"),
                            Proyecto = GetImportText(worksheet, headerMap, row, "Proyecto"),
                            TipoFormulario = ParseTipoFormulario(
                                GetImportText(worksheet, headerMap, row, "Tipo de formulario"),
                                row),
                            Habilitado = habilitado
                        };

                        await _repo.UpsertUsuarioImportadoAsync(usuario);

                        if (existente == null)
                        {
                            creados++;
                        }
                        else
                        {
                            actualizados++;
                        }

                        if (!habilitado && fechaActivacionProgramada.HasValue)
                        {
                            programados++;
                        }
                    }
                    catch (Exception ex)
                    {
                        errores.Add($"Fila {row}: {ex.Message}");
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "No fue posible importar usuarios desde Excel.");
                TempData["ErrorImportacionUsuarios"] = "No fue posible leer el archivo Excel. Revisa que sea una plantilla válida.";
                return RedirectToAction(nameof(Index));
            }

            if (errores.Any())
            {
                TempData["ErrorImportacionUsuarios"] =
                    $"Importación parcial. Creados: {creados}. Actualizados: {actualizados}. Errores: " +
                    string.Join(" | ", errores.Take(5)) +
                    (errores.Count > 5 ? $" | y {errores.Count - 5} más." : string.Empty);
            }
            else
            {
                TempData["MensajeImportacionUsuarios"] =
                    $"Importación completada. Creados: {creados}. Actualizados: {actualizados}. Programados para activación futura: {programados}.";
            }

            return RedirectToAction(nameof(Index));
        }

        private static Dictionary<string, int> BuildHeaderMap(IXLWorksheet worksheet)
        {
            var map = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            var lastColumn = worksheet.Row(1).LastCellUsed()?.Address.ColumnNumber ?? 0;

            for (var column = 1; column <= lastColumn; column++)
            {
                var header = worksheet.Cell(1, column).GetString();
                var key = NormalizeImportKey(header);
                if (!string.IsNullOrWhiteSpace(key) && !map.ContainsKey(key))
                {
                    map[key] = column;
                }
            }

            return map;
        }

        private static string? GetImportText(
            IXLWorksheet worksheet,
            IReadOnlyDictionary<string, int> headerMap,
            int row,
            string columnName)
        {
            if (!headerMap.TryGetValue(NormalizeImportKey(columnName), out var column))
            {
                return null;
            }

            var value = worksheet.Cell(row, column).GetString()?.Trim();
            return string.IsNullOrWhiteSpace(value) ? null : value;
        }

        private static DateTime? GetImportDate(
            IXLWorksheet worksheet,
            IReadOnlyDictionary<string, int> headerMap,
            int row,
            string columnName)
        {
            if (!headerMap.TryGetValue(NormalizeImportKey(columnName), out var column))
            {
                return null;
            }

            var cell = worksheet.Cell(row, column);
            if (cell.IsEmpty())
            {
                return null;
            }

            if (cell.TryGetValue<DateTime>(out var date))
            {
                return date.Date;
            }

            var raw = cell.GetString()?.Trim();
            if (string.IsNullOrWhiteSpace(raw))
            {
                return null;
            }

            var culture = CultureInfo.GetCultureInfo("es-CO");
            var formats = new[]
            {
                "yyyy-MM-dd",
                "dd/MM/yyyy",
                "d/M/yyyy",
                "dd-MM-yyyy",
                "d-M-yyyy",
                "M/d/yyyy",
                "MM/dd/yyyy"
            };

            if (DateTime.TryParseExact(raw, formats, culture, DateTimeStyles.None, out date) ||
                DateTime.TryParse(raw, culture, DateTimeStyles.None, out date))
            {
                return date.Date;
            }

            throw new InvalidOperationException($"La columna '{columnName}' no tiene una fecha válida.");
        }

        private static int? ParseTipoFormulario(string? value, int row)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                return null;
            }

            var trimmed = value.Trim();
            if (int.TryParse(trimmed, out var numericValue) &&
                TipoFormularioNiveles.ContainsKey(numericValue))
            {
                return numericValue;
            }

            var normalized = NormalizeImportKey(trimmed);
            var match = TipoFormularioNiveles.FirstOrDefault(x =>
                NormalizeImportKey(x.Value.Nombre) == normalized ||
                NormalizeImportKey(x.Value.Codigo) == normalized);

            if (!match.Equals(default(KeyValuePair<int, (string Codigo, string Nombre)>)))
            {
                return match.Key;
            }

            throw new InvalidOperationException($"Fila {row}: tipo de formulario inválido '{value}'.");
        }

        private static string NormalizeImportKey(string? value)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                return string.Empty;
            }

            var normalized = value.Trim().Normalize(NormalizationForm.FormD);
            var builder = new StringBuilder(normalized.Length);
            foreach (var ch in normalized)
            {
                if (CharUnicodeInfo.GetUnicodeCategory(ch) != UnicodeCategory.NonSpacingMark)
                {
                    builder.Append(char.ToLowerInvariant(ch));
                }
            }

            return builder
                .ToString()
                .Normalize(NormalizationForm.FormC)
                .Replace(" ", string.Empty)
                .Replace("_", string.Empty)
                .Replace("-", string.Empty);
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> Eliminar([FromBody] EliminarEvaluacionesRequest request)
        {
            var evaluador = await GetEvaluadorActualAsync();
            if (evaluador == null)
            {
                return Forbid();
            }

            if (!PuedeEliminarEvaluaciones(evaluador))
            {
                return Forbid();
            }

            if (!_repo.IsDataverseBacked)
            {
                return StatusCode(
                    503,
                    "La conexión activa a Dataverse no está disponible. Se bloqueó la eliminación para evitar borrar solo en memoria.");
            }

            var ids = request?.Ids?
                .Where(id => id != Guid.Empty)
                .Distinct()
                .ToList()
                ?? new List<Guid>();

            if (!ids.Any())
            {
                return BadRequest("Debes seleccionar al menos una evaluación.");
            }

            var idsSolicitados = ids.ToHashSet();
            var evaluacionesVisibles = await GetEvaluacionesVisiblesAsync(evaluador);
            var idsPermitidos = evaluacionesVisibles
                .Where(e => idsSolicitados.Contains(e.Id))
                .Select(e => e.Id)
                .ToHashSet();

            if (idsPermitidos.Count != idsSolicitados.Count)
            {
                return Forbid();
            }

            var eliminadas = 0;

            try
            {
                foreach (var evaluacionId in ids)
                {
                    await _repo.DeleteEvaluacionAsync(evaluacionId);
                    eliminadas++;
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(
                    ex,
                    "No fue posible eliminar las evaluaciones seleccionadas en Dataverse para el usuario {Correo}.",
                    GetCorreoActual(evaluador));

                var mensaje = eliminadas == 0
                    ? "No fue posible eliminar las evaluaciones seleccionadas en Dataverse."
                    : $"Se eliminaron {eliminadas} evaluaciones antes de que ocurriera un error. Revisa el estado en Dataverse antes de reintentar.";

                return StatusCode(500, mensaje);
            }

            var mensajeExito = eliminadas == 1
                ? "La evaluación y sus registros relacionados se eliminaron correctamente de Dataverse."
                : $"Se eliminaron {eliminadas} evaluaciones y sus registros relacionados directamente de Dataverse.";

            return Json(new
            {
                deletedCount = eliminadas,
                message = mensajeExito
            });
        }

        // ================== NUEVA EVALUACIÓN ==================

        [HttpGet]
        public async Task<IActionResult> Nueva(Guid usuarioId)
        {
            var evaluador = await GetEvaluadorActualAsync();
            if (evaluador == null)
            {
                return Forbid();
            }

            await _repo.HabilitarUsuariosProgramadosAsync(DateTime.Today);
            var usuario = await _repo.GetUsuarioByIdAsync(usuarioId);
            if (usuario == null)
            {
                return NotFound();
            }

            if (!PuedeAccederAUsuario(evaluador, usuario))
            {
                return Forbid();
            }

            if (!TieneRolDiligenciarUsuario(evaluador, usuario))
            {
                return EvaluadorSinBloqueAsignado();
            }

            if (!usuario.Habilitado)
            {
                return UsuarioNoHabilitado();
            }

            var parteActual = GetParteEvaluador(evaluador, usuario);
            var competencias = await _repo.GetCompetenciasAsync();
            var comportamientosPorNivel = new Dictionary<Guid, List<Comportamiento>>();
            var coberturasPorEvaluacion = new Dictionary<Guid, EvaluacionCoberturaInfo>();
            var evaluacionPendiente = await GetEvaluacionInicialPendienteParaParteAsync(
                usuarioId,
                parteActual,
                competencias,
                comportamientosPorNivel,
                coberturasPorEvaluacion);

            if (evaluacionPendiente != null)
            {
                return RedirectToAction(nameof(Editar), new { id = evaluacionPendiente.Id });
            }

            var niveles = await _repo.GetNivelesActivosAsync();
            var nivelAuto = ResolveNivelPorTipoFormulario(usuario, niveles);
            if (nivelAuto != null)
            {
                return RedirectToAction(nameof(Formulario), new { usuarioId = usuario.Id, nivelId = nivelAuto.Id });
            }

            ViewBag.Usuario = usuario;
            ViewBag.Niveles = niveles;
            return View();
        }

        // ================== FORMULARIO (CREAR / SEGUIMIENTO) ==================

        [HttpGet]
        public async Task<IActionResult> Formulario(Guid usuarioId, Guid nivelId, Guid? evaluacionOrigenId = null)
        {
            var evaluador = await GetEvaluadorActualAsync();
            if (evaluador == null)
            {
                return Forbid();
            }

            await _repo.HabilitarUsuariosProgramadosAsync(DateTime.Today);
            var usuario = await _repo.GetUsuarioByIdAsync(usuarioId);
            var nivel = await _repo.GetNivelByIdAsync(nivelId);

            if (usuario == null || nivel == null)
            {
                return NotFound();
            }

            if (!PuedeAccederAUsuario(evaluador, usuario))
            {
                return Forbid();
            }

            if (!TieneRolDiligenciarUsuario(evaluador, usuario))
            {
                return EvaluadorSinBloqueAsignado();
            }

            if (!usuario.Habilitado)
            {
                return UsuarioNoHabilitado();
            }

            if (evaluacionOrigenId.HasValue)
            {
                return BadRequest("La opción de reevaluar está deshabilitada.");
            }

            var parteActual = GetParteEvaluador(evaluador, usuario);
            var competencias = await _repo.GetCompetenciasAsync();
            var comportamientosPorNivel = new Dictionary<Guid, List<Comportamiento>>();
            var coberturasPorEvaluacion = new Dictionary<Guid, EvaluacionCoberturaInfo>();
            var evaluacionPendiente = await GetEvaluacionInicialPendienteParaParteAsync(
                usuarioId,
                parteActual,
                competencias,
                comportamientosPorNivel,
                coberturasPorEvaluacion);

            if (evaluacionPendiente != null)
            {
                return RedirectToAction(nameof(Editar), new { id = evaluacionPendiente.Id });
            }

            var vm = await BuildFormularioViewModelAsync(
                evaluador,
                usuario,
                nivel,
                evaluacion: null,
                evaluacionOrigenId: null);

            return View("Formulario", vm);
        }

        // ================== EDITAR EVALUACIÓN EXISTENTE ==================

        [HttpGet]
        public async Task<IActionResult> Editar(Guid id)
        {
            var evaluador = await GetEvaluadorActualAsync();
            if (evaluador == null)
            {
                return Forbid();
            }

            await _repo.HabilitarUsuariosProgramadosAsync(DateTime.Today);
            var evaluacion = await _repo.GetEvaluacionByIdAsync(id);
            if (evaluacion == null)
            {
                return NotFound();
            }

            var usuario = await _repo.GetUsuarioByIdAsync(evaluacion.UsuarioId);
            var nivel = await _repo.GetNivelByIdAsync(evaluacion.NivelId);
            if (usuario == null || nivel == null)
            {
                return NotFound();
            }

            if (!PuedeAccederAUsuario(evaluador, usuario))
            {
                return Forbid();
            }

            if (!TieneRolDiligenciarUsuario(evaluador, usuario))
            {
                return EvaluadorSinBloqueAsignado();
            }

            if (!usuario.Habilitado)
            {
                return UsuarioNoHabilitado();
            }

            var vm = await BuildFormularioViewModelAsync(evaluador, usuario, nivel, evaluacion);
            return View("Formulario", vm);
        }

        // ================== GUARDAR (CREATE / UPDATE) ==================

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> Guardar(EvaluacionFormularioViewModel model, string accion)
        {
            var evaluador = await GetEvaluadorActualAsync();
            if (evaluador == null)
            {
                return Forbid();
            }

            await _repo.HabilitarUsuariosProgramadosAsync(DateTime.Today);
            var usuario = await _repo.GetUsuarioByIdAsync(model.UsuarioId);
            var nivel = await _repo.GetNivelByIdAsync(model.NivelId);
            if (usuario == null || nivel == null)
            {
                return NotFound();
            }

            if (!PuedeAccederAUsuario(evaluador, usuario))
            {
                return Forbid();
            }

            if (!TieneRolDiligenciarUsuario(evaluador, usuario))
            {
                return EvaluadorSinBloqueAsignado();
            }

            if (!usuario.Habilitado)
            {
                return UsuarioNoHabilitado();
            }

            try
            {
                var competencias = await _repo.GetCompetenciasAsync();
                var competenciasById = BuildCompetenciasLookup(competencias);
                var comportamientos = await _repo.GetComportamientosByNivelAsync(model.NivelId);
                var parteActual = GetParteEvaluador(evaluador, usuario);
                var comportamientosParteActual = GetComportamientosPermitidos(parteActual, comportamientos, competenciasById);

                model.AlcanceEvaluadorActual = GetEtiquetaAlcance(parteActual, evaluador.EsSuperAdministrador);
                ValidarGuardadoUnicoPorParte(model, comportamientosParteActual);

                if (!ModelState.IsValid)
                {
                    return await ReturnFormularioConErrorGuardadoAsync(model, evaluador, usuario, nivel, null);
                }

                if (model.EvaluacionOrigenId.HasValue)
                {
                    return BadRequest("La opción de reevaluar está deshabilitada.");
                }

                var comportamientosPorNivel = new Dictionary<Guid, List<Comportamiento>>
                {
                    [model.NivelId] = comportamientos
                };
                var coberturasPorEvaluacion = new Dictionary<Guid, EvaluacionCoberturaInfo>();

                var evaluacionPendienteParaParte = await GetEvaluacionInicialPendienteParaParteAsync(
                    model.UsuarioId,
                    parteActual,
                    competencias,
                    comportamientosPorNivel,
                    coberturasPorEvaluacion);

                string lockKey;
                if (evaluacionPendienteParaParte != null)
                {
                    lockKey = EvaluacionCicloHelper.BuildLockKey(evaluacionPendienteParaParte.Id);
                }
                else
                {
                    lockKey = BuildHabilitadoLockKey(usuario.Id);
                }

                var saveLock = GetInicioEvaluacionLock(lockKey);
                await saveLock.WaitAsync();

                Evaluacion evaluacion;
                EvaluacionCoberturaInfo cobertura;

                try
                {
                    coberturasPorEvaluacion.Clear();

                    var evaluacionExistente = model.Id.HasValue
                        ? await _repo.GetEvaluacionByIdAsync(model.Id.Value)
                        : null;

                    if (evaluacionExistente == null)
                    {
                        evaluacionExistente = await GetEvaluacionInicialPendienteParaParteAsync(
                            model.UsuarioId,
                            parteActual,
                            competencias,
                            comportamientosPorNivel,
                            coberturasPorEvaluacion);
                    }

                    var detallesExistentes = evaluacionExistente == null
                        ? new List<EvaluacionDetalle>()
                        : await _repo.GetDetallesByEvaluacionAsync(evaluacionExistente.Id);
                    var planesExistentes = evaluacionExistente == null
                        ? new List<PlanAccion>()
                        : await _repo.GetPlanesByEvaluacionAsync(evaluacionExistente.Id);

                    if (evaluacionExistente != null)
                    {
                        var coberturaAntesDeGuardar = BuildCobertura(detallesExistentes, competencias, comportamientos);
                        if (EstaParteCompleta(coberturaAntesDeGuardar, parteActual))
                        {
                            return BadRequest("Tu parte de esta evaluación ya fue guardada y no puede modificarse.");
                        }
                    }

                    var nuevosDetalles = model.Competencias
                        .SelectMany(comp => comp.Comportamientos)
                        .Where(c => c.Puntaje.HasValue && comportamientosParteActual.Contains(c.ComportamientoId))
                        .Select(c => new EvaluacionDetalle
                        {
                            Id = Guid.NewGuid(),
                            EvaluacionId = evaluacionExistente?.Id ?? Guid.Empty,
                            ComportamientoId = c.ComportamientoId,
                            Puntaje = c.Puntaje!.Value,
                            Comentario = c.Comentario
                        })
                        .ToList();

                    var detallesFinales = evaluacionExistente == null
                        ? nuevosDetalles
                        : MergeDetalles(detallesExistentes, nuevosDetalles, comportamientosParteActual);
                    cobertura = BuildCobertura(detallesFinales, competencias, comportamientos);

                    var fechaProxima = evaluacionExistente?.FechaProximaEvaluacion
                                       ?? (model.TipoEvaluacion == "Inicial"
                                           ? model.FechaEvaluacion.AddMonths(6)
                                           : null);

                    var observaciones = MergeObservaciones(
                        evaluacionExistente?.Observaciones,
                        model.ObservacionesGenerales);

                    evaluacion = new Evaluacion
                    {
                        Id = evaluacionExistente?.Id ?? Guid.NewGuid(),
                        UsuarioId = model.UsuarioId,
                        NivelId = model.NivelId,
                        FechaEvaluacion = evaluacionExistente?.FechaEvaluacion ?? model.FechaEvaluacion,
                        TipoEvaluacion = model.TipoEvaluacion,
                        EvaluacionOrigenId = null,
                        Observaciones = observaciones,
                        Estado = cobertura.AmbasPartesCompletas ? "Finalizada" : "Parcial",
                        FechaProximaEvaluacion = fechaProxima,
                        EvaluadorNombre = usuario.EvaluadorNombre ?? evaluacionExistente?.EvaluadorNombre ?? GetCorreoActual(evaluador),
                        Proyecto = string.IsNullOrWhiteSpace(model.Proyecto) ? usuario.Proyecto : model.Proyecto,
                        Gerencia = string.IsNullOrWhiteSpace(model.Gerencia) ? usuario.Gerencia : model.Gerencia,
                        Total = cobertura.TotalCalculado,
                        ReporteFirmadoId = evaluacionExistente?.ReporteFirmadoId,
                        ReporteFirmadoNombre = evaluacionExistente?.ReporteFirmadoNombre
                    };

                    if (evaluacionExistente == null)
                    {
                        await _repo.CreateEvaluacionAsync(evaluacion, detallesFinales, planesExistentes);
                    }
                    else
                    {
                        await _repo.UpdateEvaluacionAsync(evaluacion, detallesFinales, planesExistentes);
                    }
                }
                finally
                {
                    saveLock.Release();
                }

                if (EstaParteCompleta(cobertura, parteActual))
                {
                    return RedirectToAction(nameof(PlanAccion), new { id = evaluacion.Id });
                }

                return RedirectToAction(nameof(Index));
            }
            catch (Exception ex)
            {
                _logger.LogError(
                    ex,
                    "No fue posible guardar la evaluacion para el usuario {UsuarioId} ({Cedula}) por el evaluador {CorreoEvaluador}.",
                    usuario.Id,
                    usuario.Cedula,
                    GetCorreoActual(evaluador));

                return await ReturnFormularioConErrorGuardadoAsync(
                    model,
                    evaluador,
                    usuario,
                    nivel,
                    BuildMensajeErrorGuardado(ex));
            }
        }

        [HttpGet]
        public IActionResult Reevaluar(Guid id)
        {
            _ = id;
            return BadRequest("La opción de reevaluar está deshabilitada.");
        }

        // ================== CARPETA POR USUARIO ==================

        [HttpGet]
        public async Task<IActionResult> CarpetaUsuario(Guid usuarioId)
        {
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

            var evaluaciones = await _repo.GetEvaluacionesByUsuarioAsync(usuarioId);
            var competencias = await _repo.GetCompetenciasAsync();
            var nivelesDict = new Dictionary<Guid, NivelEvaluacion>();
            var comportamientosPorNivel = new Dictionary<Guid, List<Comportamiento>>();
            var lista = new List<EvaluacionListaViewModel>();

            foreach (var evaluacion in evaluaciones)
            {
                if (!nivelesDict.TryGetValue(evaluacion.NivelId, out var nivel))
                {
                    nivel = await _repo.GetNivelByIdAsync(evaluacion.NivelId) ?? new NivelEvaluacion();
                    nivelesDict[evaluacion.NivelId] = nivel;
                }

                if (!comportamientosPorNivel.TryGetValue(evaluacion.NivelId, out var comportamientos))
                {
                    comportamientos = await _repo.GetComportamientosByNivelAsync(evaluacion.NivelId);
                    comportamientosPorNivel[evaluacion.NivelId] = comportamientos;
                }

                var detalles = await _repo.GetDetallesByEvaluacionAsync(evaluacion.Id);
                var cobertura = BuildCobertura(detalles, competencias, comportamientos);

                lista.Add(new EvaluacionListaViewModel
                {
                    Id = evaluacion.Id,
                    UsuarioId = evaluacion.UsuarioId,
                    FechaEvaluacion = evaluacion.FechaEvaluacion,
                    ProximaEvaluacion = evaluacion.FechaProximaEvaluacion,
                    NombreUsuario = usuario.NombreCompleto,
                    CedulaUsuario = usuario.Cedula,
                    GerenciaUsuario = evaluacion.Gerencia ?? usuario.Gerencia ?? string.Empty,
                    ProyectoUsuario = evaluacion.Proyecto ?? usuario.Proyecto ?? string.Empty,
                    NivelNombre = nivel.Nombre,
                    NivelCodigo = nivel.Codigo,
                    Proyecto = evaluacion.Proyecto ?? usuario.Proyecto,
                    Gerencia = evaluacion.Gerencia ?? usuario.Gerencia,
                    TipoEvaluacion = evaluacion.TipoEvaluacion,
                    ResultadoFinal = cobertura.AmbasPartesCompletas
                        ? evaluacion.Total ?? cobertura.TotalCalculado
                        : null,
                    ResultadoEvaluacionNormal = cobertura.TotalEvaluacionNormalCalculado,
                    ResultadoEvaluacionSst = cobertura.TotalEvaluacionSstCalculado,
                    EvaluacionNormalCompleta = cobertura.EvaluacionNormalCompleta,
                    EvaluacionSstCompleta = cobertura.EvaluacionSstCompleta,
                    TieneReporteFirmado = evaluacion.TieneReporteFirmado,
                    ReporteFirmadoNombre = evaluacion.ReporteFirmadoNombre
                });
            }

            var vm = new CarpetaUsuarioViewModel
            {
                UsuarioId = usuario.Id,
                NombreUsuario = usuario.NombreCompleto,
                CedulaUsuario = usuario.Cedula,
                Cargo = usuario.Cargo,
                Gerencia = usuario.Gerencia,
                Proyecto = usuario.Proyecto,
                CorreoElectronico = usuario.CorreoElectronico,
                Evaluaciones = lista.OrderByDescending(x => x.FechaEvaluacion).ToList()
            };

            return View(vm);
        }

        // ================== REPORTE ==================

        [HttpGet]
        public async Task<IActionResult> Reporte(Guid id)
        {
            var evaluador = await GetEvaluadorActualAsync();
            if (evaluador == null)
            {
                return Forbid();
            }

            var vm = await BuildReporteViewModelAsync(id, evaluador);
            if (vm == null)
            {
                return NotFound();
            }

            return View("Reporte", vm);
        }

        [HttpGet]
        public async Task<IActionResult> PlanAccion(Guid id)
        {
            var evaluador = await GetEvaluadorActualAsync();
            if (evaluador == null)
            {
                return Forbid();
            }

            await _repo.HabilitarUsuariosProgramadosAsync(DateTime.Today);
            var vm = await BuildReporteViewModelAsync(id, evaluador);
            if (vm == null)
            {
                return NotFound();
            }

            ViewBag.ModoPlanAccion = true;
            return View("Reporte", vm);
        }

        [HttpGet]
        public async Task<IActionResult> ImprimirResultados(Guid id)
        {
            var evaluador = await GetEvaluadorActualAsync();
            if (evaluador == null)
            {
                return Forbid();
            }

            var vm = await BuildReporteViewModelAsync(id, evaluador);
            if (vm == null)
            {
                return NotFound();
            }

            if (!vm.EvaluacionNormalCompleta || !vm.EvaluacionSstCompleta)
            {
                return BadRequest("El certificado solo se puede exportar cuando ambas partes hayan completado la evaluación.");
            }

            if (!TieneFirmasCompletasParaCertificado(vm))
            {
                return BadRequest("El certificado solo se puede exportar cuando existan firmas válidas en PNG o JPG para el evaluador y el evaluador SST.");
            }

            return View("ReporteImpresion", vm);
        }

        [HttpGet]
        public async Task<IActionResult> CertificadoEnBlanco(int? tipoFormulario)
        {
            var evaluador = await GetEvaluadorActualAsync();
            if (evaluador == null || !evaluador.EsSuperAdministrador)
            {
                return Forbid();
            }

            if (!tipoFormulario.HasValue)
            {
                return BadRequest("Debes seleccionar un tipo de formulario.");
            }

            var vm = await BuildReporteEnBlancoViewModelAsync(tipoFormulario.Value);
            if (vm == null)
            {
                return NotFound("No se encontró el certificado en blanco para el tipo de formulario seleccionado.");
            }

            ViewBag.EsCertificadoEnBlanco = true;
            return View("ReporteImpresion", vm);
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> SubirFirmaEvaluador(Guid id, IFormFile? archivo)
        {
            var evaluador = await GetEvaluadorActualAsync();
            if (evaluador == null)
            {
                return Forbid();
            }

            if (id == Guid.Empty)
            {
                return BadRequest(new { ok = false, message = "Evaluación inválida." });
            }

            if (archivo == null || archivo.Length == 0)
            {
                return BadRequest(new { ok = false, message = "Debes adjuntar una firma." });
            }

            var tipoContenidoFirma = GetTipoContenidoFirma(archivo);
            if (string.IsNullOrWhiteSpace(tipoContenidoFirma))
            {
                return BadRequest(new { ok = false, message = "La firma debe ser una imagen válida en formato PNG o JPG." });
            }

            var evaluacion = await _repo.GetEvaluacionByIdAsync(id);
            if (evaluacion == null)
            {
                return NotFound(new { ok = false, message = "No se encontró la evaluación." });
            }

            var usuario = await _repo.GetUsuarioByIdAsync(evaluacion.UsuarioId);
            if (usuario == null)
            {
                return NotFound(new { ok = false, message = "No se encontró el usuario evaluado." });
            }

            var parteActual = GetParteEvaluador(evaluador, usuario);
            if (!EvaluacionRolesHelper.TieneAcceso(parteActual))
            {
                return Forbid();
            }

            var planesExistentes = await _repo.GetPlanesByEvaluacionAsync(id);

            try
            {
                await using var stream = archivo.OpenReadStream();
                await _repo.UploadFirmaUsuarioAsync(evaluador.Id, archivo.FileName, tipoContenidoFirma, stream);
            }
            catch (Exception ex)
            {
                return BadRequest(new
                {
                    ok = false,
                    message = ex.Message
                });
            }

            var firmaGuardada = await DownloadFirmaUsuarioOrNullAsync(evaluador.Id, "firma recien cargada");
            var quedaBloqueado = TieneFirmaValida(firmaGuardada) && planesExistentes.Any(p => !string.IsNullOrWhiteSpace(p.DescripcionAccion));

            return Json(new
            {
                ok = true,
                firmaDataUrl = ConvertirArchivoADataUrl(firmaGuardada),
                planAccionBloqueado = quedaBloqueado,
                message = "La firma se guardó correctamente."
            });
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> SubirReporteFirmado(Guid id, IFormFile? archivo)
        {
            var evaluador = await GetEvaluadorActualAsync();
            if (evaluador == null)
            {
                return Forbid();
            }

            if (id == Guid.Empty)
            {
                return BadRequest("Evaluación inválida.");
            }

            if (archivo == null || archivo.Length == 0)
            {
                return BadRequest("Debes adjuntar un archivo.");
            }

            var evaluacion = await _repo.GetEvaluacionByIdAsync(id);
            if (evaluacion == null)
            {
                return NotFound();
            }

            if (!await PuedeAccederAEvaluacionAsync(evaluador, evaluacion))
            {
                return Forbid();
            }

            await using var stream = archivo.OpenReadStream();
            await _repo.UploadReporteFirmadoAsync(id, archivo.FileName, archivo.ContentType, stream);

            return Json(new
            {
                ok = true,
                fileName = archivo.FileName,
                downloadUrl = Url.Action(nameof(DescargarReporteFirmado), new { id })
            });
        }

        [HttpGet]
        public async Task<IActionResult> DescargarReporteFirmado(Guid id)
        {
            var evaluador = await GetEvaluadorActualAsync();
            if (evaluador == null)
            {
                return Forbid();
            }

            var evaluacion = await _repo.GetEvaluacionByIdAsync(id);
            if (evaluacion == null)
            {
                return NotFound();
            }

            if (!await PuedeAccederAEvaluacionAsync(evaluador, evaluacion))
            {
                return Forbid();
            }

            if (!evaluacion.TieneReporteFirmado)
            {
                return NotFound("La evaluación no tiene un reporte firmado adjunto.");
            }

            var archivo = await _repo.DownloadReporteFirmadoAsync(id);
            if (archivo == null || archivo.Contenido.Length == 0)
            {
                return NotFound("La evaluación no tiene un reporte firmado adjunto.");
            }

            return File(archivo.Contenido, archivo.TipoContenido, archivo.NombreArchivo);
        }

        [HttpGet]
        public async Task<IActionResult> ExportarExcel([FromQuery] List<Guid> ids)
            => await ExportarExcelCoreAsync(ids);

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> ExportarExcel([FromBody] ExportarExcelRequest request)
            => await ExportarExcelCoreAsync(request?.Ids ?? new List<Guid>());

        private async Task<IActionResult> ExportarExcelCoreAsync(List<Guid> ids)
        {
            var evaluador = await GetEvaluadorActualAsync();
            if (evaluador == null)
            {
                return Forbid();
            }

            if (ids == null || !ids.Any())
            {
                return BadRequest("Debes seleccionar al menos una evaluación.");
            }

            var idsSet = ids.Distinct().ToHashSet();
            var evaluaciones = await GetEvaluacionesVisiblesAsync(evaluador);
            var seleccionadas = evaluaciones
                .Where(e => idsSet.Contains(e.Id))
                .OrderByDescending(e => e.FechaEvaluacion)
                .ToList();

            if (!seleccionadas.Any())
            {
                return NotFound("No se encontraron evaluaciones para exportar.");
            }

            var competencias = await _repo.GetCompetenciasAsync();
            var usuariosDict = new Dictionary<Guid, UsuarioEvaluado>();
            var nivelesDict = new Dictionary<Guid, NivelEvaluacion>();
            var comportamientosPorNivel = new Dictionary<Guid, List<Comportamiento>>();

            using var workbook = new XLWorkbook();
            var worksheet = workbook.Worksheets.Add("Evaluaciones");

            var headers = new[]
            {
                "Fecha evaluación",
                "Próxima evaluación",
                "Nombre",
                "Cédula",
                "Nivel",
                "Código nivel",
                "Tipo evaluación",
                "Resultado final (%)",
                "Total evaluador normal (%)",
                "Total SST (%)"
            };

            for (var i = 0; i < headers.Length; i++)
            {
                var cell = worksheet.Cell(1, i + 1);
                cell.Value = headers[i];
                cell.Style.Font.Bold = true;
                cell.Style.Fill.BackgroundColor = XLColor.FromHtml("#DCE6F1");
                cell.Style.Alignment.Horizontal = XLAlignmentHorizontalValues.Center;
                cell.Style.Alignment.Vertical = XLAlignmentVerticalValues.Center;
            }

            var row = 2;
            foreach (var evaluacion in seleccionadas)
            {
                if (!usuariosDict.TryGetValue(evaluacion.UsuarioId, out var usuario))
                {
                    usuario = await _repo.GetUsuarioByIdAsync(evaluacion.UsuarioId) ?? new UsuarioEvaluado();
                    usuariosDict[evaluacion.UsuarioId] = usuario;
                }

                if (!nivelesDict.TryGetValue(evaluacion.NivelId, out var nivel))
                {
                    nivel = await _repo.GetNivelByIdAsync(evaluacion.NivelId) ?? new NivelEvaluacion();
                    nivelesDict[evaluacion.NivelId] = nivel;
                }

                if (!comportamientosPorNivel.TryGetValue(evaluacion.NivelId, out var comportamientos))
                {
                    comportamientos = await _repo.GetComportamientosByNivelAsync(evaluacion.NivelId);
                    comportamientosPorNivel[evaluacion.NivelId] = comportamientos;
                }

                var detalles = await _repo.GetDetallesByEvaluacionAsync(evaluacion.Id);
                var cobertura = BuildCobertura(detalles, competencias, comportamientos);

                worksheet.Cell(row, 1).Value = evaluacion.FechaEvaluacion;
                worksheet.Cell(row, 1).Style.DateFormat.Format = "dd/MM/yyyy";

                if (evaluacion.FechaProximaEvaluacion.HasValue)
                {
                    worksheet.Cell(row, 2).Value = evaluacion.FechaProximaEvaluacion.Value;
                    worksheet.Cell(row, 2).Style.DateFormat.Format = "dd/MM/yyyy";
                }

                worksheet.Cell(row, 3).Value = usuario.NombreCompleto ?? string.Empty;
                worksheet.Cell(row, 4).Value = usuario.Cedula ?? string.Empty;
                worksheet.Cell(row, 5).Value = nivel.Nombre ?? string.Empty;
                worksheet.Cell(row, 6).Value = nivel.Codigo ?? string.Empty;
                worksheet.Cell(row, 7).Value = evaluacion.TipoEvaluacion ?? string.Empty;
                worksheet.Cell(row, 8).Value = cobertura.AmbasPartesCompletas
                    ? evaluacion.Total ?? cobertura.TotalCalculado
                    : null;
                worksheet.Cell(row, 8).Style.NumberFormat.Format = "0.00";
                worksheet.Cell(row, 9).Value = cobertura.TotalEvaluacionNormalCalculado;
                worksheet.Cell(row, 9).Style.NumberFormat.Format = "0.00";
                worksheet.Cell(row, 10).Value = cobertura.TotalEvaluacionSstCalculado;
                worksheet.Cell(row, 10).Style.NumberFormat.Format = "0.00";

                row++;
            }

            var usedRange = worksheet.Range(1, 1, row - 1, headers.Length);
            usedRange.Style.Border.OutsideBorder = XLBorderStyleValues.Thin;
            usedRange.Style.Border.InsideBorder = XLBorderStyleValues.Thin;
            usedRange.Style.Alignment.Vertical = XLAlignmentVerticalValues.Top;

            worksheet.SheetView.FreezeRows(1);
            worksheet.Range(1, 1, 1, headers.Length).SetAutoFilter();
            worksheet.Columns().AdjustToContents();

            worksheet.Column(3).Width = Math.Min(worksheet.Column(3).Width + 5, 45);
            worksheet.Column(5).Width = Math.Min(worksheet.Column(5).Width + 4, 35);
            worksheet.Column(7).Width = Math.Min(worksheet.Column(7).Width + 3, 25);
            worksheet.Column(8).Width = Math.Max(worksheet.Column(8).Width, 18);
            worksheet.Column(9).Width = Math.Max(worksheet.Column(9).Width, 22);
            worksheet.Column(10).Width = Math.Max(worksheet.Column(10).Width, 18);

            using var stream = new MemoryStream();
            workbook.SaveAs(stream);
            stream.Position = 0;

            var fileName = $"Evaluaciones_{DateTime.Now:yyyyMMdd_HHmm}.xlsx";
            const string contentType = "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet";
            return File(stream.ToArray(), contentType, fileName);
        }

        // ================== GUARDAR PLAN DE ACCIÓN ==================

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> GuardarPlanAccion(EvaluacionReporteViewModel model, IFormFile? firmaArchivo)
        {
            var evaluador = await GetEvaluadorActualAsync();
            if (evaluador == null)
            {
                return Forbid();
            }

            await _repo.HabilitarUsuariosProgramadosAsync(DateTime.Today);
            var evaluacion = await _repo.GetEvaluacionByIdAsync(model.EvaluacionId);
            if (evaluacion == null)
            {
                return NotFound();
            }

            var usuario = await _repo.GetUsuarioByIdAsync(evaluacion.UsuarioId);
            if (usuario == null)
            {
                return NotFound();
            }

            if (!PuedeAccederAUsuario(evaluador, usuario))
            {
                return Forbid();
            }

            var parteActual = GetParteEvaluador(evaluador, usuario);
            if (!EvaluacionRolesHelper.TieneAcceso(parteActual))
            {
                return EvaluadorSinBloqueAsignado();
            }

            if (!usuario.Habilitado)
            {
                return UsuarioNoHabilitado();
            }

            var competencias = await _repo.GetCompetenciasAsync();
            var comportamientos = await _repo.GetComportamientosByNivelAsync(evaluacion.NivelId);
            var competenciasById = BuildCompetenciasLookup(competencias);
            var comportamientosParteActual = GetComportamientosPermitidos(parteActual, comportamientos, competenciasById);
            var comportamientosPorDescripcion = BuildComportamientosPorDescripcion(comportamientos);

            var detalles = await _repo.GetDetallesByEvaluacionAsync(model.EvaluacionId);
            var planesExistentes = await _repo.GetPlanesByEvaluacionAsync(model.EvaluacionId);
            var firmaActual = await DownloadFirmaUsuarioOrNullAsync(evaluador.Id, "guardar plan de accion");
            var tieneFirmaActualValida = TieneFirmaValida(firmaActual);
            var planAccionBloqueado = tieneFirmaActualValida &&
                                      TienePlanAccionRegistrado(
                                          planesExistentes,
                                          comportamientosParteActual,
                                          comportamientosPorDescripcion);

            if (planAccionBloqueado)
            {
                return await ReturnReporteConErrorGuardadoPlanAsync(
                    model,
                    evaluador,
                    "El plan de acción ya fue firmado y no admite cambios.");
            }

            if (!tieneFirmaActualValida && (firmaArchivo == null || firmaArchivo.Length == 0))
            {
                return await ReturnReporteConErrorGuardadoPlanAsync(
                    model,
                    evaluador,
                    "Debes adjuntar una firma válida en formato PNG o JPG para guardar el plan de acción.");
            }

            if (firmaArchivo != null && firmaArchivo.Length > 0)
            {
                var tipoContenidoFirma = GetTipoContenidoFirma(firmaArchivo);
                if (string.IsNullOrWhiteSpace(tipoContenidoFirma))
                {
                    return await ReturnReporteConErrorGuardadoPlanAsync(
                        model,
                        evaluador,
                        "La firma debe ser una imagen válida en formato PNG o JPG.");
                }

                try
                {
                    await using var stream = firmaArchivo.OpenReadStream();
                    await _repo.UploadFirmaUsuarioAsync(evaluador.Id, firmaArchivo.FileName, tipoContenidoFirma, stream);
                }
                catch (Exception ex)
                {
                    return await ReturnReporteConErrorGuardadoPlanAsync(model, evaluador, ex.Message);
                }
            }

            var comportamientosPermitidos = comportamientos
                .Where(c =>
                {
                    if (!comportamientosParteActual.Contains(c.Id))
                    {
                        return false;
                    }

                    var detalle = detalles.FirstOrDefault(d => d.ComportamientoId == c.Id);
                    return EsPuntajeOportunidadMejora(detalle?.Puntaje);
                })
                .ToDictionary(c => c.Id, c => c);

            var planesActualizados = (model.PlanAccion ?? new List<PlanAccionItemVm>())
                .Where(p =>
                    p.ComportamientoId.HasValue &&
                    comportamientosPermitidos.ContainsKey(p.ComportamientoId.Value) &&
                    !string.IsNullOrWhiteSpace(p.Descripcion))
                .Select(p =>
                {
                    var comportamiento = comportamientosPermitidos[p.ComportamientoId!.Value];
                    return new PlanAccion
                    {
                        Id = p.Id ?? Guid.NewGuid(),
                        EvaluacionId = model.EvaluacionId,
                        ComportamientoId = comportamiento.Id,
                        ComportamientoNombre = comportamiento.Descripcion,
                        DescripcionAccion = p.Descripcion!,
                        Responsable = comportamiento.Descripcion
                    };
                })
                .ToList();

            var planesFinales = MergePlanes(
                planesExistentes,
                planesActualizados,
                comportamientosParteActual,
                comportamientosPorDescripcion);

            var cobertura = BuildCobertura(detalles, competencias, comportamientos);
            evaluacion.Total = cobertura.TotalCalculado;
            evaluacion.Estado = cobertura.AmbasPartesCompletas ? "Finalizada" : evaluacion.Estado;

            await _repo.UpdateEvaluacionAsync(evaluacion, detalles, planesFinales);

            return RedirectToAction(nameof(PlanAccion), new { id = model.EvaluacionId });
        }

        private async Task<EvaluacionReporteViewModel?> BuildReporteEnBlancoViewModelAsync(int tipoFormulario)
        {
            var niveles = await _repo.GetNivelesActivosAsync();
            var nivel = ResolveNivelPorTipoFormulario(tipoFormulario, niveles);
            if (nivel == null)
            {
                return null;
            }

            var competencias = await _repo.GetCompetenciasAsync();
            var comportamientos = await _repo.GetComportamientosByNivelAsync(nivel.Id);
            var competenciasVm = new List<CompetenciaReporteVm>();

            foreach (var competencia in competencias.OrderBy(c => c.Orden))
            {
                var comportamientosCompetencia = comportamientos
                    .Where(x => x.CompetenciaId == competencia.Id)
                    .OrderBy(x => x.Orden)
                    .Select(x => new ComportamientoReporteVm
                    {
                        Descripcion = x.Descripcion
                    })
                    .ToList();

                if (!comportamientosCompetencia.Any())
                {
                    continue;
                }

                competenciasVm.Add(new CompetenciaReporteVm
                {
                    Nombre = competencia.Nombre,
                    Comportamientos = comportamientosCompetencia
                });
            }

            return new EvaluacionReporteViewModel
            {
                FechaGeneracionReporte = DateTime.Today,
                NombreNivel = nivel.Nombre,
                TipoFormularioNombre = GetTipoFormularioNombre(tipoFormulario),
                Competencias = competenciasVm,
                OportunidadesMejora = new List<OportunidadMejoraVm>(),
                PlanAccion = new List<PlanAccionItemVm>()
            };
        }

        private async Task<EvaluacionReporteViewModel?> BuildReporteViewModelAsync(Guid id, UsuarioEvaluado evaluadorActual)
        {
            var evaluacion = await _repo.GetEvaluacionByIdAsync(id);
            if (evaluacion == null)
            {
                return null;
            }

            var usuario = await _repo.GetUsuarioByIdAsync(evaluacion.UsuarioId);
            var nivel = await _repo.GetNivelByIdAsync(evaluacion.NivelId);
            if (usuario == null || nivel == null)
            {
                return null;
            }

            if (!PuedeAccederAUsuario(evaluadorActual, usuario))
            {
                return null;
            }

            var detalles = await _repo.GetDetallesByEvaluacionAsync(id);
            var planes = await _repo.GetPlanesByEvaluacionAsync(id);
            var competencias = await _repo.GetCompetenciasAsync();
            var competenciasById = BuildCompetenciasLookup(competencias);
            var comportamientos = await _repo.GetComportamientosByNivelAsync(evaluacion.NivelId);
            var comportamientosPorDescripcion = BuildComportamientosPorDescripcion(comportamientos);
            var parteActual = GetParteEvaluador(evaluadorActual, usuario);
            var puedeDiligenciarParte = EvaluacionRolesHelper.TieneAcceso(parteActual);
            var partePlanVisible = puedeDiligenciarParte
                ? parteActual
                : evaluadorActual.EsSuperAdministrador
                    ? TipoParteEvaluacion.Normal | TipoParteEvaluacion.Sst
                    : TipoParteEvaluacion.Ninguna;
            var comportamientosPlanVisible = GetComportamientosPermitidos(partePlanVisible, comportamientos, competenciasById);
            var cobertura = BuildCobertura(detalles, competencias, comportamientos);

            var competenciasVm = new List<CompetenciaReporteVm>();

            foreach (var competencia in competencias.OrderBy(c => c.Orden))
            {
                var comportamientosCompetencia = comportamientos
                    .Where(x => x.CompetenciaId == competencia.Id)
                    .OrderBy(x => x.Orden)
                    .ToList();

                if (!comportamientosCompetencia.Any())
                {
                    continue;
                }

                var compVm = new CompetenciaReporteVm
                {
                    Nombre = competencia.Nombre
                };

                var puntajes = new List<int>();

                foreach (var comportamiento in comportamientosCompetencia)
                {
                    var detalle = detalles.FirstOrDefault(d => d.ComportamientoId == comportamiento.Id);
                    if (detalle != null)
                    {
                        puntajes.Add(detalle.Puntaje);
                    }

                    compVm.Comportamientos.Add(new ComportamientoReporteVm
                    {
                        Descripcion = comportamiento.Descripcion,
                        Puntaje = detalle?.Puntaje,
                        Comentario = detalle?.Comentario
                    });
                }

                if (puntajes.Any())
                {
                    compVm.Promedio = Math.Round(puntajes.Select(p => (decimal)p).Average(), 2);
                    competenciasVm.Add(compVm);
                }
            }

            var oportunidades = competenciasVm
                .SelectMany(comp => comp.Comportamientos
                    .Where(c => EsPuntajeOportunidadMejora(c.Puntaje))
                    .Select(c => new OportunidadMejoraVm
                    {
                        Competencia = comp.Nombre,
                        Comportamiento = c.Descripcion,
                        Puntaje = c.Puntaje!.Value
                    }))
                .ToList();

            var planOptions = puedeDiligenciarParte
                ? BuildPlanOptions(parteActual, competencias, comportamientos, detalles)
                : new List<PlanAccionOpcionVm>();
            var planVm = planes
                .Where(p => PlanPerteneceAParte(p, comportamientosPlanVisible, comportamientosPorDescripcion))
                .Select(p => new PlanAccionItemVm
                {
                    Id = p.Id,
                    ComportamientoId = ResolveComportamientoId(p, comportamientosPorDescripcion),
                    Comportamiento = p.ComportamientoNombre ?? p.Responsable,
                    Descripcion = p.DescripcionAccion
                })
                .ToList();

            if (!planVm.Any())
            {
                planVm.Add(new PlanAccionItemVm());
            }

            var evaluadorPrincipal = await ResolveUsuarioPorCorreosAsync(usuario.CorreoEvaluador, usuario.EvaluadorNombre);
            var evaluadorSst = await ResolveUsuarioPorCorreosAsync(usuario.CorreoEvaluadorSst);
            var firmaEvaluador = evaluadorPrincipal == null
                ? null
                : await DownloadFirmaUsuarioOrNullAsync(evaluadorPrincipal.Id, $"reporte evaluador principal {evaluacion.Id}");
            var firmaEvaluadorSst = evaluadorSst == null
                ? null
                : await DownloadFirmaUsuarioOrNullAsync(evaluadorSst.Id, $"reporte evaluador SST {evaluacion.Id}");
            var firmaActual = puedeDiligenciarParte
                ? await DownloadFirmaUsuarioOrNullAsync(evaluadorActual.Id, $"reporte evaluador actual {evaluacion.Id}")
                : null;
            var etiquetaFirmaActual = GetEtiquetaFirmaParaParte(parteActual);
            var planAccionBloqueado = puedeDiligenciarParte &&
                                      etiquetaFirmaActual != null &&
                                      TieneFirmaValida(firmaActual) &&
                                      TienePlanAccionRegistrado(planVm);

            return new EvaluacionReporteViewModel
            {
                EvaluacionId = evaluacion.Id,
                NombreUsuario = usuario.NombreCompleto,
                CedulaUsuario = usuario.Cedula,
                Cargo = usuario.Cargo,
                Gerencia = evaluacion.Gerencia ?? usuario.Gerencia,
                Proyecto = evaluacion.Proyecto ?? usuario.Proyecto,
                TipoFormularioNombre = usuario.TipoFormulario.HasValue
                    ? GetTipoFormularioNombre(usuario.TipoFormulario.Value)
                    : null,
                NombreJefeInmediatoOEvaluador = evaluadorPrincipal?.NombreCompleto
                    ?? usuario.CorreoEvaluador
                    ?? usuario.EvaluadorNombre,
                CargoJefeInmediatoOEvaluador = evaluadorPrincipal?.Cargo ?? usuario.CargoJefeInmediato,
                NombreEvaluadorSst = evaluadorSst?.NombreCompleto ?? usuario.NombreEvaluadorSst ?? usuario.CorreoEvaluadorSst,
                CargoEvaluadorSst = evaluadorSst?.Cargo ?? usuario.CargoEvaluadorSst,
                FechaIngreso = usuario.FechaIngreso,
                FechaGeneracionReporte = DateTime.Today,
                FechaEvaluacion = evaluacion.FechaEvaluacion,
                TipoEvaluacion = evaluacion.TipoEvaluacion,
                NombreNivel = nivel.Nombre,
                AlcanceEvaluadorActual = GetEtiquetaAlcance(parteActual, evaluadorActual.EsSuperAdministrador),
                PromedioGeneral = cobertura.AmbasPartesCompletas
                    ? evaluacion.Total ?? cobertura.TotalCalculado
                    : null,
                EvaluacionNormalCompleta = cobertura.EvaluacionNormalCompleta,
                EvaluacionSstCompleta = cobertura.EvaluacionSstCompleta,
                Competencias = competenciasVm,
                OportunidadesMejora = oportunidades,
                PlanAccion = planVm,
                OpcionesPlanAccion = planOptions,
                ObservacionesGenerales = evaluacion.Observaciones,
                FirmaEvaluadorDataUrl = ConvertirArchivoADataUrl(firmaEvaluador),
                FirmaEvaluadorSstDataUrl = ConvertirArchivoADataUrl(firmaEvaluadorSst),
                FirmaActualDataUrl = ConvertirArchivoADataUrl(firmaActual),
                PuedeEditarPlanAccion = puedeDiligenciarParte && !planAccionBloqueado,
                PuedeAdjuntarFirmaActual = puedeDiligenciarParte &&
                                           etiquetaFirmaActual != null,
                EtiquetaFirmaActual = etiquetaFirmaActual,
                PlanAccionBloqueado = planAccionBloqueado,
                FechaProximaEvaluacion = evaluacion.FechaProximaEvaluacion
            };
        }
    }
}
