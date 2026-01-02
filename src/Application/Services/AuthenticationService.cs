using Amazon;
using Amazon.Runtime;
using Amazon.SimpleSystemsManagement;
using Amazon.SimpleSystemsManagement.Model;
using Amazon.SQS;
using Amazon.SQS.Model;
using Application.DTO.AuthenticationDTO;
using Application.DTO.UsersDTO;
using Domain.Entities;
using Domain.Interfaces.Repositories;
using Domain.Interfaces.Services;
using Domain.Models.Response;
using Helpers.Extensions;
using Microsoft.IdentityModel.Tokens;
using MongoDB.Bson;
using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text;
using System.Text.Json;

namespace Application.Services
{
    // Mensagens de eventos para SQS
    public record UserEventMessage(string EventType, string UserId, string Email, DateTime Timestamp);

    public class AuthenticationService(IUserRepository userRepository, IConfiguration configuration, IHostEnvironment env) : IAuthenticationService
    {
        private readonly IConfiguration _configuration = configuration;
        private readonly IHostEnvironment _env = env;
        private readonly IAmazonSQS _sqs = CreateSqsClient();

        private static IAmazonSQS CreateSqsClient()
        {
            var serviceUrl = Environment.GetEnvironmentVariable("SQS_SERVICE_URL");
            if (!string.IsNullOrEmpty(serviceUrl))
            {
                // LocalStack ou outro emulador
                var config = new AmazonSQSConfig { ServiceURL = serviceUrl };
                return new AmazonSQSClient(new BasicAWSCredentials("test", "test"), config);
            }
            // AWS real
            return new AmazonSQSClient();
        }

        public async Task<ResponseModel<ObjectId>> Register(RegisterUserDTO registerUserRequest)
        {
            var validationResult = registerUserRequest.Validate();
            if (validationResult.HasError)
                return ResponseModel<ObjectId>.BadRequest(validationResult.ToString());

            var user = await userRepository.FindOneAsync(u => u.Email == registerUserRequest.Email);
            if (user is not null)
                return ResponseModel<ObjectId>.BadRequest("Email de Usuário já registrado");

            var result = await userRepository.CreateAsync(registerUserRequest);

            // Publicar evento UserRegistered na SQS (fire-and-forget, não bloqueia resposta)
            _ = PublishUserEventAsync("UserRegistered", result._id.ToString(), registerUserRequest.Email);

            return ResponseModel<ObjectId>.Ok(result._id);
        }

        public async Task<ResponseModel<AuthenticationTokenDTO>> Login(LoginUserDTO loginUserRequest)
        {
            var validationResult = loginUserRequest.Validate();
            if (validationResult.HasError)
                return ResponseModel<AuthenticationTokenDTO>.BadRequest(validationResult.ToString());

            var user = await userRepository.FindOneAsync(u => u.Email == loginUserRequest.Email);
            if (user is null)
                return ResponseModel<AuthenticationTokenDTO>.BadRequest("Login Inválido");

            if (user.Password.Equals(loginUserRequest.Password.ToHash()))
            {
                var token = GenerateToken(user);

                // Publicar evento UserLoggedIn na SQS (fire-and-forget, não bloqueia resposta)
                _ = PublishUserEventAsync("UserLoggedIn", user._id.ToString(), user.Email);

                return ResponseModel<AuthenticationTokenDTO>.Ok(token);
            }

            return ResponseModel<AuthenticationTokenDTO>.Unauthorized("O usuário não pode ser autenticado, verifique suas informações.");
        }

        private async Task PublishUserEventAsync(string eventType, string userId, string email)
        {
            try
            {
                var queueUrl = GetQueueUrl();
                if (string.IsNullOrEmpty(queueUrl))
                {
                    // Em desenvolvimento sem SQS configurado, apenas loga
                    Console.WriteLine($"[SQS] Evento {eventType} para usuário {userId} (SQS não configurado)");
                    return;
                }

                var message = new UserEventMessage(eventType, userId, email, DateTime.UtcNow);
                var body = JsonSerializer.Serialize(message);

                await _sqs.SendMessageAsync(new SendMessageRequest
                {
                    QueueUrl = queueUrl,
                    MessageBody = body
                });

                Console.WriteLine($"[SQS] Evento {eventType} publicado para usuário {userId}");
            }
            catch (Exception ex)
            {
                // Não falha a operação principal se a publicação SQS falhar
                Console.WriteLine($"[SQS] Erro ao publicar evento {eventType}: {ex.Message}");
            }
        }

