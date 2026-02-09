using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using EvaluacionDesempenoAB.Models;

namespace EvaluacionDesempenoAB.Services
{
    public class MockRepository : IEvaluacionRepository
    {
        private readonly List<UsuarioEvaluado> _usuarios;
        private readonly List<NivelEvaluacion> _niveles;
        private readonly List<Competencia> _competencias;
        private readonly List<Comportamiento> _comportamientos;
        private readonly List<Evaluacion> _evaluaciones;
        private readonly List<EvaluacionDetalle> _detalles;
        private readonly List<PlanAccion> _planes;

        public MockRepository()
        {
            // Evaluador demo
            var evaluadorNombre = "Evaluador Demo";
            var evaluadorCorreo = "evaluador.demo@contoso.com";

            _usuarios = new List<UsuarioEvaluado>
            {
                new UsuarioEvaluado
                {
                    Id = Guid.NewGuid(),
                    NombreCompleto = evaluadorNombre,
                    Cedula = "1111111111",
                    Cargo = "Jefe",
                    Gerencia = "Operaciones",
                    CorreoElectronico = evaluadorCorreo,
                    EvaluadorNombre = null, // no tiene jefe por encima
                    EsSuperAdministrador = true,
                    Novedades = "Usuario de prueba con rol superadmin."
                },
                new UsuarioEvaluado
                {
                    Id = Guid.NewGuid(),
                    NombreCompleto = "Juan Pérez",
                    Cedula = "123456789",
                    Cargo = "Operario",
                    Gerencia = "Operaciones",
                    FechaInicioContrato = DateTime.Today.AddYears(-2),
                     FechaFinalizacionContrato = DateTime.Today.AddYears(1),
                    FechaFinalizacionPeriodoPrueba = DateTime.Today.AddYears(-2).AddMonths(3),
                    FechaActivacionEvaluacion = DateTime.Today.AddDays(-5),
                    CorreoElectronico = "juan.perez@contoso.com",
                    // está bajo el Evaluador Demo
                    EvaluadorNombre = evaluadorNombre,
                    TipoFormulario = 433930002, // Operativo
                    Novedades = "Pendiente documentación."
                },
                new UsuarioEvaluado
                {
                    Id = Guid.NewGuid(),
                    NombreCompleto = "María López",
                    Cedula = "987654321",
                    Cargo = "Auxiliar",
                    Gerencia = "Gestión Ambiental",
                      FechaInicioContrato = DateTime.Today.AddYears(-1),
                      
                    FechaFinalizacionContrato = DateTime.Today.AddYears(1),
                    FechaFinalizacionPeriodoPrueba = DateTime.Today.AddYears(-1).AddMonths(2),
                    FechaActivacionEvaluacion = null,
                    CorreoElectronico = "maria.lopez@contoso.com",
                    EvaluadorNombre = evaluadorNombre,
                    Novedades = "Cambio de cargo en trámite."
                }
            };

            _niveles = new List<NivelEvaluacion>
            {
                new NivelEvaluacion { Id = Guid.NewGuid(), Nombre = "Estratégico", Codigo = "ESTR" },
                new NivelEvaluacion { Id = Guid.NewGuid(), Nombre = "Táctico", Codigo = "TACT" },
                new NivelEvaluacion { Id = Guid.NewGuid(), Nombre = "Operativo Administrativo", Codigo = "OPEADM" },
                new NivelEvaluacion { Id = Guid.NewGuid(), Nombre = "Operativo", Codigo = "OPE" }
            };

            _competencias = new List<Competencia>
            {
                new Competencia { Id = Guid.NewGuid(), Nombre = "Trabajo en equipo", Orden = 1 },
                new Competencia { Id = Guid.NewGuid(), Nombre = "Orientación al servicio", Orden = 2 }
            };

            _comportamientos = new List<Comportamiento>();
            foreach (var nivel in _niveles)
            {
                foreach (var comp in _competencias)
                {
                    _comportamientos.Add(new Comportamiento
                    {
                        Id = Guid.NewGuid(),
                        CompetenciaId = comp.Id,
                        NivelId = nivel.Id,
                        Descripcion = $"Demuestra {comp.Nombre.ToLower()} en nivel {nivel.Nombre}",
                        Orden = 1
                    });
                    _comportamientos.Add(new Comportamiento
                    {
                        Id = Guid.NewGuid(),
                        CompetenciaId = comp.Id,
                        NivelId = nivel.Id,
                        Descripcion = $"Aplica {comp.Nombre.ToLower()} de forma consistente",
                        Orden = 2
                    });
                }
            }

            _evaluaciones = new List<Evaluacion>();
            _detalles = new List<EvaluacionDetalle>();
            _planes = new List<PlanAccion>();
        }

        // === USUARIOS ===

        public Task<UsuarioEvaluado?> GetUsuarioByCorreoAsync(string correo)
        {
            var u = _usuarios.FirstOrDefault(x =>
                string.Equals(x.CorreoElectronico, correo, StringComparison.OrdinalIgnoreCase));
            return Task.FromResult(u);
        }

