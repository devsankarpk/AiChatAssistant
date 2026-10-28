using System.Text;
using AiChatAssistant.Api.Common;
using AiChatAssistant.Api.Data;
using AiChatAssistant.Api.Dtos.Auth;
using AiChatAssistant.Api.Dtos.Chat;
using AiChatAssistant.Api.Dtos.Direct;
using AiChatAssistant.Api.Hubs;
using AiChatAssistant.Api.Messaging;
using AiChatAssistant.Api.Realtime;
using AiChatAssistant.Api.Security;
using Microsoft.AspNetCore.HttpOverrides;
using AiChatAssistant.Api.Middleware;
using AiChatAssistant.Api.Services;
using AiChatAssistant.Api.Validators;
using FluentValidation;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.Tokens;
using Microsoft.OpenApi.Models;

var builder = WebApplication.CreateBuilder(args);

// Add services to the container.

var connectionString = builder.Configuration.GetConnectionString("Default")
    ?? throw new InvalidOperationException("Connection string 'Default' not found.");

builder.Services.AddDbContext<AppDbContext>(options =>
    options.UseMySql(connectionString, ServerVersion.AutoDetect(connectionString)));

// Auth
builder.Services.AddScoped<ITokenService, TokenService>();
builder.Services.AddScoped<IValidator<RegisterRequest>, RegisterRequestValidator>();
builder.Services.AddScoped<IValidator<LoginRequest>, LoginRequestValidator>();
builder.Services.AddScoped<IValidator<ForgotPasswordRequest>, ForgotPasswordRequestValidator>();
builder.Services.AddScoped<IValidator<ResetPasswordRequest>, ResetPasswordRequestValidator>();
// Password-reset email delivery. Email:Provider picks the sender (smtp = real mail via MailKit, see
// SmtpEmailSender for the Smtp:Username/Password secrets; console = log the link, dev only). With
// RabbitMq:Host set the email is queued and a background worker sends, retries and dead-letters it;
// without it, it is sent inline as before. See Messaging/MessagingExtensions.cs.
builder.Services.AddEmailDelivery(builder.Configuration);
// Async AI replies (Chat:AsyncReplies) and direct-chat event fan-out through the outbox (Phases 18 and 19). Both need RabbitMQ.
builder.Services.AddBrokerFeatures(builder.Configuration);

// Chat
builder.Services.AddScoped<IValidator<CreateSessionRequest>, CreateSessionRequestValidator>();
builder.Services.AddScoped<IValidator<SendMessageRequest>, SendMessageRequestValidator>();
builder.Services.AddScoped<IValidator<StartConversationRequest>, StartConversationRequestValidator>();
builder.Services.AddScoped<IValidator<SendDirectMessageRequest>, SendDirectMessageRequestValidator>();

// Realtime direct chat (Phase 11): SignalR + presence. With Redis:ConnectionString set (Phase 16) both are
// shared across API replicas through Redis; without it they stay in this process (one replica only).
builder.Services.AddRealtime(builder.Configuration);
// Rate limiting, login lockout and trusted-proxy handling (Phase 21). After AddRealtime: it shares Redis when that is configured.
builder.Services.AddSecurityHardening(builder.Configuration);
builder.Services.AddSingleton<IDirectNotifier, SignalRDirectNotifier>();
builder.Services.AddScoped<PresenceAnnouncer>();
builder.Services.AddScoped<IDirectDeliveryService, DirectDeliveryService>();

// Python AI service client. InternalApiKey is the shared secret from CLAUDE.md's X-Internal-Key
// contract - it must match ai-service-python's INTERNAL_API_KEY exactly.
var pythonBaseUrl = builder.Configuration["PythonService:BaseUrl"] ?? "http://localhost:8000";
var pythonInternalApiKey = builder.Configuration["PythonService:InternalApiKey"]
    ?? throw new InvalidOperationException(
        "PythonService:InternalApiKey is not configured. Set it via "
        + "'dotnet user-secrets set \"PythonService:InternalApiKey\" \"<value>\"' in Development "
        + "(must match ai-service-python's INTERNAL_API_KEY), or the PythonService__InternalApiKey "
        + "environment variable elsewhere.");

