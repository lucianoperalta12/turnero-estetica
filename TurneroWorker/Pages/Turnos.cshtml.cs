using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using System.Security.Claims;

namespace TurneroWorker.Pages;

public class TurnosModel : PageModel
{
    private readonly IConfiguration _config;

    public TurnosModel(IConfiguration config)
    {
        _config = config;
    }

    public bool IsAuthenticated { get; private set; }
    public bool LoginError { get; private set; }

    [BindProperty]
    public string LoginUsername { get; set; } = "";

    [BindProperty]
    public string LoginPassword { get; set; } = "";

    public void OnGet()
    {
        IsAuthenticated = User.Identity?.IsAuthenticated == true;
    }

    public async Task<IActionResult> OnPostAsync()
    {
        var validUser = _config["Auth:Username"];
        var validPass = _config["Auth:Password"];

        if (LoginUsername == validUser && LoginPassword == validPass)
        {
            var claims = new List<Claim> { new Claim(ClaimTypes.Name, LoginUsername) };
            var identity = new ClaimsIdentity(claims, CookieAuthenticationDefaults.AuthenticationScheme);
            var principal = new ClaimsPrincipal(identity);

            await HttpContext.SignInAsync(
                CookieAuthenticationDefaults.AuthenticationScheme,
                principal,
                new AuthenticationProperties
                {
                    IsPersistent = true,
                    ExpiresUtc = DateTimeOffset.UtcNow.AddDays(400)
                });

            return Redirect("/turnos");
        }

        IsAuthenticated = false;
        LoginError = true;
        return Page();
    }
}
