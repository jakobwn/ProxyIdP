using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.EntityFrameworkCore;
using ProxyIdP.Data;
using ProxyIdP.Endpoints;
using ProxyIdP.Options;
using ProxyIdP.Services;

namespace ProxyIdP
{
	public class Program
	{
		public static async Task Main(string[] args)
		{
			var builder = WebApplication.CreateBuilder(args);
						builder.Configuration.AddJsonFile(ConfigPaths.CustomConfig, optional: true, reloadOnChange: false);
						builder.Configuration.AddEnvironmentVariables();
						if (args.Length > 0) builder.Configuration.AddCommandLine(args);
						var config = builder.Configuration;
						var clients = ClientOptions.FromConfiguration(config);

			EnsureDataDirectories(config);

			if (config.GetValue<bool>("ForwardedHeaders:Enabled"))
			{
				builder.Services.Configure<ForwardedHeadersOptions>(o =>
				{
					o.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto | ForwardedHeaders.XForwardedHost;
					o.KnownIPNetworks.Clear();
					o.KnownProxies.Clear();
				});
			}

			builder.Services.Configure<ProxyOptions>(config.GetSection("Proxy"));
			builder.Services.Configure<UiOptions>(config.GetSection("Ui"));
			builder.Services.Configure<SigningKeyOptions>(config.GetSection("SigningKeys"));
			builder.Services.Configure<IdpResolverOptions>(config.GetSection("IdpResolver"));
			builder.Services.AddSingleton(UpstreamIdpRegistry.FromConfiguration(config));

			builder.Services.AddSingleton(TimeProvider.System);
			builder.Services.AddDbContext<ProxyIdPDbContext>(o => o.UseSqlite(config.GetConnectionString("Default")));
			var dp = builder.Services.AddDataProtection().SetApplicationName("ProxyIdP");
			if (!string.IsNullOrWhiteSpace(config["DataProtection:KeyPath"]))
				dp.PersistKeysToFileSystem(new DirectoryInfo(config["DataProtection:KeyPath"]!));
			builder.Services.AddAntiforgery(o => o.HeaderName = "X-CSRF-TOKEN");
			builder.Services.AddHttpClient();
			builder.Services.AddHttpClient(IdpResolver.HttpClientName);
			builder.Services.AddSingleton<IIdpResolver, IdpResolver>();

			builder.Services.AddSingleton<ISigningKeyStore, SigningKeyStore>();
			builder.Services.AddSingleton<IUpstreamIdpClient, UpstreamIdpClient>();
			builder.Services.AddSingleton<TokenService>();
			builder.Services.AddHostedService<SigningKeyRotationService>();
			builder.Services.AddHostedService<GrantCleanupService>();

			var app = builder.Build();

			using (var scope = app.Services.CreateScope())
			{
				var db = scope.ServiceProvider.GetRequiredService<ProxyIdPDbContext>();
				await db.Database.EnsureCreatedAsync();
				await SeedClientsAsync(db, clients);
			}
			await app.Services.GetRequiredService<ISigningKeyStore>().EnsureKeysAsync();

			if (config.GetValue<bool>("ForwardedHeaders:Enabled")) app.UseForwardedHeaders();
			if (config.GetValue("Hosting:UseHttpsRedirection", true)) app.UseHttpsRedirection();
			app.UseAntiforgery();
			app.UseLoginUiFiles();
			app.MapOidc();
			app.MapGet("/healthz", async (ProxyIdPDbContext db, CancellationToken ct) =>
				await db.Database.CanConnectAsync(ct) ? Results.Ok("Healthy") : Results.StatusCode(StatusCodes.Status503ServiceUnavailable));
			app.MapGet("/", () => "ProxyIdP is running.");

			await app.RunAsync();
		}

		private static void EnsureDataDirectories(IConfiguration config)
		{
			var cs = config.GetConnectionString("Default");
			if (!string.IsNullOrWhiteSpace(cs))
			{
				var file = new Microsoft.Data.Sqlite.SqliteConnectionStringBuilder(cs).DataSource;
				var dir = string.IsNullOrEmpty(file) ? null : Path.GetDirectoryName(Path.GetFullPath(file));
				if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
			}
			var keyPath = config["DataProtection:KeyPath"];
			if (!string.IsNullOrWhiteSpace(keyPath)) Directory.CreateDirectory(keyPath);
		}

		public static async Task SeedClientsAsync(ProxyIdPDbContext db, IEnumerable<ClientOptions> configured)
		{
			foreach (var opt in configured)
			{
				var client = await db.Clients.FirstOrDefaultAsync(c => c.ClientId == opt.ClientId);
				if (client is null)
				{
					client = new Client { ClientId = opt.ClientId };
					db.Clients.Add(client);
				}
				client.SecretHash = string.IsNullOrEmpty(opt.Secret) ? null : Crypto.Sha256(opt.Secret);
				client.RedirectUris = [.. opt.RedirectUris];
				client.PostLogoutRedirectUris = [.. opt.PostLogoutRedirectUris];
				client.AllowedScopes = opt.AllowedScopes.Count > 0 ? [.. opt.AllowedScopes] : [.. ClientOptions.DefaultScopes];
			}
			await db.SaveChangesAsync();
		}
	}
}
