using System;
using System.Globalization;
using System.Text;
using EvaluacionDesempenoAB.Models;

namespace EvaluacionDesempenoAB.Helpers
{
    [Flags]
    public enum TipoParteEvaluacion
    {
        Ninguna = 0,
        Normal = 1,
        Sst = 2
    }

    public static class EvaluacionRolesHelper
    {
        public const string CompetenciaSstNombre = "CULTURA SST";

        public static TipoParteEvaluacion ResolveParte(
            UsuarioEvaluado usuarioObjetivo,
            string? correoActual,
            bool esSuperAdministrador = false)
        {
            if (esSuperAdministrador)
            {
                return TipoParteEvaluacion.Normal | TipoParteEvaluacion.Sst;
            }

            if (string.IsNullOrWhiteSpace(correoActual))
            {
                return TipoParteEvaluacion.Ninguna;
            }

            if (SonCorreosIguales(usuarioObjetivo.CorreoEvaluadorSst, correoActual))
            {
                return TipoParteEvaluacion.Sst;
            }

            if (SonCorreosIguales(usuarioObjetivo.EvaluadorNombre, correoActual))
            {
                return TipoParteEvaluacion.Normal;
            }

            return TipoParteEvaluacion.Ninguna;
        }

        public static bool TieneParte(TipoParteEvaluacion parte, TipoParteEvaluacion requerida)
            => (parte & requerida) == requerida;

        public static bool TieneAcceso(TipoParteEvaluacion parte)
            => parte != TipoParteEvaluacion.Ninguna;

        public static bool EsCompetenciaSst(string? nombreCompetencia)
        {
            if (string.IsNullOrWhiteSpace(nombreCompetencia))
            {
                return false;
            }

            var nombreNormalizado = Normalizar(nombreCompetencia);
            return nombreNormalizado.Contains("CULTURA SST", StringComparison.OrdinalIgnoreCase)
                || nombreNormalizado.Contains("CULUTA SST", StringComparison.OrdinalIgnoreCase)
                || (nombreNormalizado.Contains("CULTURA", StringComparison.OrdinalIgnoreCase)
                    && nombreNormalizado.Contains("SST", StringComparison.OrdinalIgnoreCase))
                || (nombreNormalizado.Contains("CULUTA", StringComparison.OrdinalIgnoreCase)
                    && nombreNormalizado.Contains("SST", StringComparison.OrdinalIgnoreCase));
        }

        public static bool DebeVerCompetencia(TipoParteEvaluacion parte, string? nombreCompetencia)
        {
            if (parte == TipoParteEvaluacion.Ninguna)
            {
                return false;
            }

            if (parte == (TipoParteEvaluacion.Normal | TipoParteEvaluacion.Sst))
            {
                return true;
            }

            var esSst = EsCompetenciaSst(nombreCompetencia);
            return esSst
                ? TieneParte(parte, TipoParteEvaluacion.Sst)
                : TieneParte(parte, TipoParteEvaluacion.Normal);
        }

        public static string GetEtiquetaParte(TipoParteEvaluacion parte)
        {
            if (parte == (TipoParteEvaluacion.Normal | TipoParteEvaluacion.Sst))
            {
                return "Evaluación completa";
            }

            if (TieneParte(parte, TipoParteEvaluacion.Sst))
            {
                return "Evaluación SST";
            }

            if (TieneParte(parte, TipoParteEvaluacion.Normal))
            {
                return "Evaluación normal";
            }

            return "Sin acceso";
        }

        public static bool SonCorreosIguales(string? left, string? right)
            => string.Equals(
                left?.Trim(),
                right?.Trim(),
                StringComparison.OrdinalIgnoreCase);

        private static string Normalizar(string value)
        {
            var normalized = value.Trim().Normalize(NormalizationForm.FormD);
            var builder = new StringBuilder(normalized.Length);

            foreach (var ch in normalized)
            {
                if (CharUnicodeInfo.GetUnicodeCategory(ch) != UnicodeCategory.NonSpacingMark)
                {
                    builder.Append(ch);
                }
            }

            return builder.ToString().Normalize(NormalizationForm.FormC);
        }
    }
}
