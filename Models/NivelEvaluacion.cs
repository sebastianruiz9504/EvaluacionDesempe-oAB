namespace EvaluacionDesempenoAB.Models
{
    public class NivelEvaluacion
    {
        public Guid Id { get; set; }             // dt_nivelid
        public string Nombre { get; set; } = ""; // Estratégico, Táctico, etc.
        public string Codigo { get; set; } = ""; // ESTR, TACT, OPEADM, OPE
        public string? Descripcion { get; set; }
        public bool Activo { get; set; } = true;
    }
}
