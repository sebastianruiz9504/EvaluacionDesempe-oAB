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
}
