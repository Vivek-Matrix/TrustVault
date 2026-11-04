using System.Text;
using System.Security.Claims;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.Tokens;
using System.Threading.RateLimiting;
using TrustVault.Domain.Security;
using TrustVault.Api.Services;
using TrustVault.Application.Documents;
using TrustVault.Infrastructure.Documents;
using TrustVault.Infrastructure.Persistence;
using TrustVault.Infrastructure.Persistence.Entities;

var builder = WebApplication.CreateBuilder(args);
if (int.TryParse(Environment.GetEnvironmentVariable("PORT"), out var renderPort))
{
    builder.WebHost.UseUrls($"http://0.0.0.0:{renderPort}");
}

var connectionString = DatabaseConnectionStrings.Get(builder.Configuration);
var signingKey = builder.Configuration["Jwt:SigningKey"];
if (string.IsNullOrWhiteSpace(signingKey) || Encoding.UTF8.GetByteCount(signingKey) < 32)
{
    throw new InvalidOperationException("Jwt:SigningKey must contain at least 32 UTF-8 bytes.");
}

var issuer = builder.Configuration["Jwt:Issuer"] ?? "TrustVault";
var audience = builder.Configuration["Jwt:Audience"] ?? "TrustVault";
var accessTokenLifetimeMinutes = builder.Configuration.GetValue(
    "Jwt:AccessTokenLifetimeMinutes",
    15);
if (accessTokenLifetimeMinutes is < 5 or > 30)
{
    throw new InvalidOperationException(
        "Jwt:AccessTokenLifetimeMinutes must be between 5 and 30.");
}

builder.Services.AddProblemDetails();
builder.Services.AddControllers();
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();
builder.Services.AddScoped<IAuthenticationService, AuthenticationService>();
builder.Services.AddScoped<DocumentAuditWriter>();
builder.Services.AddHttpClient<IDocumentTextConverter, DocumentTextConverterClient>(
    client => client.Timeout = TimeSpan.FromSeconds(45));
var documentStorageRoot = builder.Configuration["Storage:RootPath"]
    ?? Path.Combine(builder.Environment.ContentRootPath, "App_Data", "documents");
var scannerHost = builder.Configuration["MalwareScanning:ClamAv:Host"] ?? "127.0.0.1";
var scannerPort = builder.Configuration.GetValue("MalwareScanning:ClamAv:Port", 3310);
var scannerTimeoutSeconds = builder.Configuration.GetValue(
    "MalwareScanning:ClamAv:TimeoutSeconds",
    45);
if (string.IsNullOrWhiteSpace(scannerHost)
    || scannerPort is < 1 or > 65535
    || scannerTimeoutSeconds is < 1 or > 300)
{
    throw new InvalidOperationException("The ClamAV scanning configuration is invalid.");
}
var authenticationRateLimit = builder.Configuration.GetValue(
    "RateLimits:AuthenticationPermitLimit",
    10);
builder.Services.AddSingleton<IPrivateDocumentStorage>(
    new FileSystemPrivateDocumentStorage(documentStorageRoot));
builder.Services.AddSingleton<IPrivateDocumentScanner>(
    new ClamAvPrivateDocumentScanner(
        scannerHost,
        scannerPort,
        TimeSpan.FromSeconds(scannerTimeoutSeconds)));
builder.Services.AddRateLimiter(options =>
{
    options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
    options.AddPolicy("authentication", context =>
        RateLimitPartition.GetFixedWindowLimiter(
            $"{context.Connection.RemoteIpAddress?.ToString() ?? "unknown"}:{context.Request.Path}",
            _ => new FixedWindowRateLimiterOptions
            {
                PermitLimit = authenticationRateLimit,
                Window = TimeSpan.FromMinutes(1),
                QueueLimit = 0,
                AutoReplenishment = true
            }));
    options.OnRejected = async (context, cancellationToken) =>
    {
        context.HttpContext.Response.ContentType = "application/problem+json";
        await context.HttpContext.Response.WriteAsJsonAsync(new ProblemDetails
        {
            Status = StatusCodes.Status429TooManyRequests,
            Title = "Too many authentication requests.",
            Detail = "Wait before trying again."
        }, cancellationToken);
    };
});
builder.Services.AddDbContext<ApplicationDbContext>(options =>
    options.UseNpgsql(connectionString));
