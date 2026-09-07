using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using System.Security.Claims;

namespace TurneroWorker.Pages;

public class LoginModel : PageModel
{
    private readonly IConfiguration _config;

    public LoginModel(IConfiguration config)
    {
        _config = config;
    }

    [BindProperty]
    public string Username { get; set; } = "";

    [BindProperty]
    public string Password { get; set; } = "";

    public string? Error { get; set; }

    public IActionResult OnGet()
    {
        if (User.Identity?.IsAuthenticated == true)
            return Redirect("/turnos");
        return Page();
    }

    public async Task<IActionResult> OnPostAsync()
    {
        var validUser = _config["Auth:Username"];
        var validPass = _config["Auth:Password"];

        if (Username == validUser && Password == validPass)
        {
            var claims = new List<Claim>
            {
                new Claim(ClaimTypes.Name, Username)
            };
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

        Error = "Usuario o contrasena incorrectos.";
        return Page();
    }
}
