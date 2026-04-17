namespace EvaluacionDesempenoAB.ViewModels
{
    public class UsuarioEvaluadoViewModel
    {
        public Guid Id { get; set; }
        public string NombreCompleto { get; set; } = "";
        public string Cedula { get; set; } = "";
        public string? Cargo { get; set; }
        public string? Gerencia { get; set; }
        public string? Regional { get; set; }
    }

    public class UsuarioPortalEvaluadorViewModel
    {
        public Guid Id { get; set; }
        public string NombreCompleto { get; set; } = string.Empty;
        public string Cedula { get; set; } = string.Empty;
        public string? Cargo { get; set; }
        public string? Gerencia { get; set; }
        public string? CorreoElectronico { get; set; }
        public DateTime? FechaInicioContrato { get; set; }
        public DateTime? FechaFinalizacionContrato { get; set; }
        public DateTime? FechaFinalizacionPeriodoPrueba { get; set; }
        public DateTime? FechaActivacionEvaluacion { get; set; }
        public bool EvaluacionNormalCompleta { get; set; }
        public bool EvaluacionSstCompleta { get; set; }
        public decimal? ResultadoFinal { get; set; }
        public Guid? EvaluacionActualId { get; set; }
        public bool PuedeIniciarEvaluacion { get; set; }
        public bool PuedeSolicitarActivacion { get; set; }
        public bool TieneEvaluacionActiva { get; set; }
    }
}
