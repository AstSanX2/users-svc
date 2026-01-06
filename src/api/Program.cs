using Application.Services;
using Domain.Interfaces.Repositories;
using Domain.Interfaces.Services;
using Helpers;
using Infraestructure.Migration;
using Infraestructure.Repositories;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.IdentityModel.Tokens;
using Microsoft.OpenApi.Models;
using MongoDB.Driver;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;
using System.Diagnostics;
using System.Text;

var builder = WebApplication.CreateBuilder(args);

Activity.DefaultIdFormat = ActivityIdFormat.W3C;
Activity.ForceDefaultIdFormat = true;

// ------------------------------------------------------
// Kestrel otimizado para rodar em container/Kubernetes
// ------------------------------------------------------
builder.WebHost.ConfigureKestrel(options =>
{
    // Remove o header "Server" (hardening básico)
    options.AddServerHeader = false;

    // Timeouts mais razoáveis para evitar conexões zumbis
    options.Limits.KeepAliveTimeout = TimeSpan.FromMinutes(2);
    options.Limits.RequestHeadersTimeout = TimeSpan.FromSeconds(30);
});

// Para funcionar bem atrás de ingress/nginx/ALB
builder.Services.Configure<ForwardedHeadersOptions>(options =>
{
    options.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto;
});

var configuration = builder.Configuration;
var env = builder.Environment;

static string FirstNonEmpty(params string?[] vals) =>
    vals.FirstOrDefault(v => !string.IsNullOrWhiteSpace(v)) ?? "";

// ------------------------------------------------------
// MongoDB (Atlas)
// Local: appsettings no repo
// Prod (K8s): appsettings montado no container (ConfigMap/Secret)
// ------------------------------------------------------
var mongoConnectionString = FirstNonEmpty(
    configuration["MongoDB:ConnectionString"],
    Environment.GetEnvironmentVariable("MONGODB_URI") // compatibilidade (pode remover depois)
);

if (string.IsNullOrWhiteSpace(mongoConnectionString))
    throw new InvalidOperationException("MongoDB connection string not found (MongoDB:ConnectionString no appsettings ou env MONGODB_URI).");

builder.Services.AddSingleton<IMongoClient>(_ =>
{
    var settings = MongoClientSettings.FromConnectionString(mongoConnectionString);
    settings.ServerApi = new ServerApi(ServerApiVersion.V1);
    return new MongoClient(settings);
});

builder.Services.AddSingleton(sp =>
{
    var url = new MongoUrl(mongoConnectionString);
    var dbName = url.DatabaseName;
    if (string.IsNullOrWhiteSpace(dbName))
        throw new InvalidOperationException("Database name must be specified in the MongoDB connection string.");
    return sp.GetRequiredService<IMongoClient>().GetDatabase(dbName);
});

// ------------------------------------------------------
// JWT (fonte única + espelho nas seções JwtOptions)
// Local: appsettings no repo
// Prod (K8s): appsettings montado no container (ConfigMap/Secret)
// ------------------------------------------------------
var jwtSecret = FirstNonEmpty(
    configuration["JwtOptions:Key"],
    Environment.GetEnvironmentVariable("JWT_SECRET") // compatibilidade (pode remover depois)
);

if (string.IsNullOrWhiteSpace(jwtSecret))
    throw new InvalidOperationException("JWT secret not found (JwtOptions:Key no appsettings ou env JWT_SECRET).");

var jwtIssuer = FirstNonEmpty(
    configuration["JwtOptions:Issuer"],
    Environment.GetEnvironmentVariable("JWT_ISS") // compatibilidade (pode remover depois)
);

if (string.IsNullOrWhiteSpace(jwtIssuer))
    throw new InvalidOperationException("JWT issuer not found (JwtOptions:Issuer no appsettings ou env JWT_ISS).");

var jwtAudience = FirstNonEmpty(
    configuration["JwtOptions:Audience"],
    Environment.GetEnvironmentVariable("JWT_AUD") // compatibilidade (pode remover depois)
);

if (string.IsNullOrWhiteSpace(jwtAudience))
    throw new InvalidOperationException("JWT audience not found (JwtOptions:Audience no appsettings ou env JWT_AUD).");