builder.Services.AddHttpClient<IPythonAiClient, PythonAiClient>(client =>
{
    client.BaseAddress = new Uri(pythonBaseUrl);
    client.DefaultRequestHeaders.Add("X-Internal-Key", pythonInternalApiKey);
    // The LLM call itself can legitimately take a while; longer than ASP.NET Core's own
    // request timeout would matter, but this is well short of forever.
    client.Timeout = TimeSpan.FromSeconds(60);
});

var jwtKey = builder.Configuration["Jwt:Key"]
    ?? throw new InvalidOperationException(
        "Jwt:Key is not configured. Set it via 'dotnet user-secrets set \"Jwt:Key\" \"<value>\"' in Development, or the Jwt__Key environment variable elsewhere.");

builder.Services.AddAuthentication(options =>
    {
        options.DefaultAuthenticateScheme = JwtBearerDefaults.AuthenticationScheme;
        options.DefaultChallengeScheme = JwtBearerDefaults.AuthenticationScheme;
    })
    .AddJwtBearer(options =>
    {
        options.TokenValidationParameters = new TokenValidationParameters
        {
            ValidateIssuer = true,
            ValidIssuer = builder.Configuration["Jwt:Issuer"],
            ValidateAudience = true,
            ValidAudience = builder.Configuration["Jwt:Audience"],
            ValidateIssuerSigningKey = true,
            IssuerSigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(jwtKey)),
            ValidateLifetime = true,
            ClockSkew = TimeSpan.FromSeconds(30),
        };
        // Surface *why* a token was rejected in the logs - a bare 401 with no detail
        // is otherwise the only signal, which makes malformed/expired/mis-signed
        // tokens indistinguishable from each other while debugging.
        options.Events = new JwtBearerEvents
        {
            // Browsers cannot set an Authorization header on a WebSocket, so the SignalR client sends the
            // JWT as ?access_token=... . Accept it from the query string for the hub path only.
            OnMessageReceived = context =>
            {
                var accessToken = context.Request.Query["access_token"];
                if (!string.IsNullOrEmpty(accessToken) && context.HttpContext.Request.Path.StartsWithSegments("/hubs"))
                {
                    context.Token = accessToken;
                }
                return Task.CompletedTask;
            },
            OnAuthenticationFailed = context =>
            {
                context.HttpContext.RequestServices
                    .GetRequiredService<ILoggerFactory>()
                    .CreateLogger("JwtBearer")
                    .LogWarning(context.Exception, "JWT authentication failed");
                return Task.CompletedTask;
            },
            // Without these, a missing/invalid token or a wrong role short-circuits the pipeline
            // before any controller runs, so ASP.NET Core's own default kicks in: a bare 401/403
            // with an empty body - breaking the "every .NET error response uses
            // { error: { code, message } }" rule from CLAUDE.md for exactly the two error cases
            // every protected endpoint can hit on every request.
            OnChallenge = async context =>
            {
                context.HandleResponse(); // suppress the default empty-body 401
                await new ErrorResponse("unauthorized", "Authentication is required.")
                    .WriteAsync(context.HttpContext, StatusCodes.Status401Unauthorized);
            },
            OnForbidden = async context =>
            {
                await new ErrorResponse("forbidden", "You do not have permission to perform this action.")
                    .WriteAsync(context.HttpContext, StatusCodes.Status403Forbidden);
            },
        };
    });

builder.Services.AddAuthorization();

// Angular (a different origin in dev: localhost:4200 vs this API's 5291) needs an explicit
// CORS policy to call this API from the browser. Origins are configurable, not hardcoded,
// since the deployed frontend's origin will differ from the dev one.
const string FrontendCorsPolicy = "Frontend";
var allowedOrigins = builder.Configuration.GetSection("Cors:AllowedOrigins").Get<string[]>()
    ?? new[] { "http://localhost:4200" };

