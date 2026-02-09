using System;
using System.Security.Claims;
using System.Threading.Tasks;
using EvaluacionDesempenoAB.Models;
using EvaluacionDesempenoAB.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace EvaluacionDesempenoAB.Controllers
{
    [Authorize]
    public class AdminController : Controller
    {
        private readonly IEvaluacionRepository _repo;

        public AdminController(IEvaluacionRepository repo)
        {
            _repo = repo;
        }

        private string? GetUserEmail()
        {
            return User.FindFirst("preferred_username")?.Value
                   ?? User.FindFirst(ClaimTypes.Email)?.Value
                   ?? User.FindFirst(ClaimTypes.Upn)?.Value;
        }

        private async Task<UsuarioEvaluado?> GetUsuarioActualAsync()
        {
            var email = GetUserEmail();
            if (string.IsNullOrWhiteSpace(email))
                return null;

            return await _repo.GetUsuarioByCorreoAsync(email);
        }

        private async Task<bool> EsSuperAdminAsync()
        {
            var usuario = await GetUsuarioActualAsync();
            return usuario?.EsSuperAdministrador == true;
        }

        public async Task<IActionResult> Index()
        {
            if (!await EsSuperAdminAsync())
                return Forbid();

            var usuarios = await _repo.GetUsuariosAsync();
            return View(usuarios);
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> ActualizarNovedades(Guid id, string? novedades)
        {
            if (!await EsSuperAdminAsync())
                return Forbid();

            await _repo.UpdateUsuarioNovedadesAsync(id, novedades);
            return RedirectToAction(nameof(Index));
        }
    }
}
