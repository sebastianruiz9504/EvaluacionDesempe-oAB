using System;

namespace EvaluacionDesempenoAB.Models
{
    public class UsuarioEvaluado
    {
        public Guid Id { get; set; }

        public string NombreCompleto { get; set; } = string.Empty;
        public string Cedula { get; set; } = string.Empty;

        public string? Cargo { get; set; }
        public string? Gerencia { get; set; }
        public string? Regional { get; set; }
        public DateTime? FechaIngreso { get; set; }
         public DateTime? FechaInicioContrato { get; set; }
        public DateTime? FechaFinalizacionContrato { get; set; }
        public DateTime? FechaFinalizacionPeriodoPrueba { get; set; }

        // Correo electrónico con el que se autentica el usuario/evaluador
        public string? CorreoElectronico { get; set; }

        // Nombre del evaluador (texto) tal como está en crfb7_evaluadorid
        public string? EvaluadorNombre { get; set; }
         public int? TipoFormulario { get; set; }

        public bool EsSuperAdministrador { get; set; }

        public string? Novedades { get; set; }
    }
}
