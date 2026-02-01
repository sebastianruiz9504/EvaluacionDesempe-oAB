namespace EvaluacionDesempenoAB.Models
{
    public class EvaluacionDetalle
    {
        public Guid Id { get; set; }               // dt_evaluaciondetalleid
        public Guid EvaluacionId { get; set; }     // dt_evaluacionid
        public Guid ComportamientoId { get; set; } // dt_comportamientoid
        public Guid? ParametroEvaluacionId { get; set; } // dt_parametroevaluacionid

        public int Puntaje { get; set; }           // 1-5
        public string? Comentario { get; set; }
    }
}
