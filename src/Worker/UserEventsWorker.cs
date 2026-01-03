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

public record UserEventMessage(string EventType, string UserId, string Email, DateTime Timestamp);

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
        var evt = JsonSerializer.Deserialize<UserEventMessage>(message.Body);
        if (evt is null)
        {
            Console.WriteLine($"[UsersWorker] Mensagem inválida: {message.Body}");
            return;
        }

        Console.WriteLine($"[UsersWorker] Processando evento {evt.EventType} para usuário {evt.UserId}");

        var events = _db.GetCollection<BsonDocument>("Events");

        // Idempotência: verifica se já processou este MessageId
        var existingEvent = await events.Find(
            Builders<BsonDocument>.Filter.Eq("SqsMessageId", message.MessageId)
        ).FirstOrDefaultAsync(ct);

        if (existingEvent != null)
        {
            Console.WriteLine($"[UsersWorker] Mensagem {message.MessageId} já processada, ignorando");
            return;
        }

        // Grava evento processado no MongoDB com MessageId para idempotência
        var doc = new BsonDocument
        {
            { "SqsMessageId", message.MessageId },
            { "AggregateId", evt.UserId },
            { "Type", $"{evt.EventType}Processed" },
            { "Timestamp", DateTime.UtcNow },
            { "Data", new BsonDocument
                {
                    { "OriginalEventType", evt.EventType },
                    { "UserId", evt.UserId },
                    { "Email", evt.Email },
                    { "OriginalTimestamp", evt.Timestamp },
                    { "ProcessedAt", DateTime.UtcNow }
                }
            }
        };

        await events.InsertOneAsync(doc, cancellationToken: ct);

        // Lógica adicional baseada no tipo de evento
        switch (evt.EventType)
        {
            case "UserLoggedIn":
                await HandleUserLoggedInAsync(evt, ct);
                break;
            case "UserRegistered":
                await HandleUserRegisteredAsync(evt, ct);
                break;
            default:
                Console.WriteLine($"[UsersWorker] Tipo de evento desconhecido: {evt.EventType}");
                break;
        }
    }

    private async Task HandleUserLoggedInAsync(UserEventMessage evt, CancellationToken ct)
    {
        // Atualiza last login do usuário
        var users = _db.GetCollection<BsonDocument>("Users");
        if (ObjectId.TryParse(evt.UserId, out var userId))
        {
            var filter = Builders<BsonDocument>.Filter.Eq("_id", userId);
            var update = Builders<BsonDocument>.Update.Set("LastLoginAt", DateTime.UtcNow);
            await users.UpdateOneAsync(filter, update, cancellationToken: ct);
        }
        Console.WriteLine($"[UsersWorker] UserLoggedIn processado: {evt.UserId}");
    }

    private async Task HandleUserRegisteredAsync(UserEventMessage evt, CancellationToken ct)
    {
        // Lógica de boas-vindas ou notificação pode ser adicionada aqui
        Console.WriteLine($"[UsersWorker] UserRegistered processado: {evt.UserId} ({evt.Email})");
        await Task.CompletedTask;
    }
}

