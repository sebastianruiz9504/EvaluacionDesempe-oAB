using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Claims;
using System.Threading.Tasks;
using EvaluacionDesempenoAB.Models;
using EvaluacionDesempenoAB.Services;
using EvaluacionDesempenoAB.ViewModels;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace EvaluacionDesempenoAB.Controllers
{
    [Authorize]
    public class EvaluacionesController : Controller
    {
        private readonly IEvaluacionRepository _repo;

 private static readonly Dictionary<int, (string Codigo, string Nombre)> TipoFormularioNiveles = new()
        {
            { 433930001, ("OPEADM", "Operativo Administrativo") },
            { 433930000, ("TACT", "Táctico") },
            { 433930003, ("ESTR", "Estratégico") },
            { 433930002, ("OPE", "Operativo") }
        };
        public EvaluacionesController(IEvaluacionRepository repo)
        {
            _repo = repo;
        }

        // ================== HELPERS ==================

        private string? GetUserEmail()
        {
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
private static NivelEvaluacion? ResolveNivelPorTipoFormulario(
            UsuarioEvaluado usuario,
            IEnumerable<NivelEvaluacion> niveles)
        {
            if (!usuario.TipoFormulario.HasValue)
                return null;

            if (!TipoFormularioNiveles.TryGetValue(usuario.TipoFormulario.Value, out var nivelInfo))
                return null;

            return niveles.FirstOrDefault(n =>
                       string.Equals(n.Codigo, nivelInfo.Codigo, StringComparison.OrdinalIgnoreCase))
                   ?? niveles.FirstOrDefault(n =>
                       string.Equals(n.Nombre, nivelInfo.Nombre, StringComparison.OrdinalIgnoreCase));
        }

        // ================== LISTADO PRINCIPAL ==================

        public async Task<IActionResult> Index()
        {
            var evaluador = await GetEvaluadorActualAsync();
            if (evaluador == null)
                return Forbid();

            var evals = await _repo.GetEvaluacionesByEvaluadorAsync(evaluador.CorreoElectronico ?? GetUserEmail() ?? string.Empty);

            var usuariosDict = new Dictionary<Guid, UsuarioEvaluado>();
            var nivelesDict = new Dictionary<Guid, NivelEvaluacion>();

            var vm = new List<EvaluacionListaViewModel>();

            foreach (var e in evals)
            {
                if (!usuariosDict.TryGetValue(e.UsuarioId, out var usuario))
                {
                    usuario = await _repo.GetUsuarioByIdAsync(e.UsuarioId) ?? new UsuarioEvaluado();
                    usuariosDict[e.UsuarioId] = usuario;
                }

                if (!nivelesDict.TryGetValue(e.NivelId, out var nivel))
                {
                    nivel = await _repo.GetNivelByIdAsync(e.NivelId) ?? new NivelEvaluacion();
                    nivelesDict[e.NivelId] = nivel;
                }

                var puedeReevaluar = e.TipoEvaluacion == "Inicial";

                vm.Add(new EvaluacionListaViewModel
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
                    ResultadoFinal = e.Total,
                    PuedeReevaluar = puedeReevaluar
                });
            }

            return View(vm);
        }

        // ================== NUEVA EVALUACIÓN: SELECCIÓN DE NIVEL ==================

        [HttpGet]
        public async Task<IActionResult> Nueva(Guid usuarioId)
        {
            var evaluador = await GetEvaluadorActualAsync();
            if (evaluador == null)
                return Forbid();

            var usuario = await _repo.GetUsuarioByIdAsync(usuarioId);
            if (usuario == null)
                return NotFound();

            var niveles = await _repo.GetNivelesActivosAsync();

 var nivelAuto = ResolveNivelPorTipoFormulario(usuario, niveles);
            if (nivelAuto != null)
            {
                return RedirectToAction("Formulario", new { usuarioId = usuario.Id, nivelId = nivelAuto.Id });
            }
            
            ViewBag.Usuario = usuario;
            ViewBag.Niveles = niveles;

            return View(); // Views/Evaluaciones/Nueva.cshtml
        }

        // ================== FORMULARIO (CREAR / SEGUIMIENTO) ==================

        [HttpGet]
        public async Task<IActionResult> Formulario(Guid usuarioId, Guid nivelId, Guid? evaluacionOrigenId = null)
        {
            var evaluador = await GetEvaluadorActualAsync();
            if (evaluador == null)
                return Forbid();

            var usuario = await _repo.GetUsuarioByIdAsync(usuarioId);
            var nivel = await _repo.GetNivelByIdAsync(nivelId);

            if (usuario == null || nivel == null)
                return NotFound();

            var competencias = await _repo.GetCompetenciasAsync();
            var comportamientos = await _repo.GetComportamientosByNivelAsync(nivelId);

            var vm = new EvaluacionFormularioViewModel
            {
                UsuarioId = usuario.Id,
                NivelId = nivel.Id,
                NombreUsuario = usuario.NombreCompleto,
                CedulaUsuario = usuario.Cedula,
                Cargo = usuario.Cargo,
                Gerencia = usuario.Gerencia,
                NombreNivel = nivel.Nombre,
                TipoEvaluacion = evaluacionOrigenId.HasValue ? "Seguimiento" : "Inicial",
                EvaluacionOrigenId = evaluacionOrigenId,
                FechaEvaluacion = DateTime.Today
            };

            foreach (var comp in competencias.OrderBy(c => c.Orden))
            {
                var compVm = new CompetenciaEvaluacionVm
                {
                    Nombre = comp.Nombre
                };

                var comps = comportamientos
                    .Where(x => x.CompetenciaId == comp.Id)
                    .OrderBy(x => x.Orden);

                foreach (var c in comps)
                {
                    compVm.Comportamientos.Add(new ComportamientoEvaluacionVm
                    {
                        ComportamientoId = c.Id,
                        Descripcion = c.Descripcion
                    });
                }

                if (compVm.Comportamientos.Any())
                    vm.Competencias.Add(compVm);
            }

            return View("Formulario", vm);
        }

        // ================== EDITAR EVALUACIÓN EXISTENTE ==================

        [HttpGet]
        public async Task<IActionResult> Editar(Guid id)
        {
            var evaluador = await GetEvaluadorActualAsync();
            if (evaluador == null)
                return Forbid();

            var eval = await _repo.GetEvaluacionByIdAsync(id);
            if (eval == null)
                return NotFound();

            var usuario = await _repo.GetUsuarioByIdAsync(eval.UsuarioId);
            var nivel = await _repo.GetNivelByIdAsync(eval.NivelId);

            if (usuario == null || nivel == null)
                return NotFound();

            var detalles = await _repo.GetDetallesByEvaluacionAsync(id);
            var competencias = await _repo.GetCompetenciasAsync();
            var comportamientos = await _repo.GetComportamientosByNivelAsync(eval.NivelId);

            var vm = new EvaluacionFormularioViewModel
            {
                Id = eval.Id,
                UsuarioId = usuario.Id,
                NivelId = nivel.Id,
                NombreUsuario = usuario.NombreCompleto,
                CedulaUsuario = usuario.Cedula,
                Cargo = usuario.Cargo,
                Gerencia = usuario.Gerencia,
                FechaEvaluacion = eval.FechaEvaluacion,
                NombreNivel = nivel.Nombre,
                ObservacionesGenerales = eval.Observaciones,
                TipoEvaluacion = eval.TipoEvaluacion,
                EvaluacionOrigenId = eval.EvaluacionOrigenId
            };

            foreach (var comp in competencias.OrderBy(c => c.Orden))
            {
                var compVm = new CompetenciaEvaluacionVm { Nombre = comp.Nombre };
                var comps = comportamientos
                    .Where(x => x.CompetenciaId == comp.Id)
                    .OrderBy(x => x.Orden);

                foreach (var c in comps)
                {
                    var det = detalles.FirstOrDefault(d => d.ComportamientoId == c.Id);

                    compVm.Comportamientos.Add(new ComportamientoEvaluacionVm
                    {
                        ComportamientoId = c.Id,
                        Descripcion = c.Descripcion,
                        Puntaje = det?.Puntaje,
                        Comentario = det?.Comentario
                    });
                }

                if (compVm.Comportamientos.Any())
                    vm.Competencias.Add(compVm);
            }

            // OJO: el plan de acción ya NO se edita en el formulario,
            // solo en el Reporte, así que no lo cargamos aquí.

            return View("Formulario", vm);
        }

        // ================== GUARDAR (CREATE / UPDATE) ==================

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> Guardar(EvaluacionFormularioViewModel model, string accion)
        {
            var evaluador = await GetEvaluadorActualAsync();
            if (evaluador == null)
                return Forbid();

            if (!ModelState.IsValid)
                return View("Formulario", model);

            var evaluacionId = model.Id ?? Guid.NewGuid();

            var evaluacion = new Evaluacion
            {
                Id = evaluacionId,
                UsuarioId = model.UsuarioId,
                NivelId = model.NivelId,
                FechaEvaluacion = model.FechaEvaluacion,
                TipoEvaluacion = model.TipoEvaluacion,
                EvaluacionOrigenId = model.EvaluacionOrigenId,
                Observaciones = model.ObservacionesGenerales,
                Estado = accion == "finalizar" ? "Finalizada" : "Borrador",
                FechaProximaEvaluacion = model.TipoEvaluacion == "Inicial"
                    ? model.FechaEvaluacion.AddMonths(6)
                    : null,
                EvaluadorNombre = evaluador.CorreoElectronico ?? GetUserEmail() ?? string.Empty
            };

            var detalles = new List<EvaluacionDetalle>();
            foreach (var comp in model.Competencias)
            {
                foreach (var c in comp.Comportamientos)
                {
                    if (c.Puntaje.HasValue)
                    {
                        detalles.Add(new EvaluacionDetalle
                        {
                            Id = Guid.NewGuid(),
                            EvaluacionId = evaluacion.Id,
                            ComportamientoId = c.ComportamientoId,
                            Puntaje = c.Puntaje.Value,
                            Comentario = c.Comentario
                        });
                    }
                }
            }

            if (detalles.Any())
            {
                var prom = detalles.Average(d => d.Puntaje);
                evaluacion.Total = (decimal?)Math.Round(prom, 2);
            }

            // 🔴 IMPORTANTE:
            // Ya NO se guarda plan de acción desde el formulario.
            // - Si es una nueva evaluación → no tiene planes todavía.
            // - Si es edición → conservamos los planes existentes.

            if (model.Id == null)
            {
                var planesVacios = new List<PlanAccion>();
                await _repo.CreateEvaluacionAsync(evaluacion, detalles, planesVacios);
            }
            else
            {
                var planesExistentes = await _repo.GetPlanesByEvaluacionAsync(evaluacion.Id);
                await _repo.UpdateEvaluacionAsync(evaluacion, detalles, planesExistentes);
            }

            if (accion == "finalizar")
                return RedirectToAction(nameof(Reporte), new { id = evaluacion.Id });

            return RedirectToAction(nameof(Index));
        }

        // ================== REEVALUAR (SEGUIMIENTO) ==================

        [HttpGet]
        public async Task<IActionResult> Reevaluar(Guid id)
        {
            var evaluador = await GetEvaluadorActualAsync();
            if (evaluador == null)
                return Forbid();

            var evalOriginal = await _repo.GetEvaluacionByIdAsync(id);
            if (evalOriginal == null)
                return NotFound();

            return RedirectToAction(nameof(Formulario), new
            {
                usuarioId = evalOriginal.UsuarioId,
                nivelId = evalOriginal.NivelId,
                evaluacionOrigenId = evalOriginal.Id
            });
        }

        // ================== CARPETA POR USUARIO ==================

        [HttpGet]
        public async Task<IActionResult> CarpetaUsuario(Guid usuarioId)
        {
            var evaluador = await GetEvaluadorActualAsync();
            if (evaluador == null)
                return Forbid();

            var usuario = await _repo.GetUsuarioByIdAsync(usuarioId);
            if (usuario == null)
                return NotFound();

            var evals = await _repo.GetEvaluacionesByUsuarioAsync(usuarioId);

            var nivelesDict = new Dictionary<Guid, NivelEvaluacion>();
            var lista = new List<EvaluacionListaViewModel>();

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
                    ResultadoFinal = e.Total,
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

        // ================== REPORTE (VER RESULTADOS) ==================

        [HttpGet]
        public async Task<IActionResult> Reporte(Guid id)
        {
            var evaluador = await GetEvaluadorActualAsync();
            if (evaluador == null)
                return Forbid();

           var vm = await BuildReporteViewModelAsync(id);
            if (vm == null)
                return NotFound();

            return View("Reporte", vm);
        }

        [HttpGet]
        public async Task<IActionResult> ImprimirResultados(Guid id)
        {
            var evaluador = await GetEvaluadorActualAsync();
            if (evaluador == null)
                return Forbid();

            var vm = await BuildReporteViewModelAsync(id);
            if (vm == null)
                return NotFound();

            return View("ReporteImpresion", vm);
        }

        // ================== GUARDAR PLAN DE ACCIÓN (DESDE REPORTE) ==================

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> GuardarPlanAccion(EvaluacionReporteViewModel model)
        {
            var evaluador = await GetEvaluadorActualAsync();
            if (evaluador == null)
                return Forbid();

            var eval = await _repo.GetEvaluacionByIdAsync(model.EvaluacionId);
            if (eval == null)
                return NotFound();

            var detalles = await _repo.GetDetallesByEvaluacionAsync(model.EvaluacionId);

            var planes = model.PlanAccion
                .Where(p => !string.IsNullOrWhiteSpace(p.Descripcion) &&
                            !string.IsNullOrWhiteSpace(p.Comportamiento))
                .Select(p => new PlanAccion
                {
                    Id = p.Id ?? Guid.NewGuid(),
                    EvaluacionId = model.EvaluacionId,
                    DescripcionAccion = p.Descripcion!,
                       Responsable = p.Comportamiento
                }).ToList();
 eval.FechaProximaEvaluacion = model.FechaProximaEvaluacion;

            // Reutilizamos UpdateEvaluacionAsync para actualizar solo el plan:
            await _repo.UpdateEvaluacionAsync(eval, detalles, planes);

            return RedirectToAction(nameof(Reporte), new { id = model.EvaluacionId });
        }

        private async Task<EvaluacionReporteViewModel?> BuildReporteViewModelAsync(Guid id)
        {
            var eval = await _repo.GetEvaluacionByIdAsync(id);
            if (eval == null)
                return null;

            var usuario = await _repo.GetUsuarioByIdAsync(eval.UsuarioId);
            var nivel = await _repo.GetNivelByIdAsync(eval.NivelId);
            if (usuario == null || nivel == null)
                return null;

            var detalles = await _repo.GetDetallesByEvaluacionAsync(id);
            var planes = await _repo.GetPlanesByEvaluacionAsync(id);

            var competencias = await _repo.GetCompetenciasAsync();
            var comportamientos = await _repo.GetComportamientosByNivelAsync(eval.NivelId);

            var competenciasVm = new List<CompetenciaReporteVm>();

            foreach (var comp in competencias.OrderBy(c => c.Orden))
            {
                var compComps = comportamientos
                    .Where(x => x.CompetenciaId == comp.Id)
                    .OrderBy(x => x.Orden)
                    .ToList();

                if (!compComps.Any())
                    continue;

                var compVm = new CompetenciaReporteVm
                {
                    Nombre = comp.Nombre
                };

                var puntajes = new List<int>();

                foreach (var compo in compComps)
                {
                    var det = detalles.FirstOrDefault(d => d.ComportamientoId == compo.Id);

                    var puntaje = det?.Puntaje ?? 0;
                    if (puntaje > 0)
                        puntajes.Add(puntaje);

                    compVm.Comportamientos.Add(new ComportamientoReporteVm
                    {
                        Descripcion = compo.Descripcion,
                        Puntaje = det?.Puntaje,
                        Comentario = det?.Comentario
                    });
                }

                if (puntajes.Any())
                {
                    var promedio = puntajes.Select(p => (decimal)p).Average();
                    compVm.Promedio = Math.Round(promedio, 2);
                    competenciasVm.Add(compVm);
                }
            }

            decimal? promedioGeneral = null;
            if (competenciasVm.Any())
            {
                promedioGeneral = Math.Round(
                    competenciasVm.Average(c => c.Promedio),
                    2
                );
            }

            var oportunidades = new List<OportunidadMejoraVm>();

            foreach (var comp in competenciasVm)
            {
                foreach (var c in comp.Comportamientos)
                {
                    if (c.Puntaje.HasValue && c.Puntaje.Value < 76)
                    {
                        oportunidades.Add(new OportunidadMejoraVm
                        {
                            Competencia = comp.Nombre,
                            Comportamiento = c.Descripcion,
                            Puntaje = c.Puntaje.Value
                        });
                    }
                }
            }

            var planVm = planes.Select(p => new PlanAccionItemVm
            {
                Id = p.Id,
                 Comportamiento = p.Responsable,
                Descripcion = p.DescripcionAccion
            }).ToList();

            if (!planVm.Any())
            {
                planVm.Add(new PlanAccionItemVm());
            }

            return new EvaluacionReporteViewModel
            {
                EvaluacionId = eval.Id,
                NombreUsuario = usuario.NombreCompleto,
                CedulaUsuario = usuario.Cedula,
                Cargo = usuario.Cargo,
                Gerencia = usuario.Gerencia,
                FechaEvaluacion = eval.FechaEvaluacion,
                TipoEvaluacion = eval.TipoEvaluacion,
                NombreNivel = nivel.Nombre,
                PromedioGeneral = promedioGeneral,
                Competencias = competenciasVm,
                OportunidadesMejora = oportunidades,
                PlanAccion = planVm,
                   ObservacionesGenerales = eval.Observaciones,
                FechaProximaEvaluacion = eval.FechaProximaEvaluacion
            };
        }
    }
}