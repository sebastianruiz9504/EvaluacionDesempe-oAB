using System;

namespace EvaluacionDesempenoAB.Models
{
    public class Evaluacion
    {
        public Guid Id { get; set; }

        public Guid UsuarioId { get; set; }

        // Ya no usamos Guid para evaluador, pero lo dejamos por compatibilidad
        public Guid EvaluadorId { get; set; }

        public Guid NivelId { get; set; }

        public DateTime FechaEvaluacion { get; set; }

        public string TipoEvaluacion { get; set; } = string.Empty; // "Inicial" / "Seguimiento"

        public decimal? Total { get; set; }

        public string? Observaciones { get; set; }

        public DateTime? FechaProximaEvaluacion { get; set; }

        public Guid? EvaluacionOrigenId { get; set; }

        public string? Estado { get; set; }

        // Nuevo: nombre del evaluador tal como se guarda en crfb7_evaluadorid
        public string? EvaluadorNombre { get; set; }
    }
}
