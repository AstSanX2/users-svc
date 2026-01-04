using Amazon;
using Amazon.Runtime;
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

    public class AuthenticationService(
        IUserRepository userRepository, 
        IEventRepository eventRepository,
        IConfiguration configuration, 
        IHostEnvironment env) : IAuthenticationService
    {
        private readonly IConfiguration _configuration = configuration;
        private readonly IHostEnvironment _env = env;
        private readonly IAmazonSQS _sqs = CreateSqsClient(configuration);

        private static IAmazonSQS CreateSqsClient(IConfiguration configuration)
        {
            var serviceUrl = configuration["Sqs:ServiceUrl"] ?? Environment.GetEnvironmentVariable("SQS_SERVICE_URL");
            if (!string.IsNullOrEmpty(serviceUrl))
            {
                // LocalStack ou outro emulador
                var config = new AmazonSQSConfig { ServiceURL = serviceUrl };
                var accessKey = configuration["AWS:AccessKey"];
                var secretKey = configuration["AWS:SecretKey"];
                if (!string.IsNullOrWhiteSpace(accessKey) && !string.IsNullOrWhiteSpace(secretKey))
                    return new AmazonSQSClient(new BasicAWSCredentials(accessKey, secretKey), config);

                return new AmazonSQSClient(new BasicAWSCredentials("test", "test"), config);
            }
            // AWS real (credenciais via appsettings ou cadeia default)
            var region = configuration["AWS:Region"] ?? Environment.GetEnvironmentVariable("AWS_REGION");
            var sqsConfig = new AmazonSQSConfig();
            if (!string.IsNullOrWhiteSpace(region))
                sqsConfig.RegionEndpoint = RegionEndpoint.GetBySystemName(region);

            var ak = configuration["AWS:AccessKey"];
            var sk = configuration["AWS:SecretKey"];
            if (!string.IsNullOrWhiteSpace(ak) && !string.IsNullOrWhiteSpace(sk))
                return new AmazonSQSClient(new BasicAWSCredentials(ak, sk), sqsConfig);

            return new AmazonSQSClient(sqsConfig);
        }

        public async Task<ResponseModel<ObjectId>> Register(RegisterUserDTO registerUserRequest)
        {
            var validationResult = registerUserRequest.Validate();
            if (validationResult.HasError)
            {
                // Registra evento de validação falhada
                var evFail = DomainEvent.Create(
                    aggregateId: ObjectId.Empty,
                    type: "UserRegistrationValidationFailed",
                    data: new Dictionary<string, object?>
                    {
                        ["Errors"] = validationResult.ToString(),
                        ["Email"] = registerUserRequest.Email // Não incluir dados sensíveis como senha
                    }
                );
                await eventRepository.AppendEventAsync(evFail, CancellationToken.None);

                return ResponseModel<ObjectId>.BadRequest(validationResult.ToString());
            }

            var user = await userRepository.FindOneAsync(u => u.Email == registerUserRequest.Email);
            if (user is not null)
            {
                // Registra evento de email duplicado
                var evDup = DomainEvent.Create(
                    aggregateId: ObjectId.Empty,
                    type: "UserRegistrationDuplicateEmail",
                    data: new Dictionary<string, object?>
                    {
                        ["Email"] = registerUserRequest.Email
                    }
                );
                await eventRepository.AppendEventAsync(evDup, CancellationToken.None);

                return ResponseModel<ObjectId>.BadRequest("Email de Usuário já registrado");
            }

            var result = await userRepository.CreateAsync(registerUserRequest);

            // Registra evento de usuário registrado
            var ev = DomainEvent.Create(
                aggregateId: result._id,
                type: "UserRegistered",
                data: new Dictionary<string, object?>
                {
                    ["UserId"] = result._id.ToString(),
                    ["Email"] = registerUserRequest.Email,
                    ["Name"] = registerUserRequest.Name
                }
            );
            await eventRepository.AppendEventAsync(ev, CancellationToken.None);

            // Publicar evento UserRegistered na SQS (fire-and-forget, não bloqueia resposta)
            _ = PublishUserEventAsync("UserRegistered", result._id.ToString(), registerUserRequest.Email);

            return ResponseModel<ObjectId>.Ok(result._id);
        }

        public async Task<ResponseModel<AuthenticationTokenDTO>> Login(LoginUserDTO loginUserRequest)
        {
            var validationResult = loginUserRequest.Validate();
            if (validationResult.HasError)
            {
                // Registra evento de validação falhada
                var evFail = DomainEvent.Create(
                    aggregateId: ObjectId.Empty,
                    type: "UserLoginValidationFailed",
                    data: new Dictionary<string, object?>
                    {
                        ["Errors"] = validationResult.ToString(),
                        ["Email"] = loginUserRequest.Email
                    }
                );
                await eventRepository.AppendEventAsync(evFail, CancellationToken.None);

                return ResponseModel<AuthenticationTokenDTO>.BadRequest(validationResult.ToString());
            }

            var user = await userRepository.FindOneAsync(u => u.Email == loginUserRequest.Email);
            if (user is null)
            {
                // Registra evento de usuário não encontrado
                var evNotFound = DomainEvent.Create(
                    aggregateId: ObjectId.Empty,
                    type: "UserLoginUserNotFound",
                    data: new Dictionary<string, object?>
                    {
                        ["Email"] = loginUserRequest.Email
                    }
                );
                await eventRepository.AppendEventAsync(evNotFound, CancellationToken.None);

                return ResponseModel<AuthenticationTokenDTO>.BadRequest("Login Inválido");
            }

            if (user.Password.Equals(loginUserRequest.Password.ToHash()))
            {
                var token = GenerateToken(user);

                // Registra evento de login bem-sucedido
                var ev = DomainEvent.Create(
                    aggregateId: user._id,
                    type: "UserLoggedIn",
                    data: new Dictionary<string, object?>
                    {
                        ["UserId"] = user._id.ToString(),
                        ["Email"] = user.Email
                    }
                );
                await eventRepository.AppendEventAsync(ev, CancellationToken.None);

                // Publicar evento UserLoggedIn na SQS (fire-and-forget, não bloqueia resposta)
                _ = PublishUserEventAsync("UserLoggedIn", user._id.ToString(), user.Email);

                return ResponseModel<AuthenticationTokenDTO>.Ok(token);
            }

            // Registra evento de senha inválida
            var evInvalid = DomainEvent.Create(
                aggregateId: user._id,
                type: "UserLoginFailed",
                data: new Dictionary<string, object?>
                {
                    ["UserId"] = user._id.ToString(),
                    ["Email"] = user.Email,
                    ["Reason"] = "InvalidPassword"
                }
            );
            await eventRepository.AppendEventAsync(evInvalid, CancellationToken.None);

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
            // Primeiro tenta env var (K8s ConfigMap/Secret)
            var queueUrl = Environment.GetEnvironmentVariable("USERS_EVENTS_QUEUE_URL");
            if (!string.IsNullOrEmpty(queueUrl)) return queueUrl;

            // Depois tenta appsettings (K8s: arquivo montado; Local: arquivo do repo)
            return _configuration["Sqs:UsersEventsQueueUrl"]
                ?? _configuration["USERS_EVENTS_QUEUE_URL"];
        }

        private AuthenticationTokenDTO GenerateToken(User user)
        {
            // Resolve JwtOptions via appsettings (Local: arquivo no repo; Prod/K8s: arquivo montado no pod)
            var jwt = ResolveJwtOptions();

            // Claims
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
            var key = _configuration["JwtOptions:Key"];
            var iss = _configuration["JwtOptions:Issuer"];
            var aud = _configuration["JwtOptions:Audience"];

            if (string.IsNullOrWhiteSpace(key) || string.IsNullOrWhiteSpace(iss) || string.IsNullOrWhiteSpace(aud))
                throw new InvalidOperationException("JwtOptions incompleto no appsettings (Key/Issuer/Audience).");

            return (key, iss, aud);
        }
    }
}
