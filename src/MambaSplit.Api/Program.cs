using System.Net;
using System.Text;
using MambaSplit.Api.Contracts;
using MambaSplit.Api.Middleware;
using MambaSplit.Api.Configuration;
using MambaSplit.Api.Data;
using MambaSplit.Api.Security;
using MambaSplit.Api.Services;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.Tokens;

var builder = WebApplication.CreateBuilder(args);


builder.Services.Configure<AppSecurityOptions>(builder.Configuration.GetSection("app:security"));
builder.Services.Configure<EmailOptions>(builder.Configuration.GetSection("Email"));

builder.Services.AddControllers();
var corsOrigins = builder.Configuration.GetSection("app:cors:origins").Get<string[]>()
    ?? new[] { "http://localhost:5173", "http://127.0.0.1:5173", "http://localhost:3000", "http://127.0.0.1:3000" };
builder.Services.AddCors(options =>
{
    options.AddPolicy("WebClient", policy =>
    {
        policy.WithOrigins(corsOrigins)
            .AllowAnyHeader()
            .AllowAnyMethod();
    });
});
builder.Services.Configure<ApiBehaviorOptions>(options =>
{
    options.InvalidModelStateResponseFactory = context =>
    {
        var message = string.Join(
            "; ",
            context.ModelState
                .Where(kvp => kvp.Value?.Errors.Count > 0)
                .SelectMany(kvp => kvp.Value!.Errors.Select(err =>
                {
                    var key = string.IsNullOrWhiteSpace(kvp.Key) ? "request" : kvp.Key;
                    var normalized = key.Replace("$.", string.Empty);
                    var errorMessage = string.IsNullOrWhiteSpace(err.ErrorMessage) ? "is invalid" : err.ErrorMessage;
                    return $"{normalized}: {errorMessage}";
                })));

        if (string.IsNullOrWhiteSpace(message))
        {
            message = "Request validation failed.";
        }

        return new BadRequestObjectResult(new ErrorResponse(
            "VALIDATION_FAILED",
            message,
            DateTimeOffset.UtcNow.ToString("O")));
    };
});

if (IsPublicDocsEnabled(builder.Environment.EnvironmentName))
{
    builder.Services.AddEndpointsApiExplorer();
    builder.Services.AddSwaggerGen();
}

var jwtIssuer = builder.Configuration["app:security:jwt:issuer"] ?? "mambasplit-api";
var jwtSecret = builder.Configuration["app:security:jwt:secret"] ?? string.Empty;
if (string.IsNullOrWhiteSpace(jwtSecret))
{
    throw new InvalidOperationException("app.security.jwt.secret is required.");
}

var key = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(jwtSecret));
builder.Services
    .AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
    .AddJwtBearer(options =>
    {
        options.MapInboundClaims = false;
        options.TokenValidationParameters = new TokenValidationParameters
        {
            ValidateIssuer = true,
            ValidIssuer = jwtIssuer,
            ValidateAudience = false,
            ValidateIssuerSigningKey = true,
            IssuerSigningKey = key,
            ValidateLifetime = true,
            ClockSkew = TimeSpan.Zero,
            NameClaimType = "sub",
        };
        options.Events = new JwtBearerEvents
        {
            OnChallenge = context =>
            {
                context.HandleResponse();
                context.Response.StatusCode = (int)HttpStatusCode.Unauthorized;
                context.Response.ContentType = "application/json";
                return context.Response.WriteAsJsonAsync(new ErrorResponse(
                    "AUTHENTICATION_FAILED",
                    "Invalid access token",
                    DateTimeOffset.UtcNow.ToString("O")));
            },
        };
    });

builder.Services.AddAuthorization(options =>
{
    options.FallbackPolicy = new AuthorizationPolicyBuilder()
        .RequireAuthenticatedUser()
        .Build();
});

var connectionString = ConnectionStringResolver.Resolve(builder.Configuration);
if (string.IsNullOrWhiteSpace(connectionString))
{
    throw new InvalidOperationException("Database connection is not configured.");
}

builder.Services.AddDbContext<AppDbContext>(options => options.UseNpgsql(connectionString));

builder.Services.AddScoped<JwtService>();
builder.Services.AddScoped<IGoogleTokenVerifier, GoogleIdTokenVerifierService>();
builder.Services.AddScoped<AuthService>();
builder.Services.AddScoped<GroupMembershipService>();
builder.Services.AddScoped<GroupService>();
builder.Services.AddScoped<ExpenseService>();
builder.Services.AddScoped<SettlementService>();
builder.Services.AddScoped<IEmailTemplateRenderer, FileEmailTemplateRenderer>();
builder.Services.AddHttpClient<IEmailSender, Smtp2GoEmailSender>();
builder.Services.AddScoped<TransactionalEmailService>();

var app = builder.Build();

var runMigrationsOnStartup = builder.Configuration.GetValue("app:database:runMigrationsOnStartup", true);
if (runMigrationsOnStartup)
{
    DatabaseMigrationRunner.Run(connectionString, app.Logger);
}

app.UseMiddleware<ApiExceptionMiddleware>();
app.UseCors("WebClient");
app.UseStaticFiles();

if (IsPublicDocsEnabled(app.Environment.EnvironmentName))
{
    app.UseSwagger();
    app.UseSwaggerUI();
}

app.UseAuthentication();
app.UseAuthorization();

app.MapGet("/health", () => Results.Ok(new { status = "ok" })).AllowAnonymous();
app.MapControllers();

app.Run();

static bool IsPublicDocsEnabled(string environmentName)
{
    var env = environmentName.ToLowerInvariant();
    return env is "local" or "dev" or "test" or "development";
}

public partial class Program
{
}
