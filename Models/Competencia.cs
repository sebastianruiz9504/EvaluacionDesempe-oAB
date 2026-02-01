namespace EvaluacionDesempenoAB.Models
{
    public class Competencia
    {
        public Guid Id { get; set; }           // dt_competenciaid
        public string Nombre { get; set; } = "";
        public string? Descripcion { get; set; }
        public int Orden { get; set; }
        public bool Activo { get; set; } = true;
    }
}
