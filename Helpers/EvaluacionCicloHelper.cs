using System;
using System.Collections.Generic;
using System.Linq;
using EvaluacionDesempenoAB.Models;

namespace EvaluacionDesempenoAB.Helpers
{
    public enum TipoVentanaEvaluacion
    {
        PeriodoPrueba = 0,
        FinalizacionContrato = 1,
        ActivacionManual = 2
    }

    public sealed class VentanaEvaluacionActiva
    {
        public TipoVentanaEvaluacion Tipo { get; init; }
        public DateTime FechaReferencia { get; init; }
        public DateTime FechaInicio { get; init; }
        public DateTime FechaFin { get; init; }
        public string Clave { get; init; } = string.Empty;
    }

    public static class EvaluacionCicloHelper
    {
        private const int WindowDays = 25;

        public static bool IsWithinWindow(DateTime? targetDate, int windowDays = WindowDays, DateTime? today = null)
        {
            if (!targetDate.HasValue)
            {
                return false;
            }

            var currentDate = (today ?? DateTime.Today).Date;
            var start = targetDate.Value.Date.AddDays(-windowDays);
            var end = targetDate.Value.Date;
            return currentDate >= start && currentDate <= end;
        }

        public static bool IsWithinActivationWindow(DateTime? activationDate, int windowDays = WindowDays, DateTime? today = null)
        {
            if (!activationDate.HasValue)
            {
                return false;
            }

            var currentDate = (today ?? DateTime.Today).Date;
            var start = activationDate.Value.Date;
            var end = activationDate.Value.Date.AddDays(windowDays);
            return currentDate >= start && currentDate <= end;
        }

        public static VentanaEvaluacionActiva? ResolveVentanaActiva(UsuarioEvaluado usuario, DateTime? today = null)
        {
            var currentDate = (today ?? DateTime.Today).Date;
            var ventanasNaturales = new List<VentanaEvaluacionActiva>();

            AddNaturalWindow(
                ventanasNaturales,
                TipoVentanaEvaluacion.PeriodoPrueba,
                usuario.FechaFinalizacionPeriodoPrueba,
                currentDate);
            AddNaturalWindow(
                ventanasNaturales,
                TipoVentanaEvaluacion.FinalizacionContrato,
                usuario.FechaFinalizacionContrato,
                currentDate);

            if (ventanasNaturales.Count > 0)
            {
                return ventanasNaturales
                    .OrderBy(x => x.FechaReferencia)
                    .ThenBy(x => x.Tipo)
                    .First();
            }

            if (!IsWithinActivationWindow(usuario.FechaActivacionEvaluacion, today: currentDate))
            {
                return null;
            }

            var activationDate = usuario.FechaActivacionEvaluacion!.Value.Date;
            return new VentanaEvaluacionActiva
            {
                Tipo = TipoVentanaEvaluacion.ActivacionManual,
                FechaReferencia = activationDate,
                FechaInicio = activationDate,
                FechaFin = activationDate.AddDays(WindowDays),
                Clave = BuildKey(TipoVentanaEvaluacion.ActivacionManual, activationDate)
            };
        }

        public static bool PerteneceAVentanaInicial(Evaluacion evaluacion, VentanaEvaluacionActiva ventana)
        {
            if (evaluacion.Id == Guid.Empty || evaluacion.EvaluacionOrigenId.HasValue)
            {
                return false;
            }

            if (!string.Equals(evaluacion.TipoEvaluacion, "Inicial", StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            var fechaEvaluacion = evaluacion.FechaEvaluacion.Date;
            return fechaEvaluacion >= ventana.FechaInicio && fechaEvaluacion <= ventana.FechaFin;
        }

        public static bool PerteneceASeguimiento(Evaluacion evaluacion, Guid evaluacionOrigenId)
        {
            return evaluacion.Id != Guid.Empty &&
                   evaluacion.EvaluacionOrigenId == evaluacionOrigenId &&
                   string.Equals(evaluacion.TipoEvaluacion, "Seguimiento", StringComparison.OrdinalIgnoreCase);
        }

        public static string BuildLockKey(Guid usuarioId, VentanaEvaluacionActiva ventana)
            => $"inicial:{usuarioId:D}:{ventana.Clave}";

        public static string BuildLockKey(Guid evaluacionOrigenId)
            => $"seguimiento:{evaluacionOrigenId:D}";

        private static void AddNaturalWindow(
            ICollection<VentanaEvaluacionActiva> ventanas,
            TipoVentanaEvaluacion tipo,
            DateTime? referenceDate,
            DateTime currentDate)
        {
            if (!IsWithinWindow(referenceDate, today: currentDate))
            {
                return;
            }

            var fecha = referenceDate!.Value.Date;
            ventanas.Add(new VentanaEvaluacionActiva
            {
                Tipo = tipo,
                FechaReferencia = fecha,
                FechaInicio = fecha.AddDays(-WindowDays),
                FechaFin = fecha,
                Clave = BuildKey(tipo, fecha)
            });
        }

        private static string BuildKey(TipoVentanaEvaluacion tipo, DateTime fechaReferencia)
            => $"{tipo}:{fechaReferencia:yyyyMMdd}";
    }
}
