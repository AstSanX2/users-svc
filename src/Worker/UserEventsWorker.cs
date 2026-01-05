using Amazon;
using Amazon.Runtime;
using Amazon.SQS;
using Amazon.SQS.Model;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using MongoDB.Bson;
using MongoDB.Driver;
using System.Text.Json;

namespace UsersWorker;

public record IntegrationEventEnvelope(
    Guid EventId,
    string Type,
    DateTime OccurredAt,
    string Source,
    string AggregateId,
    string? CorrelationId,
    string? CausationId,
    int Version,
    JsonElement Data
);

public class UserEventsWorker : BackgroundService
{
    private readonly IMongoDatabase _db;
    private readonly IAmazonSQS _sqs;
    private readonly string _queueUrl;
    private readonly int _pollIntervalMs;
    private readonly int _maxMessages;

    public UserEventsWorker(IMongoDatabase db, IConfiguration configuration)
    {
        _db = db;
        _sqs = CreateSqsClient(configuration);
        _queueUrl = configuration["Sqs:UsersEventsQueueUrl"]
            ?? configuration["USERS_EVENTS_QUEUE_URL"]
            ?? Environment.GetEnvironmentVariable("USERS_EVENTS_QUEUE_URL")
            ?? throw new InvalidOperationException("Users queue URL not found (Sqs:UsersEventsQueueUrl no appsettings ou env USERS_EVENTS_QUEUE_URL).");

        _pollIntervalMs = int.TryParse(configuration["Worker:PollIntervalMs"] ?? configuration["POLL_INTERVAL_MS"], out var interval)
            ? interval : 5000;
        _maxMessages = int.TryParse(configuration["Worker:MaxMessages"] ?? configuration["MAX_MESSAGES"], out var max)
            ? max : 10;

        EnsureIdempotencyIndex();
    }

    private void EnsureIdempotencyIndex()
    {
        var events = _db.GetCollection<BsonDocument>("Events");
        var indexKeys = Builders<BsonDocument>.IndexKeys.Ascending("SqsMessageId");
        var index = new CreateIndexModel<BsonDocument>(indexKeys, new CreateIndexOptions { Unique = true, Name = "ux_sqsMessageId" });
        try
        {
            events.Indexes.CreateOne(index);
        }
        catch
        {
            // best-effort
        }
    }

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

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        Console.WriteLine($"[UsersWorker] Escutando fila: {_queueUrl}");

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var response = await _sqs.ReceiveMessageAsync(new ReceiveMessageRequest
                {
                    QueueUrl = _queueUrl,
                    MaxNumberOfMessages = _maxMessages,
                    WaitTimeSeconds = 20, // Long polling
                    VisibilityTimeout = 60
                }, stoppingToken);

                if (response.Messages.Count == 0)
                {
                    await Task.Delay(_pollIntervalMs, stoppingToken);
                    continue;
                }

                foreach (var message in response.Messages)
                {
                    try
                    {
                        await ProcessMessageAsync(message, stoppingToken);
                        await _sqs.DeleteMessageAsync(_queueUrl, message.ReceiptHandle, stoppingToken);
                        Console.WriteLine($"[UsersWorker] Mensagem processada: {message.MessageId}");
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine($"[UsersWorker] Erro ao processar mensagem {message.MessageId}: {ex.Message}");
                        // Mensagem volta para a fila após visibility timeout
                    }
                }
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[UsersWorker] Erro no loop: {ex.Message}");
                await Task.Delay(5000, stoppingToken);
            }
        }

        Console.WriteLine("[UsersWorker] Worker encerrado");
    }

    private async Task ProcessMessageAsync(Message message, CancellationToken ct)
    {
        var env = JsonSerializer.Deserialize<IntegrationEventEnvelope>(message.Body, new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true
        });

        if (env is null)
            throw new InvalidOperationException("Envelope inválido (null).");

        if (!env.Data.TryGetProperty("UserId", out var userIdEl) || userIdEl.ValueKind != JsonValueKind.String)
            throw new InvalidOperationException("Envelope sem Data.UserId.");

        if (!env.Data.TryGetProperty("Email", out var emailEl) || emailEl.ValueKind != JsonValueKind.String)
            throw new InvalidOperationException("Envelope sem Data.Email.");

        var userId = userIdEl.GetString() ?? "";
        var email = emailEl.GetString() ?? "";

        Console.WriteLine($"[UsersWorker] Processando evento {env.Type} para usuário {userId}");

        var events = _db.GetCollection<BsonDocument>("Events");

        var doc = new BsonDocument
        {
            { "SqsMessageId", message.MessageId },
            { "AggregateId", userId },
            { "Type", $"{env.Type}Processed" },
            { "Timestamp", DateTime.UtcNow },
            { "Data", new BsonDocument
                {
                    { "EventId", env.EventId.ToString() },
                    { "OriginalEventType", env.Type },
                    { "UserId", userId },
                    { "Email", email },
                    { "OriginalTimestamp", env.OccurredAt },
                    { "Source", env.Source },
                    { "CorrelationId", env.CorrelationId is null ? BsonNull.Value : env.CorrelationId },
                    { "ProcessedAt", DateTime.UtcNow }
                }
            }
        };

        try
        {
            await events.InsertOneAsync(doc, cancellationToken: ct);
        }
        catch (MongoWriteException mw) when (mw.WriteError?.Category == ServerErrorCategory.DuplicateKey)
        {
            Console.WriteLine($"[UsersWorker] Mensagem {message.MessageId} já processada (duplicate key), ignorando");
            return;
        }

        // Lógica adicional baseada no tipo de evento
        switch (env.Type)
        {
            case "UserLoggedIn":
                await HandleUserLoggedInAsync(userId, ct);
                break;
            case "UserRegistered":
                await HandleUserRegisteredAsync(userId, email, ct);
                break;
            default:
                Console.WriteLine($"[UsersWorker] Tipo de evento desconhecido: {env.Type}");
                break;
        }
    }

    private async Task HandleUserLoggedInAsync(string userId, CancellationToken ct)
    {
        // Atualiza last login do usuário
        // Collection name deve ser "User" para coincidir com o repositório (nameof(User))
        var users = _db.GetCollection<BsonDocument>("User");
        if (ObjectId.TryParse(userId, out var oid))
        {
            var filter = Builders<BsonDocument>.Filter.Eq("_id", oid);
            var update = Builders<BsonDocument>.Update.Set("LastLoginAt", DateTime.UtcNow);
            await users.UpdateOneAsync(filter, update, cancellationToken: ct);
        }
        Console.WriteLine($"[UsersWorker] UserLoggedIn processado: {userId}");
    }

    private async Task HandleUserRegisteredAsync(string userId, string email, CancellationToken ct)
    {
        // Lógica de boas-vindas ou notificação pode ser adicionada aqui
        Console.WriteLine($"[UsersWorker] UserRegistered processado: {userId} ({email})");
        await Task.CompletedTask;
    }
}

