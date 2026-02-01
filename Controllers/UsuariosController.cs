using System;
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
    public class UsuariosController : Controller
    {
        private readonly IEvaluacionRepository _repo;

        public UsuariosController(IEvaluacionRepository repo)
        {
            _repo = repo;
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
        public async Task<IActionResult> Index()
        {
            var evaluador = await GetEvaluadorActualAsync();
            if (evaluador == null)
            {
                // El usuario autenticado no está en la tabla crfb7_usuario → no es evaluador
                return Forbid();
            }

            // Filtramos por el NOMBRE del evaluador, tal como está en crfb7_evaluadorid
            var usuarios = await _repo.GetUsuariosByEvaluadorAsync(evaluador.NombreCompleto);

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
    }
}
