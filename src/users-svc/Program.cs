using Amazon.SimpleSystemsManagement;
using Amazon.SimpleSystemsManagement.Model;
using Application.Services;
using Domain.Interfaces.Repositories;
using Domain.Interfaces.Services;
using Infraestructure.Migration;
using Infraestructure.Repositories;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.IdentityModel.Tokens;
using Microsoft.OpenApi.Models;
using MongoDB.Driver;
using System.Net;
using System.Text;

var builder = WebApplication.CreateBuilder(args);


builder.Services.AddAWSLambdaHosting(LambdaEventSource.RestApi);

var configuration = builder.Configuration;
var env = builder.Environment;

bool useSsm = !env.IsDevelopment() || string.Equals(Environment.GetEnvironmentVariable("USE_SSM"), "true", StringComparison.OrdinalIgnoreCase);

var ssm = new AmazonSimpleSystemsManagementClient();

string? TryGetSsm(string name, bool decrypt = true)
{
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
        ex.StatusCode == HttpStatusCode.Forbidden || ex.StatusCode == HttpStatusCode.Unauthorized)
    {
        return null;
    }
    catch
    {
        return null;
    }
}

static string FirstNonEmpty(params string?[] vals) => vals.FirstOrDefault(v => !string.IsNullOrWhiteSpace(v)) ?? "";

// -------- MongoDB (Atlas) --------
// Ordem de resolução:
// 1) SSM (se habilitado)  2) appsettings (MongoDB:ConnectionString)  3) SSM (fallback final)
var mongoConnectionString = FirstNonEmpty(
    useSsm ? TryGetSsm("/fcg/MONGODB_URI") : null,
    configuration["MongoDB:ConnectionString"],
    TryGetSsm("/fcg/MONGODB_URI")
);

if (string.IsNullOrWhiteSpace(mongoConnectionString))
    throw new InvalidOperationException("MongoDB connection string not found (SSM /fcg/MONGODB_URI or MongoDB:ConnectionString).");

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
    var client = sp.GetRequiredService<IMongoClient>();
    return client.GetDatabase(dbName);
});

// -------- JWT --------
var jwtSecret = FirstNonEmpty(
    useSsm ? TryGetSsm("/fcg/JWT_SECRET") : null,
    configuration["JwtOptions:Key"],
    TryGetSsm("/fcg/JWT_SECRET")
);

if (string.IsNullOrWhiteSpace(jwtSecret))
    throw new InvalidOperationException("JWT secret not found (SSM /fcg/JWT_SECRET or Jwt:Key).");

var jwtIssuer = FirstNonEmpty(
    useSsm ? TryGetSsm("/fcg/JWT_ISS", decrypt: false) : null,
    configuration["JwtOptions:Issuer"],
    TryGetSsm("/fcg/JWT_ISS", decrypt: false)
);

var jwtAudience = FirstNonEmpty(
    useSsm ? TryGetSsm("/fcg/JWT_AUD", decrypt: false) : null,
    configuration["JwtOptions:Audience"],
    TryGetSsm("/fcg/JWT_AUD", decrypt: false)
);

var signingKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(jwtSecret));

builder.Services
    .AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
    .AddJwtBearer(options =>
    {
        options.TokenValidationParameters = new TokenValidationParameters
        {
            ValidateIssuerSigningKey = true,
            IssuerSigningKey = signingKey,

            ValidateIssuer = !string.IsNullOrWhiteSpace(jwtIssuer),
            ValidIssuer = string.IsNullOrWhiteSpace(jwtIssuer) ? null : jwtIssuer,

            ValidateAudience = !string.IsNullOrWhiteSpace(jwtAudience),
            ValidAudience = string.IsNullOrWhiteSpace(jwtAudience) ? null : jwtAudience,

            ValidateLifetime = true,
            ClockSkew = TimeSpan.FromSeconds(30)
        };
    });

builder.Services.AddAuthorization();

// -------- MVC + Swagger --------
builder.Services.AddControllers();
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
            new OpenApiSecurityScheme {
                Reference = new OpenApiReference { Type = ReferenceType.SecurityScheme, Id = "Bearer" }
            },
            Array.Empty<string>()
        }
    });
});

// -------- DI --------
builder.Services.AddScoped<IUserRepository, UserRepository>();
builder.Services.AddScoped<IUserService, UserService>();
builder.Services.AddScoped<IAuthenticationService, AuthenticationService>();

builder.Services.AddHostedService<MongoSeeder>();

var app = builder.Build();

if (app.Environment.IsDevelopment() || true)
{
    app.UseSwagger();
    app.UseSwaggerUI();
}

app.UseAuthentication();
app.UseAuthorization();

app.MapControllers();

app.MapGet("/health", () => Results.Ok(new { ok = true, svc = "users", env = env.EnvironmentName, useSsm }));
app.MapGet("/", () => "UsersSvc up & running");

app.Run();
