using System;
using System.Collections.Generic;

namespace EvaluacionDesempenoAB.ViewModels
{
    // ===== LISTADO PRINCIPAL / CARPETA =====

    public class EvaluacionListaViewModel
    {
        public Guid Id { get; set; }
        public Guid UsuarioId { get; set; }
        public DateTime FechaEvaluacion { get; set; }
        public DateTime? ProximaEvaluacion { get; set; }

        public string NombreUsuario { get; set; } = string.Empty;
        public string CedulaUsuario { get; set; } = string.Empty;

        public string? NivelNombre { get; set; }
        public string? NivelCodigo { get; set; }

        public string TipoEvaluacion { get; set; } = string.Empty;
        public bool PuedeReevaluar { get; set; }
    }

    public class CarpetaUsuarioViewModel
    {
        public Guid UsuarioId { get; set; }
        public string NombreUsuario { get; set; } = string.Empty;
        public string CedulaUsuario { get; set; } = string.Empty;
        public string? Cargo { get; set; }

        public List<EvaluacionListaViewModel> Evaluaciones { get; set; } = new();
    }

    // ===== FORMULARIO =====

    public class EvaluacionFormularioViewModel
    {
        public Guid? Id { get; set; }

        public Guid UsuarioId { get; set; }
        public Guid NivelId { get; set; }

        public string NombreUsuario { get; set; } = string.Empty;
        public string CedulaUsuario { get; set; } = string.Empty;
        public string? Cargo { get; set; }
        public string? Gerencia { get; set; }
        public string NombreNivel { get; set; } = string.Empty;

        public DateTime FechaEvaluacion { get; set; } = DateTime.Today;

        public string TipoEvaluacion { get; set; } = "Inicial"; // o Seguimiento
        public Guid? EvaluacionOrigenId { get; set; }

        public string? ObservacionesGenerales { get; set; }

        public List<CompetenciaEvaluacionVm> Competencias { get; set; } = new();
        public List<PlanAccionItemVm> PlanAccion { get; set; } = new();
    }

    public class CompetenciaEvaluacionVm
    {
        public string Nombre { get; set; } = string.Empty;
        public List<ComportamientoEvaluacionVm> Comportamientos { get; set; } = new();
    }

    public class ComportamientoEvaluacionVm
    {
        public Guid ComportamientoId { get; set; }
        public string Descripcion { get; set; } = string.Empty;

        // 0 a 100
        public int? Puntaje { get; set; }

        public string? Comentario { get; set; }
    }

    public class PlanAccionItemVm
    {
        public Guid? Id { get; set; }
          public string? Comportamiento { get; set; }
        public string? Descripcion { get; set; }
        public string? Responsable { get; set; }
        public DateTime? FechaCompromiso { get; set; }
    }

    // ===== REPORTE =====

    public class EvaluacionReporteViewModel
    {
        public Guid EvaluacionId { get; set; }

        public string NombreUsuario { get; set; } = string.Empty;
        public string CedulaUsuario { get; set; } = string.Empty;
        public string? Cargo { get; set; }
        public string? Gerencia { get; set; }

        public DateTime FechaEvaluacion { get; set; }
        public string TipoEvaluacion { get; set; } = string.Empty;
        public string NombreNivel { get; set; } = string.Empty;

        public decimal? PromedioGeneral { get; set; }

        public List<CompetenciaReporteVm> Competencias { get; set; } = new();
        public List<OportunidadMejoraVm> OportunidadesMejora { get; set; } = new();
        public List<PlanAccionItemVm> PlanAccion { get; set; } = new();

        public string? ObservacionesGenerales { get; set; }
        
        public DateTime? FechaProximaEvaluacion { get; set; }
    }

    public class CompetenciaReporteVm
    {
        public string Nombre { get; set; } = string.Empty;
        public decimal Promedio { get; set; }

        public List<ComportamientoReporteVm> Comportamientos { get; set; } = new();
    }

    public class ComportamientoReporteVm
    {
        public string Descripcion { get; set; } = string.Empty;
        public int? Puntaje { get; set; }
        public string? Comentario { get; set; }
    }

    public class OportunidadMejoraVm
    {
        public string Competencia { get; set; } = string.Empty;
        public string Comportamiento { get; set; } = string.Empty;
        public int Puntaje { get; set; }
    }
}
