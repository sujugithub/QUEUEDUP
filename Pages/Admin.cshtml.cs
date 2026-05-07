using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using System.Text;
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

    public async Task<IActionResult> OnGetExportCsvAsync()
    {
        if (HttpContext.Session.GetString("AdminAuth") != "1")
            return RedirectToPage();

        var emails = await _db.EmailSignups.OrderByDescending(e => e.SignedUpAt).ToListAsync();
        var csv = new StringBuilder("Email,SignedUpAt\r\n");
        foreach (var e in emails)
            csv.AppendLine($"{e.Email},{e.SignedUpAt:yyyy-MM-dd HH:mm:ss}");

        return File(Encoding.UTF8.GetBytes(csv.ToString()), "text/csv", "queuedup-subscribers.csv");
    }

    public async Task<IActionResult> OnPostDeleteAsync(int id)
    {
        if (HttpContext.Session.GetString("AdminAuth") != "1")
            return RedirectToPage();

        var signup = await _db.EmailSignups.FindAsync(id);
        if (signup != null)
        {
            _db.EmailSignups.Remove(signup);
            await _db.SaveChangesAsync();
        }
        return RedirectToPage();
    }
}
