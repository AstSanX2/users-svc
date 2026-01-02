using Amazon.SimpleSystemsManagement;
using Amazon.SimpleSystemsManagement.Model;
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
using System.Net;
using System.Text;

var builder = WebApplication.CreateBuilder(args);

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

// ------------------------------------------------------
// Feature flag para uso de SSM (em k8s você pode desligar
// e usar apenas env vars / secrets).
// ------------------------------------------------------
bool useSsm = !env.IsDevelopment() ||
              string.Equals(Environment.GetEnvironmentVariable("USE_SSM"), "true", StringComparison.OrdinalIgnoreCase);

// SSM é lazy: só cria client se realmente for usar.
IAmazonSimpleSystemsManagement? ssm = null;

string? TryGetSsm(string name, bool decrypt = true)
{
    if (!useSsm) return null;

    ssm ??= new AmazonSimpleSystemsManagementClient();

    try
    {
        var resp = ssm.GetParameterAsync(new GetParameterRequest
        {
            Name = name,
            WithDecryption = decrypt
        }).GetAwaiter().GetResult();

        return resp?.Parameter?.Value;
    }
    catch (ParameterNotFoundException)
    {
        return null;
    }
    catch (AmazonSimpleSystemsManagementException ex) when (
        string.Equals(ex.ErrorCode, "UnrecognizedClientException", StringComparison.OrdinalIgnoreCase) ||
        ex.StatusCode == HttpStatusCode.Forbidden ||
        ex.StatusCode == HttpStatusCode.Unauthorized)
    {
        // Sem permissão / credencial → ignora e segue para outras fontes
        return null;
    }
    catch
    {
        // Qualquer outro erro de SSM não deve impedir o serviço de subir
        return null;
    }
}

static string FirstNonEmpty(params string?[] vals) =>
    vals.FirstOrDefault(v => !string.IsNullOrWhiteSpace(v)) ?? "";

// ------------------------------------------------------
// MongoDB (Atlas)
// Em k8s: preferencialmente via env (ConfigMap/Secret)
// ------------------------------------------------------
var mongoConnectionString = FirstNonEmpty(
    useSsm ? TryGetSsm("/fcg/MONGODB_URI") : null,
    configuration["MongoDB:ConnectionString"],
    Environment.GetEnvironmentVariable("MONGODB_URI"),
    TryGetSsm("/fcg/MONGODB_URI")
);

if (string.IsNullOrWhiteSpace(mongoConnectionString))
    throw new InvalidOperationException("MongoDB connection string not found (SSM /fcg/MONGODB_URI, env MONGODB_URI ou MongoDB:ConnectionString).");

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
// Em k8s: usar Secret para a key.
// ------------------------------------------------------
var jwtSecret = FirstNonEmpty(
    useSsm ? TryGetSsm("/fcg/JWT_SECRET") : null,
    configuration["JwtOptions:Key"],
    Environment.GetEnvironmentVariable("JWT_SECRET"),
    TryGetSsm("/fcg/JWT_SECRET")
);

if (string.IsNullOrWhiteSpace(jwtSecret))
    throw new InvalidOperationException("JWT secret not found (/fcg/JWT_SECRET, env JWT_SECRET ou JwtOptions:Key).");

var jwtIssuer = FirstNonEmpty(
    useSsm ? TryGetSsm("/fcg/JWT_ISS", decrypt: false) : null,
    configuration["JwtOptions:Issuer"],
    Environment.GetEnvironmentVariable("JWT_ISS"),
    TryGetSsm("/fcg/JWT_ISS", decrypt: false)
);

if (string.IsNullOrWhiteSpace(jwtIssuer))
    throw new InvalidOperationException("JWT issuer not found (/fcg/JWT_ISS, env JWT_ISS ou JwtOptions:Issuer).");

var jwtAudience = FirstNonEmpty(
    useSsm ? TryGetSsm("/fcg/JWT_AUD", decrypt: false) : null,
    configuration["JwtOptions:Audience"],
    Environment.GetEnvironmentVariable("JWT_AUD"),
    TryGetSsm("/fcg/JWT_AUD", decrypt: false)
);

if (string.IsNullOrWhiteSpace(jwtAudience))
    throw new InvalidOperationException("JWT audience not found (/fcg/JWT_AUD, env JWT_AUD ou JwtOptions:Audience).");

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
builder.Services.AddScoped<IUserRepository, UserRepository>();
builder.Services.AddScoped<IUserService, UserService>();
builder.Services.AddScoped<IAuthenticationService, AuthenticationService>();

// Seeding/Migrations
builder.Services.AddHostedService<MongoSeeder>();

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
    useSsm,
    jwt = new { issuer = jwtIssuer, audience = jwtAudience }
}));

app.MapGet("/ready", () => Results.Ok(new
{
    ready = true,
    svc = "users"
}));

app.MapGet("/", () => "UsersSvc up & running (container mode)");

app.Run();
