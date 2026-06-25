using System.Collections.Generic;
using System.Linq;

namespace EvaluacionDesempenoAB.ViewModels
{
    public class SupervisionEvaluacionesViewModel
    {
        public List<SupervisionEvaluadorViewModel> Evaluadores { get; set; } = new();

        public int TotalEvaluadores => Evaluadores.Count;
        public int TotalUsuariosAsignados => Evaluadores.Sum(x => x.CantidadUsuariosAsignados);
        public int TotalUsuariosEnVentanaActiva => Evaluadores.Sum(x => x.CantidadUsuariosEnVentanaActiva);
        public int TotalEvaluacionesRealizadas => Evaluadores.Sum(x => x.CantidadEvaluacionesRealizadas);
        public int TotalEvaluacionesPendientes => Evaluadores.Sum(x => x.EvaluacionesPendientesEnVentanaActiva);
    }

    public class SupervisionEvaluadorViewModel
    {
        public string EvaluadorKey { get; set; } = string.Empty;
        public string EvaluadorNombre { get; set; } = string.Empty;
        public string? EvaluadorCorreo { get; set; }
        public int CantidadUsuariosAsignados { get; set; }
        public int CantidadUsuariosEnVentanaActiva { get; set; }
        public int CantidadEvaluacionesRealizadas { get; set; }
        public int EvaluacionesPendientesEnVentanaActiva { get; set; }

        public bool PuedeEnviarRecordatorio =>
            !string.IsNullOrWhiteSpace(EvaluadorCorreo) &&
            EvaluacionesPendientesEnVentanaActiva > 0;
    }
}