builder.Services
    .AddIdentityCore<ApplicationUser>(options =>
    {
        options.User.RequireUniqueEmail = true;
        options.Password.RequiredLength = 12;
        options.Password.RequireDigit = true;
        options.Password.RequireUppercase = true;
        options.Password.RequireLowercase = true;
        options.Password.RequireNonAlphanumeric = true;
        options.Lockout.MaxFailedAccessAttempts = 5;
        options.Lockout.DefaultLockoutTimeSpan = TimeSpan.FromMinutes(15);
    })
    .AddRoles<IdentityRole<Guid>>()
    .AddEntityFrameworkStores<ApplicationDbContext>()
    .AddSignInManager()
    .AddDefaultTokenProviders();
builder.Services
    .AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
    .AddJwtBearer(options =>
    {
        options.TokenValidationParameters = new TokenValidationParameters
        {
            ValidateIssuer = true,
            ValidIssuer = issuer,
            ValidateAudience = true,
            ValidAudience = audience,
            ValidateIssuerSigningKey = true,
            IssuerSigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(signingKey)),
            ValidateLifetime = true,
            ClockSkew = TimeSpan.FromSeconds(30)
        };
        options.Events = new JwtBearerEvents
        {
            OnTokenValidated = async context =>
            {
                var userIdClaim = context.Principal?.FindFirstValue(ClaimTypes.NameIdentifier);
                var tokenSecurityStamp =
                    context.Principal?.FindFirstValue("security_stamp");
                if (!Guid.TryParse(userIdClaim, out var userId)
                    || string.IsNullOrWhiteSpace(tokenSecurityStamp))
                {
                    context.Fail("The access token is missing required account claims.");
                    return;
                }

                var database = context.HttpContext.RequestServices
                    .GetRequiredService<ApplicationDbContext>();
                var account = await database.Users
                    .AsNoTracking()
                    .SingleOrDefaultAsync(
                        user => user.Id == userId,
                        context.HttpContext.RequestAborted);
                if (account is null
                    || account.IsSuspended
                    || !string.Equals(
                        account.SecurityStamp,
                        tokenSecurityStamp,
                        StringComparison.Ordinal))
                {
                    context.Fail("The account is inactive or its access token has been revoked.");
                    return;
                }

                var passwordChangeRequest =
                    HttpMethods.IsPost(context.Request.Method)
                    && context.Request.Path.Equals(
                        "/api/auth/change-password",
                        StringComparison.OrdinalIgnoreCase);
                if (account.MustChangePassword && !passwordChangeRequest)
                {
                    context.Fail("The account must change its temporary password.");
                }
            }
        };
    });
builder.Services.AddAuthorization(options =>
{
    options.FallbackPolicy = new AuthorizationPolicyBuilder()
        .RequireAuthenticatedUser()
        .Build();
    options.AddPolicy("AdminPolicy", policy => policy.RequireRole(UserRoles.Administrator));
});

var frontendOrigin = builder.Configuration["Frontend:Origin"] ?? "http://localhost:4200";
builder.Services.AddCors(options =>
    options.AddDefaultPolicy(policy =>
        policy.WithOrigins(frontendOrigin).AllowAnyHeader().AllowAnyMethod()));

var app = builder.Build();
await AdministratorBootstrapper.InitializeAsync(app.Services, builder.Configuration);

app.UseExceptionHandler();
app.UseDefaultFiles();
app.UseStaticFiles();
app.UseRouting();
app.UseCors();
app.UseRateLimiter();
app.UseAuthentication();
app.UseAuthorization();

if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI();
}

app.MapControllers();
app.MapFallback("/api/{**path}", () => Results.NotFound()).AllowAnonymous();
app.MapFallbackToFile("index.html").AllowAnonymous();
app.Run();

public partial class Program { }
