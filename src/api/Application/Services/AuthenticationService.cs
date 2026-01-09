using Application.DTO.AuthenticationDTO;
using Application.DTO.UsersDTO;
using Domain.Entities;
using Domain.Events;
using Domain.Interfaces.Repositories;
using Domain.Interfaces.Services;
using Domain.Models.Response;
using Helpers.Extensions;
using Microsoft.IdentityModel.Tokens;
using MongoDB.Bson;
using System.Diagnostics;
using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text;

namespace Application.Services
{
    public class AuthenticationService(
        IUserRepository userRepository, 
        IEventRepository eventRepository,
        IOutboxRepository outboxRepository,
        IConfiguration configuration, 
        IHostEnvironment env) : IAuthenticationService
    {
        private readonly IConfiguration _configuration = configuration;
        private readonly IHostEnvironment _env = env;
        private const string SourceName = "users-svc";

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

            // Publica evento de integração via Outbox (resiliente).
            _ = EnqueueIntegrationEventAsync(
                eventType: "UserRegistered",
                aggregateId: result._id.ToString(),
                data: new Dictionary<string, object?>
                {
                    ["UserId"] = result._id.ToString(),
                    ["Email"] = registerUserRequest.Email
                });

            // Notificação (assíncrona) com mínimo impacto: enfileira um evento que o UsersWorker consome.
            _ = EnqueueIntegrationEventAsync(
                eventType: "NotificationRequested",
                aggregateId: result._id.ToString(),
                data: new Dictionary<string, object?>
                {
                    ["UserId"] = result._id.ToString(),
                    ["Email"] = registerUserRequest.Email,
                    ["Template"] = "Welcome"
                });

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

                // Publica evento de integração via Outbox (resiliente).
                _ = EnqueueIntegrationEventAsync(
                    eventType: "UserLoggedIn",
                    aggregateId: user._id.ToString(),
                    data: new Dictionary<string, object?>
                    {
                        ["UserId"] = user._id.ToString(),
                        ["Email"] = user.Email
                    });

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

        private async Task EnqueueIntegrationEventAsync(string eventType, string aggregateId, Dictionary<string, object?> data)
        {
            try
            {
                var queueUrl = _configuration["Sqs:UsersEventsQueueUrl"] ?? _configuration["USERS_EVENTS_QUEUE_URL"];
                if (string.IsNullOrWhiteSpace(queueUrl))
                    return;

                // Preferimos W3C traceparent para permitir encadear API -> Outbox -> Worker.
                var correlationId = Activity.Current?.Id;
                var env = IntegrationEventEnvelope.Create(
                    type: eventType,
                    source: SourceName,
                    aggregateId: aggregateId,
                    data: data,
                    correlationId: correlationId
                );

                var body = System.Text.Json.JsonSerializer.Serialize(env);
                var outbox = new OutboxMessage
                {
                    EventId = env.EventId,
                    EventType = env.Type,
                    Source = env.Source,
                    AggregateId = env.AggregateId,
                    CorrelationId = env.CorrelationId,
                    CausationId = env.CausationId,
                    Version = env.Version,
                    Destination = queueUrl,
                    Body = body
                };

                await outboxRepository.EnqueueAsync(outbox, CancellationToken.None);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[Outbox] Erro ao enfileirar evento {eventType}: {ex.Message}");
            }
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
