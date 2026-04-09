using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using EvaluacionDesempenoAB.Models;

namespace EvaluacionDesempenoAB.Services
{
    public interface IEvaluacionRepository
    {
        // === USUARIOS ===

        // Traer un usuario (evaluador) por su correo electrónico
        Task<UsuarioEvaluado?> GetUsuarioByCorreoAsync(string correo);

        // Traer los usuarios a cargo de un evaluador (por correo del evaluador)
        Task<List<UsuarioEvaluado>> GetUsuariosByEvaluadorAsync(string evaluadorCorreo);

        // Traer todos los usuarios
        Task<List<UsuarioEvaluado>> GetUsuariosAsync();

        // Traer un usuario por Id
        Task<UsuarioEvaluado?> GetUsuarioByIdAsync(Guid id);
        Task<List<UsuarioEvaluado>> GetUsuariosByIdsAsync(IEnumerable<Guid> ids);

        // Actualizar novedades del usuario
        Task UpdateUsuarioNovedadesAsync(Guid usuarioId, string? novedades);
        Task UploadFirmaUsuarioAsync(Guid usuarioId, string fileName, string? contentType, Stream content);
        Task<ArchivoEvaluacion?> DownloadFirmaUsuarioAsync(Guid usuarioId);

        // === NIVELES ===

        Task<List<NivelEvaluacion>> GetNivelesActivosAsync();
        Task<NivelEvaluacion?> GetNivelByIdAsync(Guid id);
        Task<List<NivelEvaluacion>> GetNivelesByIdsAsync(IEnumerable<Guid> ids);

        // === COMPETENCIAS / COMPORTAMIENTOS ===

        Task<List<Competencia>> GetCompetenciasAsync();
        Task<List<Comportamiento>> GetComportamientosByNivelAsync(Guid nivelId);
        Task<List<Comportamiento>> GetComportamientosByNivelesAsync(IEnumerable<Guid> nivelIds);

        // === EVALUACIONES ===

        // Evaluaciones de un evaluador (por correo del evaluador)
        Task<List<Evaluacion>> GetEvaluacionesByEvaluadorAsync(string evaluadorCorreo);

        // Todas las evaluaciones
        Task<List<Evaluacion>> GetEvaluacionesAsync();

        // Evaluaciones por usuario
        Task<List<Evaluacion>> GetEvaluacionesByUsuarioAsync(Guid usuarioId);

        Task<Evaluacion?> GetEvaluacionByIdAsync(Guid id);
        Task UploadReporteFirmadoAsync(Guid evaluacionId, string fileName, string? contentType, Stream content);
        Task<ArchivoEvaluacion?> DownloadReporteFirmadoAsync(Guid evaluacionId);

        Task<Guid> CreateEvaluacionAsync(Evaluacion evaluacion,
                                         List<EvaluacionDetalle> detalles,
                                         List<PlanAccion> planAccion);

        Task UpdateEvaluacionAsync(Evaluacion evaluacion,
                                   List<EvaluacionDetalle> detalles,
                                   List<PlanAccion> planAccion);

        // Detalles y planes
        Task<List<EvaluacionDetalle>> GetDetallesByEvaluacionAsync(Guid evaluacionId);
        Task<List<EvaluacionDetalle>> GetDetallesByEvaluacionesAsync(IEnumerable<Guid> evaluacionIds);
        Task<List<PlanAccion>> GetPlanesByEvaluacionAsync(Guid evaluacionId);
    }
}
