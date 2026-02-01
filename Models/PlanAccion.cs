namespace EvaluacionDesempenoAB.Models
{
    public class PlanAccion
    {
        public Guid Id { get; set; }               // dt_planaccionid
        public Guid EvaluacionId { get; set; }     // dt_evaluacionid

        public string DescripcionAccion { get; set; } = "";
        public string? Responsable { get; set; }
        public DateTime? FechaCompromiso { get; set; }

        public string Estado { get; set; } = "Pendiente"; // Pendiente/En progreso/Completada/No cumplida
        public int? PorcentajeAvance { get; set; }

        public string? ComentariosEvaluador { get; set; }
        public string? ComentariosSeguimiento { get; set; }
        public DateTime? FechaSeguimiento { get; set; }
    }
}
