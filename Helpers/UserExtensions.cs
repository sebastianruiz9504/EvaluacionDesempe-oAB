using System.Security.Claims;

namespace EvaluacionDesempenoAB.Helpers
{
    public static class UserExtensions
    {
        public static Guid? GetObjectId(this ClaimsPrincipal user)
        {
            var oid = user.FindFirstValue("http://schemas.microsoft.com/identity/claims/objectidentifier")
                      ?? user.FindFirstValue("oid");
            if (Guid.TryParse(oid, out var guid))
                return guid;
            return null;
        }
    }
}
