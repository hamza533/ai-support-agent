using System.Security.Claims;
using System.Security.Cryptography;
using Brightline.Data;
using Brightline.Data.Entities;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.AspNetCore.Components.Server;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Options;

namespace Brightline.Web.Infrastructure;

public sealed class EntraIdOptions
{
    public const string Section = "Auth:EntraId";
    public bool Enabled { get; set; }
    public string TenantId { get; set; } = string.Empty;
    public string ClientId { get; set; } = string.Empty;
    public string ClientSecret { get; set; } = string.Empty;
}

public static class AuthExtensions
{
    public const string EntraScheme = "EntraId";

    public static IServiceCollection AddBrightlineAuth(this IServiceCollection services, IConfiguration configuration, IHostEnvironment env)
    {
        services.AddCascadingAuthenticationState();
        services.AddScoped<AuthenticationStateProvider, IdentityRevalidatingAuthenticationStateProvider>();

        var auth = services.AddAuthentication(o =>
        {
            o.DefaultScheme = IdentityConstants.ApplicationScheme;
            o.DefaultSignInScheme = IdentityConstants.ExternalScheme;
        });
        auth.AddIdentityCookies(cookies => cookies.ApplicationCookie?.Configure(o =>
        {
            o.LoginPath = "/account/login";
            o.LogoutPath = "/account/logout";
            o.AccessDeniedPath = "/account/denied";
            o.Cookie.Name = "brightline.auth";
            o.Cookie.HttpOnly = true;
            o.Cookie.SameSite = SameSiteMode.Lax;
            o.Cookie.SecurePolicy = env.IsDevelopment() || env.IsEnvironment("Testing")
                ? CookieSecurePolicy.SameAsRequest
                : CookieSecurePolicy.Always;
            o.ExpireTimeSpan = TimeSpan.FromHours(8);
            o.SlidingExpiration = true;
        }));

        var entra = configuration.GetSection(EntraIdOptions.Section).Get<EntraIdOptions>() ?? new EntraIdOptions();
        services.Configure<EntraIdOptions>(configuration.GetSection(EntraIdOptions.Section));
        if (entra.Enabled)
        {
            auth.AddOpenIdConnect(EntraScheme, "Microsoft Entra ID", o =>
            {
                o.Authority = $"https://login.microsoftonline.com/{entra.TenantId}/v2.0";
                o.ClientId = entra.ClientId;
                o.ClientSecret = string.IsNullOrEmpty(entra.ClientSecret) ? null : entra.ClientSecret;
                o.ResponseType = "code";
                o.UsePkce = true;
                o.CallbackPath = "/signin-oidc";
                o.SignInScheme = IdentityConstants.ExternalScheme;
                o.Scope.Add("email");
                o.MapInboundClaims = false;
                o.TokenValidationParameters.NameClaimType = "name";
            });
        }

        services.AddIdentityCore<AppUser>(o =>
            {
                o.SignIn.RequireConfirmedAccount = false;
                o.User.RequireUniqueEmail = true;
                o.Password.RequiredLength = 12;
                o.Lockout.MaxFailedAccessAttempts = 5;
                o.Lockout.DefaultLockoutTimeSpan = TimeSpan.FromMinutes(15);
            })
            .AddRoles<IdentityRole>()
            .AddEntityFrameworkStores<AppDbContext>()
            .AddSignInManager()
            .AddDefaultTokenProviders();

        services.AddAuthorizationBuilder()
            .AddPolicy("staff", p => p.RequireRole(Roles.Admin, Roles.Staff))
            .AddPolicy("admin", p => p.RequireRole(Roles.Admin));

        services.AddDataProtection()
            .SetApplicationName("brightline")
            .PersistKeysToDbContext<AppDbContext>();

        return services;
    }

    /// <summary>
    /// Entra ID sign-in maps to an existing local staff account with the same email (provisioned by an admin):
    /// roles stay managed in one place, and unknown tenants' users cannot get in.
    /// </summary>
    public static IEndpointRouteBuilder MapExternalLogin(this IEndpointRouteBuilder app)
    {
        app.MapGet("/account/external/{scheme}", (string scheme, string? returnUrl) =>
            TypedResults.Challenge(
                new AuthenticationProperties { RedirectUri = $"/account/external-callback?returnUrl={Uri.EscapeDataString(SafeReturnUrl(returnUrl))}" },
                [scheme]));

        app.MapGet("/account/external-callback", async (
            string? returnUrl,
            HttpContext http,
            UserManager<AppUser> users,
            SignInManager<AppUser> signIn,
            ILogger<Program> logger) =>
        {
            var external = await http.AuthenticateAsync(IdentityConstants.ExternalScheme);
            var email = external.Principal?.FindFirstValue("email")
                ?? external.Principal?.FindFirstValue("preferred_username");
            await http.SignOutAsync(IdentityConstants.ExternalScheme);

            var user = email is null ? null : await users.FindByEmailAsync(email);
            if (user is null || !(await users.IsInRoleAsync(user, Roles.Admin) || await users.IsInRoleAsync(user, Roles.Staff)))
            {
                logger.LogWarning("Entra ID sign-in rejected for {User}", PiiMasker.MaskEmail(email));
                return Results.Redirect("/account/denied");
            }

            await signIn.SignInAsync(user, isPersistent: false, authenticationMethod: EntraScheme);
            return Results.LocalRedirect(SafeReturnUrl(returnUrl));
        });

        app.MapPost("/account/logout", async (SignInManager<AppUser> signIn) =>
        {
            await signIn.SignOutAsync();
            return Results.LocalRedirect("/");
        });

        return app;
    }

