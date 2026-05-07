using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using QUEUEDUP.Data;

namespace QUEUEDUP.Pages;

public class AdminModel : PageModel
{
    private readonly AppDbContext   _db;
    private readonly IConfiguration _config;

    public bool              Authenticated { get; set; }
    public bool              LoginFailed   { get; set; }
    public List<EmailSignup> Emails        { get; set; } = new();

    public AdminModel(AppDbContext db, IConfiguration config)
    {
        _db     = db;
        _config = config;
    }

    public async Task OnGetAsync()
    {
        Authenticated = HttpContext.Session.GetString("AdminAuth") == "1";
        if (Authenticated)
            Emails = await _db.EmailSignups.OrderByDescending(e => e.SignedUpAt).ToListAsync();
    }

    public async Task<IActionResult> OnPostAsync(string? password)
    {
        var correct = _config["Admin:Password"] ?? "admin123";
        if (password == correct)
        {
            HttpContext.Session.SetString("AdminAuth", "1");
            Emails = await _db.EmailSignups.OrderByDescending(e => e.SignedUpAt).ToListAsync();
            Authenticated = true;
        }
        else
        {
            LoginFailed = true;
        }
        return Page();
    }

    public IActionResult OnPostLogout()
    {
        HttpContext.Session.Remove("AdminAuth");
        return RedirectToPage();
    }
}