        public Task<List<UsuarioEvaluado>> GetUsuariosByEvaluadorAsync(string evaluadorNombre)
        {
            var lista = _usuarios
                .Where(x => string.Equals(x.EvaluadorNombre, evaluadorNombre, StringComparison.OrdinalIgnoreCase))
                .ToList();

            return Task.FromResult(lista);
        }

        public Task<List<UsuarioEvaluado>> GetUsuariosAsync()
            => Task.FromResult(_usuarios.ToList());

        public Task<UsuarioEvaluado?> GetUsuarioByIdAsync(Guid id)
        {
            var u = _usuarios.FirstOrDefault(x => x.Id == id);
            return Task.FromResult(u);
        }

        public Task UpdateUsuarioNovedadesAsync(Guid usuarioId, string? novedades)
        {
            var u = _usuarios.FirstOrDefault(x => x.Id == usuarioId);
            if (u != null)
            {
                u.Novedades = novedades;
            }

            return Task.CompletedTask;
        }

        // === NIVELES ===

        public Task<List<NivelEvaluacion>> GetNivelesActivosAsync()
            => Task.FromResult(_niveles.ToList());

        public Task<NivelEvaluacion?> GetNivelByIdAsync(Guid id)
        {
            var n = _niveles.FirstOrDefault(x => x.Id == id);
            return Task.FromResult(n);
        }

        // === COMPETENCIAS / COMPORTAMIENTOS ===

        public Task<List<Competencia>> GetCompetenciasAsync()
            => Task.FromResult(_competencias.OrderBy(c => c.Orden).ToList());

        public Task<List<Comportamiento>> GetComportamientosByNivelAsync(Guid nivelId)
        {
            var lista = _comportamientos
                .Where(x => x.NivelId == nivelId)
                .OrderBy(x => x.CompetenciaId)
                .ThenBy(x => x.Orden)
                .ToList();

            return Task.FromResult(lista);
        }

        // === EVALUACIONES ===

        public Task<List<Evaluacion>> GetEvaluacionesByEvaluadorAsync(string evaluadorNombre)
        {
            var lista = _evaluaciones
                .Where(x => string.Equals(x.EvaluadorNombre, evaluadorNombre, StringComparison.OrdinalIgnoreCase))
                .OrderByDescending(x => x.FechaEvaluacion)
                .ToList();

            return Task.FromResult(lista);
        }

        public Task<List<Evaluacion>> GetEvaluacionesByUsuarioAsync(Guid usuarioId)
        {
            var lista = _evaluaciones
                .Where(x => x.UsuarioId == usuarioId)
                .OrderByDescending(x => x.FechaEvaluacion)
                .ToList();

            return Task.FromResult(lista);
        }

        public Task<Evaluacion?> GetEvaluacionByIdAsync(Guid id)
        {
            var e = _evaluaciones.FirstOrDefault(x => x.Id == id);
            return Task.FromResult(e);
        }

        public Task<Guid> CreateEvaluacionAsync(Evaluacion evaluacion,
                                                List<EvaluacionDetalle> detalles,
                                                List<PlanAccion> planAccion)
        {
            if (evaluacion.Id == Guid.Empty)
                evaluacion.Id = Guid.NewGuid();

            _evaluaciones.Add(evaluacion);
            foreach (var d in detalles)
            {
                d.Id = Guid.NewGuid();
                d.EvaluacionId = evaluacion.Id;
                _detalles.Add(d);
            }

            foreach (var p in planAccion)
            {
                p.Id = Guid.NewGuid();
                p.EvaluacionId = evaluacion.Id;
                _planes.Add(p);
            }

            return Task.FromResult(evaluacion.Id);
        }

        public Task UpdateEvaluacionAsync(Evaluacion evaluacion,
                                          List<EvaluacionDetalle> detalles,
                                          List<PlanAccion> planAccion)
        {
            var existing = _evaluaciones.FirstOrDefault(x => x.Id == evaluacion.Id);
            if (existing != null)
                _evaluaciones.Remove(existing);

            _evaluaciones.Add(evaluacion);

            _detalles.RemoveAll(x => x.EvaluacionId == evaluacion.Id);
            foreach (var d in detalles)
            {
                if (d.Id == Guid.Empty)
                    d.Id = Guid.NewGuid();
                d.EvaluacionId = evaluacion.Id;
                _detalles.Add(d);
            }

            _planes.RemoveAll(x => x.EvaluacionId == evaluacion.Id);
            foreach (var p in planAccion)
            {
                if (p.Id == Guid.Empty)
                    p.Id = Guid.NewGuid();
                p.EvaluacionId = evaluacion.Id;
                _planes.Add(p);
            }

            return Task.CompletedTask;
        }

        public Task<List<EvaluacionDetalle>> GetDetallesByEvaluacionAsync(Guid evaluacionId)
        {
            var lista = _detalles.Where(x => x.EvaluacionId == evaluacionId).ToList();
            return Task.FromResult(lista);
        }

        public Task<List<PlanAccion>> GetPlanesByEvaluacionAsync(Guid evaluacionId)
        {
            var lista = _planes.Where(x => x.EvaluacionId == evaluacionId).ToList();
            return Task.FromResult(lista);
        }
    }
}