        private string? GetQueueUrl()
        {
            // Primeiro tenta variável de ambiente (K8s ConfigMap/Secret)
            var queueUrl = Environment.GetEnvironmentVariable("USERS_EVENTS_QUEUE_URL");
            if (!string.IsNullOrEmpty(queueUrl))
                return queueUrl;

            // Se não estiver em desenvolvimento, tenta SSM
            if (!_env.IsDevelopment())
            {
                try
                {
                    using var ssm = new AmazonSimpleSystemsManagementClient();
                    var resp = ssm.GetParameterAsync(new GetParameterRequest
                    {
                        Name = "/fcg/USERS_EVENTS_QUEUE_URL"
                    }).GetAwaiter().GetResult();
                    return resp.Parameter?.Value;
                }
                catch
                {
                    return null;
                }
            }

            return null;
        }

        private AuthenticationTokenDTO GenerateToken(User user)
        {
            // 1) Resolve JwtOptions de acordo com o ambiente:
            //    - Development/Debug: appsettings (JwtOptions)
            //    - Ambientes não-Dev: SSM Parameter Store (/fcg/JWT_SECRET, /fcg/JWT_ISS, /fcg/JWT_AUD)
            var jwt = ResolveJwtOptions();

            // 2) Claims
            var claims = new List<Claim>
            {
                new("UserId", user._id.ToString()),
                new("userId", user._id.ToString()),
                new(JwtRegisteredClaimNames.Sub, user._id.ToString()),
                new(JwtRegisteredClaimNames.Name, user.Name ?? string.Empty),
                new(JwtRegisteredClaimNames.Email, user.Email ?? string.Empty),
                new(ClaimTypes.Role, user.Role.ToString()),
                new("role", user.Role.ToString())          
            };

            var key = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(jwt.Key!));
            var creds = new SigningCredentials(key, SecurityAlgorithms.HmacSha256);

            var tokenDescriptor = new SecurityTokenDescriptor
            {
                Subject = new ClaimsIdentity(claims),
                Expires = DateTime.UtcNow.AddHours(8),
                Issuer = jwt.Issuer,
                Audience = jwt.Audience,
                SigningCredentials = creds
            };

            var handler = new JwtSecurityTokenHandler();
            var token = handler.WriteToken(handler.CreateToken(tokenDescriptor));

            return new AuthenticationTokenDTO
            {
                Token = token,
                ExpiresOn = tokenDescriptor.Expires,
                UserInfo = new ProjectUserDTO(user)
            };
        }

        private (string Key, string Issuer, string Audience) ResolveJwtOptions()
        {
            if (_env.IsDevelopment())
            {
                var key = _configuration["JwtOptions:Key"];
                var iss = _configuration["JwtOptions:Issuer"];
                var aud = _configuration["JwtOptions:Audience"];

                if (string.IsNullOrWhiteSpace(key) || string.IsNullOrWhiteSpace(iss) || string.IsNullOrWhiteSpace(aud))
                    throw new InvalidOperationException("JwtOptions incompleto no appsettings (Key/Issuer/Audience).");

                return (key, iss, aud);
            }

            var ssm = new AmazonSimpleSystemsManagementClient();
            string Get(string name, bool decrypt = true)
            {
                var resp = ssm.GetParameterAsync(new GetParameterRequest { Name = name, WithDecryption = decrypt })
                              .GetAwaiter().GetResult();
                return resp.Parameter?.Value ?? throw new InvalidOperationException($"Parâmetro SSM ausente: {name}");
            }

            var keySsm = Get("/fcg/JWT_SECRET", decrypt: true);
            var issSsm = Get("/fcg/JWT_ISS", decrypt: false);
            var audSsm = Get("/fcg/JWT_AUD", decrypt: false);

            if (string.IsNullOrWhiteSpace(keySsm) || string.IsNullOrWhiteSpace(issSsm) || string.IsNullOrWhiteSpace(audSsm))
                throw new InvalidOperationException("Parâmetros JWT do SSM inválidos (JWT_SECRET/JWT_ISS/JWT_AUD).");

            return (keySsm, issSsm, audSsm);
        }
    }
}
