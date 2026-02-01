namespace EvaluacionDesempenoAB.Models
{
    public class ParametroEvaluacion
    {
        public Guid Id { get; set; }         // dt_parametroevaluacionid
        public string Nombre { get; set; } = ""; // NUNCA, SIEMPRE, etc.
        public int Valor { get; set; }       // 1-5
        public string? Descripcion { get; set; }
        public bool Activo { get; set; } = true;
    }
}