    public static string SafeReturnUrl(string? returnUrl) =>
        !string.IsNullOrEmpty(returnUrl) && returnUrl.StartsWith('/') && !returnUrl.StartsWith("//", StringComparison.Ordinal)
            && !returnUrl.StartsWith("/\\", StringComparison.Ordinal)
            ? returnUrl
            : "/admin";
}

/// <summary>Re-checks the Identity security stamp every 30 minutes for interactive circuits.</summary>
internal sealed class IdentityRevalidatingAuthenticationStateProvider(
    ILoggerFactory loggerFactory,
    IServiceScopeFactory scopeFactory,
    IOptions<IdentityOptions> options)
    : RevalidatingServerAuthenticationStateProvider(loggerFactory)
{
    protected override TimeSpan RevalidationInterval => TimeSpan.FromMinutes(30);

    protected override async Task<bool> ValidateAuthenticationStateAsync(AuthenticationState authenticationState, CancellationToken cancellationToken)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var users = scope.ServiceProvider.GetRequiredService<UserManager<AppUser>>();
        var principal = authenticationState.User;
        var user = await users.GetUserAsync(principal);
        if (user is null)
        {
            return false;
        }

        if (!users.SupportsUserSecurityStamp)
        {
            return true;
        }

        var principalStamp = principal.FindFirstValue(options.Value.ClaimsIdentity.SecurityStampClaimType);
        return principalStamp == await users.GetSecurityStampAsync(user);
    }
}

/// <summary>Creates roles, the first admin (<c>--create-admin</c>), and the read-only demo staff user.</summary>
public sealed class IdentityBootstrapper(
    RoleManager<IdentityRole> roles,
    UserManager<AppUser> users,
    ILogger<IdentityBootstrapper> logger)
{
    public async Task EnsureRolesAsync()
    {
        foreach (var role in new[] { Roles.Admin, Roles.Staff })
        {
            if (!await roles.RoleExistsAsync(role))
            {
                await roles.CreateAsync(new IdentityRole(role));
            }
        }
    }

    /// <summary>Admin console "Add user": creates a NEW account only (never resets an existing one). Returns the generated password.</summary>
    public async Task<string> CreateNewUserAsync(string email, string role)
    {
        if (await users.FindByEmailAsync(email) is not null)
        {
            throw new InvalidOperationException("A user with this email already exists.");
        }

        return await CreateUserAsync(email, role, null);
    }

    /// <summary>Creates (or resets, for the --create-admin command) a user in a role. Returns the password used (generated when none is given).</summary>
    public async Task<string> CreateUserAsync(string email, string role, string? password, string? displayName = null)
    {
        await EnsureRolesAsync();
        password ??= GeneratePassword();
        var user = await users.FindByEmailAsync(email);
        if (user is null)
        {
            user = new AppUser { UserName = email, Email = email, EmailConfirmed = true, DisplayName = displayName ?? email };
            Check(await users.CreateAsync(user, password));
        }
        else
        {
            var token = await users.GeneratePasswordResetTokenAsync(user);
            Check(await users.ResetPasswordAsync(user, token, password));
        }

        if (!await users.IsInRoleAsync(user, role))
        {
            Check(await users.AddToRoleAsync(user, role));
        }

        logger.LogInformation("User {User} ready with role {Role}", PiiMasker.MaskEmail(email), role);
        return password;
    }

    public static string GeneratePassword() =>
        "Bl-" + Convert.ToBase64String(RandomNumberGenerator.GetBytes(18)).Replace('/', 'x').Replace('+', 'y') + "!7";

    private static void Check(IdentityResult result)
    {
        if (!result.Succeeded)
        {
            throw new InvalidOperationException(string.Join("; ", result.Errors.Select(e => e.Description)));
        }
    }
}
