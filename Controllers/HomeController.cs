using System.Diagnostics;
using System.Text;
using EvaluacionDesempenoAB.Models;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Mvc;

namespace EvaluacionDesempenoAB.Controllers
{
    public class HomeController : Controller
    {
        private readonly ILogger<HomeController> _logger;

        public HomeController(ILogger<HomeController> logger)
        {
            _logger = logger;
        }

        public IActionResult Index()
        {
            return View();
        }

        [AllowAnonymous]
        [ResponseCache(Duration = 0, Location = ResponseCacheLocation.None, NoStore = true)]
        public IActionResult Error()
        {
            var exceptionFeature = HttpContext.Features.Get<IExceptionHandlerPathFeature>();
            var exception = exceptionFeature?.Error;
            var path = exceptionFeature?.Path ?? HttpContext.Request.Path.Value;
            var method = HttpContext.Request.Method;
            var requestId = Activity.Current?.Id ?? HttpContext.TraceIdentifier;

            Response.StatusCode = StatusCodes.Status500InternalServerError;

            if (exception != null)
            {
                _logger.LogError(
                    exception,
                    "Error no controlado. RequestId: {RequestId}. Metodo: {Method}. Ruta: {Path}.",
                    requestId,
                    method,
                    path);
            }

            return View(BuildErrorViewModel(
                StatusCodes.Status500InternalServerError,
                path,
                method,
                requestId,
                exception));
        }

        [AllowAnonymous]
        [ResponseCache(Duration = 0, Location = ResponseCacheLocation.None, NoStore = true)]
        public IActionResult StatusCodePage(int code)
        {
            var statusCodeFeature = HttpContext.Features.Get<IStatusCodeReExecuteFeature>();
            var path = statusCodeFeature?.OriginalPath ?? HttpContext.Request.Path.Value;
            var method = HttpContext.Request.Method;
            var requestId = Activity.Current?.Id ?? HttpContext.TraceIdentifier;

            Response.StatusCode = code;

            return View("Error", BuildErrorViewModel(
                code,
                path,
                method,
                requestId,
                exception: null));
        }

        private static ErrorViewModel BuildErrorViewModel(
            int statusCode,
            string? path,
            string? method,
            string? requestId,
            Exception? exception)
        {
            return new ErrorViewModel
            {
                RequestId = requestId,
                StatusCode = statusCode,
                Path = path,
                Method = method,
                ExceptionType = exception?.GetType().FullName,
                ExceptionMessage = exception?.Message,
                TechnicalDetail = BuildExceptionDetail(exception),
                Timestamp = DateTimeOffset.Now
            };
        }

        private static string? BuildExceptionDetail(Exception? exception)
        {
            if (exception == null)
            {
                return null;
            }

            var builder = new StringBuilder();
            var current = exception;
            var level = 0;

            while (current != null)
            {
                if (level > 0)
                {
                    builder.AppendLine();
                    builder.AppendLine($"Inner exception {level}:");
                }

                builder.AppendLine($"{current.GetType().FullName}: {current.Message}");
                current = current.InnerException;
                level++;
            }

            return builder.ToString().Trim();
        }
    }
}
