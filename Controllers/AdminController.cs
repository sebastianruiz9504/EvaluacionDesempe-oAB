using System;
using System.IO;
using System.Linq;
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

        public async Task<IActionResult> Index(string? cedula)
        {
            if (!await EsSuperAdminAsync())
                return Forbid();

            var usuarios = await _repo.GetUsuariosAsync();
            if (!string.IsNullOrWhiteSpace(cedula))
            {
                usuarios = usuarios
                    .Where(usuario => usuario.Cedula?.Contains(cedula, StringComparison.OrdinalIgnoreCase) == true)
                    .ToList();
            }

            ViewData["CedulaFiltro"] = cedula;
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

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> SubirFoto(Guid id, IFormFile? archivo, string? cedula)
        {
            if (!await EsSuperAdminAsync())
                return Forbid();

            if (id == Guid.Empty)
            {
                TempData["AdminFotoError"] = "Selecciona un usuario antes de subir la foto.";
                return RedirectToAction(nameof(Index), new { cedula });
            }

            if (archivo == null || archivo.Length == 0)
            {
                TempData["AdminFotoError"] = "Selecciona una imagen en formato PNG o JPG.";
                return RedirectToAction(nameof(Index), new { cedula });
            }

            if (!EsImagenValida(archivo))
            {
                TempData["AdminFotoError"] = "La foto debe estar en formato PNG o JPG.";
                return RedirectToAction(nameof(Index), new { cedula });
            }

            try
            {
                await using var stream = archivo.OpenReadStream();
                await _repo.UploadFotoUsuarioAsync(id, archivo.FileName, archivo.ContentType, stream);
                TempData["AdminFotoSuccess"] = "La foto se cargó correctamente en Dataverse.";
            }
            catch (Exception ex)
            {
                TempData["AdminFotoError"] = $"No fue posible cargar la foto: {ex.Message}";
            }

            return RedirectToAction(nameof(Index), new { cedula });
        }

        private static bool EsImagenValida(IFormFile archivo)
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
    }
}
