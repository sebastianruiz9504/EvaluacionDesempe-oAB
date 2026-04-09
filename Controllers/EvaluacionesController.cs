using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Claims;
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

        private static readonly Dictionary<int, (string Codigo, string Nombre)> TipoFormularioNiveles = new()
        {
            { 433930001, ("OPEADM", "Operativo Administrativo") },
            { 433930000, ("TACT", "Táctico") },
            { 433930003, ("ESTR", "Estratégico") },
            { 433930002, ("OPE", "Operativo") }
        };

        private sealed class EvaluacionCoberturaInfo
        {
            public bool EvaluacionNormalCompleta { get; init; }
            public bool EvaluacionSstCompleta { get; init; }
            public decimal? TotalCalculado { get; init; }
            public bool AmbasPartesCompletas => EvaluacionNormalCompleta && EvaluacionSstCompleta;
        }

        private const int OportunidadMejoraPuntajeMinimo = 70;
        private const int OportunidadMejoraPuntajeMaximo = 85;

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

            var tipoContenido = string.IsNullOrWhiteSpace(archivo.TipoContenido)
                ? "application/octet-stream"
                : archivo.TipoContenido;

            return $"data:{tipoContenido};base64,{Convert.ToBase64String(archivo.Contenido)}";
        }

        private static bool TieneContenido(ArchivoEvaluacion? archivo)
            => archivo != null && archivo.Contenido.Length > 0;

        private static bool EsPuntajeOportunidadMejora(int? puntaje)
            => puntaje.HasValue &&
               puntaje.Value >= OportunidadMejoraPuntajeMinimo &&
               puntaje.Value <= OportunidadMejoraPuntajeMaximo;

        private static bool EsFirmaImagenValida(IFormFile archivo)
        {
            var contentTypeValido =
                string.Equals(archivo.ContentType, "image/png", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(archivo.ContentType, "image/jpeg", StringComparison.OrdinalIgnoreCase);

            if (contentTypeValido)
            {
                return true;
            }

            var extension = Path.GetExtension(archivo.FileName);
            return string.Equals(extension, ".png", StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(extension, ".jpg", StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(extension, ".jpeg", StringComparison.OrdinalIgnoreCase);
        }

        private static string? GetEtiquetaFirmaParaParte(TipoParteEvaluacion parte)
        {
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

            ViewBag.MostrarModalGuardarPlan = true;
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
                GetCorreoActual(evaluadorActual),
                evaluadorActual.EsSuperAdministrador);

        private bool PuedeAccederAUsuario(UsuarioEvaluado evaluadorActual, UsuarioEvaluado usuarioObjetivo)
            => evaluadorActual.EsSuperAdministrador || EvaluacionRolesHelper.TieneAcceso(GetParteEvaluador(evaluadorActual, usuarioObjetivo));

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

        private async Task<Evaluacion?> GetEvaluacionEnCursoAsync(Guid usuarioId)
        {
            var evaluaciones = await _repo.GetEvaluacionesByUsuarioAsync(usuarioId);
            if (!evaluaciones.Any())
            {
                return null;
            }

            var competencias = await _repo.GetCompetenciasAsync();
            var comportamientosPorNivel = new Dictionary<Guid, List<Comportamiento>>();

            foreach (var evaluacion in evaluaciones.OrderByDescending(e => e.FechaEvaluacion))
            {
                if (!comportamientosPorNivel.TryGetValue(evaluacion.NivelId, out var comportamientos))
                {
                    comportamientos = await _repo.GetComportamientosByNivelAsync(evaluacion.NivelId);
                    comportamientosPorNivel[evaluacion.NivelId] = comportamientos;
                }

                var detalles = await _repo.GetDetallesByEvaluacionAsync(evaluacion.Id);
                var cobertura = BuildCobertura(detalles, competencias, comportamientos);
                if (!cobertura.AmbasPartesCompletas)
                {
                    return evaluacion;
                }
            }

            return null;
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
                        ProyectoUsuario = evaluacion.Proyecto ?? string.Empty,
                        NivelNombre = nivel.Nombre,
                        NivelCodigo = nivel.Codigo,
                        Proyecto = evaluacion.Proyecto ?? usuario.Cargo,
                        Gerencia = evaluacion.Gerencia ?? usuario.Gerencia,
                        TipoEvaluacion = evaluacion.TipoEvaluacion,
                        ResultadoFinal = cobertura.AmbasPartesCompletas
                            ? evaluacion.Total ?? cobertura.TotalCalculado
                            : null,
                        EvaluacionNormalCompleta = cobertura.EvaluacionNormalCompleta,
                        EvaluacionSstCompleta = cobertura.EvaluacionSstCompleta,
                        PuedeReevaluar = evaluacion.TipoEvaluacion == "Inicial",
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

        // ================== NUEVA EVALUACIÓN ==================

        [HttpGet]
        public async Task<IActionResult> Nueva(Guid usuarioId)
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

            var evaluacionEnCurso = await GetEvaluacionEnCursoAsync(usuarioId);
            if (evaluacionEnCurso != null)
            {
                return RedirectToAction(nameof(Editar), new { id = evaluacionEnCurso.Id });
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

            var evaluacionesUsuario = await _repo.GetEvaluacionesByUsuarioAsync(usuarioId);
            if (evaluacionOrigenId.HasValue)
            {
                var seguimientoExistente = evaluacionesUsuario
                    .OrderByDescending(e => e.FechaEvaluacion)
                    .FirstOrDefault(e => e.EvaluacionOrigenId == evaluacionOrigenId);

                if (seguimientoExistente != null)
                {
                    return RedirectToAction(nameof(Editar), new { id = seguimientoExistente.Id });
                }
            }

            var competencias = await _repo.GetCompetenciasAsync();
            var competenciasById = BuildCompetenciasLookup(competencias);
            var comportamientos = await _repo.GetComportamientosByNivelAsync(nivelId);
            var parteActual = GetParteEvaluador(evaluador, usuario);
            var comportamientosPermitidos = FilterComportamientosPermitidos(parteActual, comportamientos, competenciasById);

            var vm = new EvaluacionFormularioViewModel
            {
                UsuarioId = usuario.Id,
                NivelId = nivel.Id,
                NombreUsuario = usuario.NombreCompleto,
                CedulaUsuario = usuario.Cedula,
                Cargo = usuario.Cargo,
                Gerencia = usuario.Gerencia,
                NombreNivel = nivel.Nombre,
                AlcanceEvaluadorActual = EvaluacionRolesHelper.GetEtiquetaParte(parteActual),
                TipoEvaluacion = evaluacionOrigenId.HasValue ? "Seguimiento" : "Inicial",
                EvaluacionOrigenId = evaluacionOrigenId,
                FechaEvaluacion = DateTime.Today
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
                    compVm.Comportamientos.Add(new ComportamientoEvaluacionVm
                    {
                        ComportamientoId = comportamiento.Id,
                        Descripcion = comportamiento.Descripcion
                    });
                }

                if (compVm.Comportamientos.Any())
                {
                    vm.Competencias.Add(compVm);
                }
            }

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

            var detalles = await _repo.GetDetallesByEvaluacionAsync(id);
            var competencias = await _repo.GetCompetenciasAsync();
            var competenciasById = BuildCompetenciasLookup(competencias);
            var comportamientos = await _repo.GetComportamientosByNivelAsync(evaluacion.NivelId);
            var parteActual = GetParteEvaluador(evaluador, usuario);
            var comportamientosPermitidos = FilterComportamientosPermitidos(parteActual, comportamientos, competenciasById);
            var cobertura = BuildCobertura(detalles, competencias, comportamientos);

            var vm = new EvaluacionFormularioViewModel
            {
                Id = evaluacion.Id,
                UsuarioId = usuario.Id,
                NivelId = nivel.Id,
                NombreUsuario = usuario.NombreCompleto,
                CedulaUsuario = usuario.Cedula,
                Cargo = usuario.Cargo,
                Gerencia = usuario.Gerencia,
                NombreNivel = nivel.Nombre,
                AlcanceEvaluadorActual = EvaluacionRolesHelper.GetEtiquetaParte(parteActual),
                EvaluacionNormalCompleta = cobertura.EvaluacionNormalCompleta,
                EvaluacionSstCompleta = cobertura.EvaluacionSstCompleta,
                FechaEvaluacion = evaluacion.FechaEvaluacion,
                ObservacionesGenerales = evaluacion.Observaciones,
                TipoEvaluacion = evaluacion.TipoEvaluacion,
                EvaluacionOrigenId = evaluacion.EvaluacionOrigenId
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

            var competencias = await _repo.GetCompetenciasAsync();
            var competenciasById = BuildCompetenciasLookup(competencias);
            var comportamientos = await _repo.GetComportamientosByNivelAsync(model.NivelId);
            var parteActual = GetParteEvaluador(evaluador, usuario);
            var comportamientosParteActual = GetComportamientosPermitidos(parteActual, comportamientos, competenciasById);

            model.AlcanceEvaluadorActual = EvaluacionRolesHelper.GetEtiquetaParte(parteActual);

            if (!ModelState.IsValid)
            {
                return View("Formulario", model);
            }

            Evaluacion? evaluacionExistente = null;
            if (model.Id.HasValue)
            {
                evaluacionExistente = await _repo.GetEvaluacionByIdAsync(model.Id.Value);
            }
            else if (model.EvaluacionOrigenId.HasValue)
            {
                evaluacionExistente = (await _repo.GetEvaluacionesByUsuarioAsync(model.UsuarioId))
                    .OrderByDescending(e => e.FechaEvaluacion)
                    .FirstOrDefault(e => e.EvaluacionOrigenId == model.EvaluacionOrigenId);
            }
            else
            {
                evaluacionExistente = await GetEvaluacionEnCursoAsync(model.UsuarioId);
            }

            var detallesExistentes = evaluacionExistente != null
                ? await _repo.GetDetallesByEvaluacionAsync(evaluacionExistente.Id)
                : new List<EvaluacionDetalle>();

            var planesExistentes = evaluacionExistente != null
                ? await _repo.GetPlanesByEvaluacionAsync(evaluacionExistente.Id)
                : new List<PlanAccion>();

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

            var detallesFinales = MergeDetalles(detallesExistentes, nuevosDetalles, comportamientosParteActual);
            var cobertura = BuildCobertura(detallesFinales, competencias, comportamientos);

            var fechaProxima = evaluacionExistente?.FechaProximaEvaluacion
                               ?? (model.TipoEvaluacion == "Inicial"
                                   ? model.FechaEvaluacion.AddMonths(6)
                                   : null);

            var observaciones = string.IsNullOrWhiteSpace(model.ObservacionesGenerales)
                ? evaluacionExistente?.Observaciones
                : model.ObservacionesGenerales;

            var evaluacion = new Evaluacion
            {
                Id = evaluacionExistente?.Id ?? model.Id ?? Guid.NewGuid(),
                UsuarioId = model.UsuarioId,
                NivelId = model.NivelId,
                FechaEvaluacion = evaluacionExistente != null
                    ? evaluacionExistente.FechaEvaluacion
                    : model.FechaEvaluacion,
                TipoEvaluacion = model.TipoEvaluacion,
                EvaluacionOrigenId = model.EvaluacionOrigenId,
                Observaciones = observaciones,
                Estado = cobertura.AmbasPartesCompletas
                    ? "Finalizada"
                    : (accion == "finalizar" ? "Parcial" : "Borrador"),
                FechaProximaEvaluacion = fechaProxima,
                EvaluadorNombre = usuario.EvaluadorNombre ?? evaluacionExistente?.EvaluadorNombre ?? GetCorreoActual(evaluador),
                Proyecto = model.Cargo,
                Gerencia = model.Gerencia,
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

            if (accion == "finalizar")
            {
                return RedirectToAction(nameof(Reporte), new { id = evaluacion.Id });
            }

            return RedirectToAction(nameof(Index));
        }

        // ================== REEVALUAR (SEGUIMIENTO) ==================

        [HttpGet]
        public async Task<IActionResult> Reevaluar(Guid id)
        {
            var evaluador = await GetEvaluadorActualAsync();
            if (evaluador == null)
            {
                return Forbid();
            }

            var evaluacionOriginal = await _repo.GetEvaluacionByIdAsync(id);
            if (evaluacionOriginal == null)
            {
                return NotFound();
            }

            if (!await PuedeAccederAEvaluacionAsync(evaluador, evaluacionOriginal))
            {
                return Forbid();
            }

            var seguimientoExistente = (await _repo.GetEvaluacionesByUsuarioAsync(evaluacionOriginal.UsuarioId))
                .OrderByDescending(e => e.FechaEvaluacion)
                .FirstOrDefault(e => e.EvaluacionOrigenId == evaluacionOriginal.Id);

            if (seguimientoExistente != null)
            {
                return RedirectToAction(nameof(Editar), new { id = seguimientoExistente.Id });
            }

            return RedirectToAction(nameof(Formulario), new
            {
                usuarioId = evaluacionOriginal.UsuarioId,
                nivelId = evaluacionOriginal.NivelId,
                evaluacionOrigenId = evaluacionOriginal.Id
            });
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
                    ProyectoUsuario = evaluacion.Proyecto ?? string.Empty,
                    NivelNombre = nivel.Nombre,
                    NivelCodigo = nivel.Codigo,
                    Proyecto = evaluacion.Proyecto ?? usuario.Cargo,
                    Gerencia = evaluacion.Gerencia ?? usuario.Gerencia,
                    TipoEvaluacion = evaluacion.TipoEvaluacion,
                    ResultadoFinal = cobertura.AmbasPartesCompletas
                        ? evaluacion.Total ?? cobertura.TotalCalculado
                        : null,
                    EvaluacionNormalCompleta = cobertura.EvaluacionNormalCompleta,
                    EvaluacionSstCompleta = cobertura.EvaluacionSstCompleta,
                    PuedeReevaluar = evaluacion.TipoEvaluacion == "Inicial",
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
                return NotFound("No se encontro el certificado en blanco para el tipo de formulario seleccionado.");
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

            if (!EsFirmaImagenValida(archivo))
            {
                return BadRequest(new { ok = false, message = "La firma debe estar en formato PNG o JPG." });
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
            if (evaluador.EsSuperAdministrador || !EvaluacionRolesHelper.TieneAcceso(parteActual))
            {
                return Forbid();
            }

            try
            {
                await using var stream = archivo.OpenReadStream();
                await _repo.UploadFirmaUsuarioAsync(evaluador.Id, archivo.FileName, archivo.ContentType, stream);
            }
            catch (Exception ex)
            {
                return BadRequest(new
                {
                    ok = false,
                    message = ex.Message
                });
            }

            return Json(new
            {
                ok = true,
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

            var evaluaciones = await GetEvaluacionesVisiblesAsync(evaluador);
            var seleccionadas = evaluaciones
                .Where(e => ids.Contains(e.Id))
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
                "Resultado final (%)"
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
            var requiereFirma = !evaluador.EsSuperAdministrador && EvaluacionRolesHelper.TieneAcceso(parteActual);
            if (requiereFirma)
            {
                if (firmaArchivo == null || firmaArchivo.Length == 0)
                {
                    return await ReturnReporteConErrorGuardadoPlanAsync(
                        model,
                        evaluador,
                        "Debes adjuntar la firma en formato PNG o JPG para guardar el plan de acción.");
                }

                if (!EsFirmaImagenValida(firmaArchivo))
                {
                    return await ReturnReporteConErrorGuardadoPlanAsync(
                        model,
                        evaluador,
                        "La firma debe estar en formato PNG o JPG.");
                }

                try
                {
                    await using var stream = firmaArchivo.OpenReadStream();
                    await _repo.UploadFirmaUsuarioAsync(evaluador.Id, firmaArchivo.FileName, firmaArchivo.ContentType, stream);
                }
                catch (Exception ex)
                {
                    return await ReturnReporteConErrorGuardadoPlanAsync(model, evaluador, ex.Message);
                }
            }

            var competencias = await _repo.GetCompetenciasAsync();
            var comportamientos = await _repo.GetComportamientosByNivelAsync(evaluacion.NivelId);
            var competenciasById = BuildCompetenciasLookup(competencias);
            var comportamientosParteActual = GetComportamientosPermitidos(parteActual, comportamientos, competenciasById);
            var comportamientosPorDescripcion = BuildComportamientosPorDescripcion(comportamientos);

            var detalles = await _repo.GetDetallesByEvaluacionAsync(model.EvaluacionId);
            var planesExistentes = await _repo.GetPlanesByEvaluacionAsync(model.EvaluacionId);
            var firmaActual = requiereFirma
                ? await _repo.DownloadFirmaUsuarioAsync(evaluador.Id)
                : null;
            var planAccionBloqueado = requiereFirma &&
                                      TieneContenido(firmaActual) &&
                                      TienePlanAccionRegistrado(
                                          planesExistentes,
                                          comportamientosParteActual,
                                          comportamientosPorDescripcion);

            if (planAccionBloqueado)
            {
                return await ReturnReporteConErrorGuardadoPlanAsync(
                    model,
                    evaluador,
                    "El plan de accion ya fue firmado y no admite cambios.");
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

            var planesActualizados = model.PlanAccion
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

            return RedirectToAction(nameof(Reporte), new { id = model.EvaluacionId });
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
            var comportamientosParteActual = GetComportamientosPermitidos(parteActual, comportamientos, competenciasById);
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

            var planOptions = BuildPlanOptions(parteActual, competencias, comportamientos, detalles);
            var planVm = planes
                .Where(p => PlanPerteneceAParte(p, comportamientosParteActual, comportamientosPorDescripcion))
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

            var evaluadorPrincipal = await ResolveUsuarioPorCorreosAsync(usuario.EvaluadorNombre, usuario.CorreoEvaluador);
            var evaluadorSst = await ResolveUsuarioPorCorreosAsync(usuario.CorreoEvaluadorSst);
            var firmaEvaluador = evaluadorPrincipal == null
                ? null
                : await _repo.DownloadFirmaUsuarioAsync(evaluadorPrincipal.Id);
            var firmaEvaluadorSst = evaluadorSst == null
                ? null
                : await _repo.DownloadFirmaUsuarioAsync(evaluadorSst.Id);
            var firmaActual = !evaluadorActual.EsSuperAdministrador && EvaluacionRolesHelper.TieneAcceso(parteActual)
                ? await _repo.DownloadFirmaUsuarioAsync(evaluadorActual.Id)
                : null;
            var etiquetaFirmaActual = GetEtiquetaFirmaParaParte(parteActual);
            var planAccionBloqueado = etiquetaFirmaActual != null &&
                                      TieneContenido(firmaActual) &&
                                      TienePlanAccionRegistrado(planVm);

            return new EvaluacionReporteViewModel
            {
                EvaluacionId = evaluacion.Id,
                NombreUsuario = usuario.NombreCompleto,
                CedulaUsuario = usuario.Cedula,
                Cargo = usuario.Cargo,
                Gerencia = evaluacion.Gerencia ?? usuario.Gerencia,
                Proyecto = evaluacion.Proyecto,
                NombreJefeInmediatoOEvaluador = string.IsNullOrWhiteSpace(usuario.CorreoEvaluador)
                    ? evaluadorPrincipal?.NombreCompleto ?? usuario.EvaluadorNombre
                    : usuario.CorreoEvaluador,
                CargoJefeInmediatoOEvaluador = usuario.CargoJefeInmediato,
                NombreEvaluadorSst = evaluadorSst?.NombreCompleto ?? usuario.CorreoEvaluadorSst,
                CargoEvaluadorSst = usuario.CargoEvaluadorSst,
                FechaIngreso = usuario.FechaIngreso,
                FechaGeneracionReporte = DateTime.Today,
                FechaEvaluacion = evaluacion.FechaEvaluacion,
                TipoEvaluacion = evaluacion.TipoEvaluacion,
                NombreNivel = nivel.Nombre,
                AlcanceEvaluadorActual = EvaluacionRolesHelper.GetEtiquetaParte(parteActual),
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
                PuedeAdjuntarFirmaActual = !evaluadorActual.EsSuperAdministrador &&
                                           etiquetaFirmaActual != null &&
                                           !planAccionBloqueado,
                EtiquetaFirmaActual = etiquetaFirmaActual,
                PlanAccionBloqueado = planAccionBloqueado,
                FechaProximaEvaluacion = evaluacion.FechaProximaEvaluacion
            };
        }
    }
}
