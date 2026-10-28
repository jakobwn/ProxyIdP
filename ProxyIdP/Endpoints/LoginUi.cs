using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Options;
using ProxyIdP.Options;

namespace ProxyIdP.Endpoints;

public static class LoginUi
{
    public const string DefaultCss =
        "body.login-page{font-family:sans-serif;max-width:380px;margin:4em auto}\n" +
        ".login-page input,.login-page button{width:100%;padding:.6em;margin:.3em 0;box-sizing:border-box}\n" +
        ".login-error{color:#b00}\n" +
        ".login-divider{margin:1.5em 0}\n";

    private static string Resolve(IHostEnvironment env, string path) =>
        Path.IsPathRooted(path) ? path : Path.Combine(env.ContentRootPath, path);

    public static void UseLoginUiFiles(this WebApplication app)
    {
        var ui = app.Services.GetRequiredService<IOptions<UiOptions>>().Value;

        var stylePath = Resolve(app.Environment, ui.StylePath);
        app.MapGet("/style/login.css", () =>
        {
            var file = Path.Combine(stylePath, "login.css");
            return File.Exists(file) ? Results.File(File.OpenRead(file), "text/css") : Results.Text(DefaultCss, "text/css");
        });

        if (!ui.CustomUi.Enabled) return;
        var root = Resolve(app.Environment, ui.CustomUi.Path);
        Directory.CreateDirectory(root);
        app.UseStaticFiles(new StaticFileOptions
        {
            FileProvider = new PhysicalFileProvider(root),
            RequestPath = "/customui"
        });
    }

    public static string? CustomIndexPath(IHostEnvironment env, UiOptions ui)
    {
        if (!ui.CustomUi.Enabled) return null;
        var file = Path.Combine(Resolve(env, ui.CustomUi.Path), "index.html");
        return File.Exists(file) ? file : null;
    }
}