builder.Services.AddCors(options =>
{
    options.AddPolicy(FrontendCorsPolicy, policy => policy
        .WithOrigins(allowedOrigins)
        .AllowAnyHeader()
        .AllowAnyMethod()
        // SignalR's negotiate request is sent with credentials; with explicit origins this is allowed.
        .AllowCredentials());
});

builder.Services.AddControllers();

// Liveness only (is the process serving requests) - deliberately no DB check, so a MySQL blip
// doesn't make Kubernetes restart a healthy API. Used by container/k8s probes.
builder.Services.AddHealthChecks();

// Make model-binding failures (e.g. malformed JSON) use the same { error: { code, message } }
// shape as everything else, instead of the framework's default ProblemDetails.
builder.Services.Configure<ApiBehaviorOptions>(options =>
{
    options.InvalidModelStateResponseFactory = context =>
    {
        var message = string.Join(" ", context.ModelState.Values
            .SelectMany(v => v.Errors)
            .Select(e => e.ErrorMessage));
        return new BadRequestObjectResult(new ErrorResponse("validation_error", message));
    };
});

// Learn more about configuring Swagger/OpenAPI at https://aka.ms/aspnetcore/swashbuckle
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen(options =>
{
    options.AddSecurityDefinition("Bearer", new OpenApiSecurityScheme
    {
        // Type=Http + Scheme="bearer" makes Swagger UI prepend "Bearer " itself, so the box
        // must hold ONLY the raw token, e.g. "eyJhbGciOi..." - never "Bearer eyJhbGciOi...".
        Description = "Paste the raw JWT only - do NOT include the word \"Bearer\". Swagger UI adds that prefix for you.",
        Name = "Authorization",
        In = ParameterLocation.Header,
        Type = SecuritySchemeType.Http,
        Scheme = "bearer",
        BearerFormat = "JWT",
    });
    options.AddSecurityRequirement(new OpenApiSecurityRequirement
    {
        {
            new OpenApiSecurityScheme { Reference = new OpenApiReference { Type = ReferenceType.SecurityScheme, Id = "Bearer" } },
            Array.Empty<string>()
        }
    });
});

builder.WebHost.ConfigureKestrel(options => options.AddServerHeader = false);

var app = builder.Build();

// First: the real client address (from the proxy's X-Forwarded-For, trusted only from the configured networks) must be in place before
// anything logs or counts it. Skipped when ForwardedHeaders:Enabled is false.
if (builder.Configuration.GetValue("ForwardedHeaders:Enabled", true))
{
    app.UseForwardedHeaders();
}
app.UseMiddleware<SecurityHeadersMiddleware>();
app.UseExceptionHandling();

// Configure the HTTP request pipeline.
if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI();
}

// Behind a TLS-terminating proxy (the containers) there is no HTTPS port here to redirect to, and enabling this only logs
// "Failed to determine the https port for redirect" on every request. On by default for local development only.
if (builder.Configuration.GetValue("Https:RedirectEnabled", app.Environment.IsDevelopment()))
{
    app.UseHttpsRedirection();
}

app.UseCors(FrontendCorsPolicy);

app.UseAuthentication();
app.UseMiddleware<RateLimitMiddleware>();
app.UseAuthorization();

app.MapControllers();
app.MapHub<DirectHub>("/hubs/direct");
app.MapHealthChecks("/health");

// A route matching nothing above (typo'd path, wrong method, ...) would otherwise fall through
// to ASP.NET Core's bare, empty-body 404 - same consistent-error-shape reasoning as the
// OnChallenge/OnForbidden handlers above.
app.MapFallback(context =>
    new ErrorResponse("not_found", "The requested resource was not found.")
        .WriteAsync(context, StatusCodes.Status404NotFound));

app.Run();

// Lets the pipeline tests (WebApplicationFactory<Program>) see the entry point of this top-level-statements file.
public partial class Program;
