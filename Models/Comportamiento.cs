namespace EvaluacionDesempenoAB.Models
{
    public class Comportamiento
    {
        public Guid Id { get; set; }                // dt_comportamientoid
        public Guid CompetenciaId { get; set; }     // dt_competenciaid (lookup)
        public Guid NivelId { get; set; }           // dt_nivelid (lookup)

        public string Descripcion { get; set; } = "";
        public int Orden { get; set; }
        public bool Activo { get; set; } = true;
    }
}