// espelho em configuration pra quem injeta IOptions<JwtOptions>
var jwtMirror = new Dictionary<string, string?>
{
    ["JwtOptions:Key"] = jwtSecret,
    ["JwtOptions:Issuer"] = jwtIssuer,
    ["JwtOptions:Audience"] = jwtAudience
};
builder.Configuration.AddInMemoryCollection(jwtMirror);

var signingKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(jwtSecret));

builder.Services
    .AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
    .AddJwtBearer(options =>
    {
        options.TokenValidationParameters = new TokenValidationParameters
        {
            ValidateIssuerSigningKey = true,
            IssuerSigningKey = signingKey,

            ValidateIssuer = true,
            ValidIssuer = jwtIssuer,

            ValidateAudience = true,
            ValidAudience = jwtAudience,

            ValidateLifetime = true,
            ClockSkew = TimeSpan.FromSeconds(30)
        };
    });

builder.Services.AddAuthorization();

// ------------------------------------------------------
// MVC + Swagger
// ------------------------------------------------------
builder.Services.AddControllers()
    .AddJsonOptions(x =>
    {
        x.JsonSerializerOptions.Converters.Add(new ObjectIdJsonConverter());
    });

builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen(c =>
{
    c.SwaggerDoc("v1", new OpenApiInfo { Title = "UsersSvc", Version = "v1" });
    c.AddSecurityDefinition("Bearer", new OpenApiSecurityScheme
    {
        In = ParameterLocation.Header,
        Description = "JWT no header. Ex: Bearer {token}",
        Name = "Authorization",
        Type = SecuritySchemeType.Http,
        BearerFormat = "JWT",
        Scheme = "Bearer"
    });
    c.AddSecurityRequirement(new OpenApiSecurityRequirement
    {
        {
            new OpenApiSecurityScheme
            {
                Reference = new OpenApiReference
                {
                    Type = ReferenceType.SecurityScheme,
                    Id = "Bearer"
                }
            },
            Array.Empty<string>()
        }
    });
});

// ------------------------------------------------------
// DI
// ------------------------------------------------------
builder.Services.AddScoped<IEventRepository, EventRepository>();
builder.Services.AddScoped<IOutboxRepository, OutboxRepository>();
builder.Services.AddScoped<IUserRepository, UserRepository>();
builder.Services.AddScoped<IUserService, UserService>();
builder.Services.AddScoped<IAuthenticationService, AuthenticationService>();

// ------------------------------------------------------
// Observabilidade (OpenTelemetry -> OTLP). Se OTLP endpoint não estiver setado,
// mantém o comportamento atual (sem export) e evita ruído no dev.
// ------------------------------------------------------
var otlpEndpoint = Environment.GetEnvironmentVariable("OTEL_EXPORTER_OTLP_ENDPOINT");
if (!string.IsNullOrWhiteSpace(otlpEndpoint))
{
    builder.Services.AddOpenTelemetry()
        .ConfigureResource(r => r.AddService(serviceName: "users-api"))
        .WithTracing(t =>
        {
            t.AddAspNetCoreInstrumentation();
            t.AddHttpClientInstrumentation();
            t.AddOtlpExporter(o => o.Endpoint = new Uri(otlpEndpoint));
        });
}

// Seeding/Migrations
builder.Services.AddHostedService<MongoSeeder>();
// Outbox publisher (SQS integration events)
builder.Services.AddHostedService<Application.Services.OutboxPublisherHostedService>();

var app = builder.Build();

// Para funcionar bem atrás de proxy reverso / ingress
app.UseForwardedHeaders();

if (env.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI();
}
else
{
    app.UseSwagger();
    app.UseSwaggerUI();
}

app.UseAuthentication();
app.UseAuthorization();

app.MapControllers();

// ------------------------------------------------------
// Endpoints para probes do Kubernetes
// ------------------------------------------------------
app.MapGet("/health", () => Results.Ok(new
{
    ok = true,
    svc = "users",
    env = env.EnvironmentName,
    jwt = new { issuer = jwtIssuer, audience = jwtAudience }
}));

app.MapGet("/ready", () => Results.Ok(new
{
    ready = true,
    svc = "users"
}));

app.MapGet("/", () => "UsersSvc up & running (container mode)");

app.Run();
