using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace EvaluacionDesempenoAB.Controllers;

[AllowAnonymous]
public class AccountController : Controller
{
    [HttpGet]
    public IActionResult Login(string? returnUrl = null)
    {
        var redirectUrl = string.IsNullOrWhiteSpace(returnUrl)
            ? Url.Action("Index", "Home") ?? "/"
            : returnUrl;

        return Challenge(new AuthenticationProperties
        {
            RedirectUri = redirectUrl
        }, OpenIdConnectDefaults.AuthenticationScheme);
    }

    [HttpGet]
    public IActionResult Logout()
    {
        var callbackUrl = Url.Action("Index", "Home", values: null, protocol: Request.Scheme) ?? "/";

        return SignOut(new AuthenticationProperties
        {
            RedirectUri = callbackUrl
        },
        CookieAuthenticationDefaults.AuthenticationScheme,
        OpenIdConnectDefaults.AuthenticationScheme);
    }

    [HttpGet]
    public IActionResult AccessDenied(string? returnUrl = null)
    {
        ViewData["ReturnUrl"] = returnUrl;
        return View();
    }
}
