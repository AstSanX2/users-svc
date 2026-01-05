# Users Service (`users-svc`)

Serviço responsável por **autenticação/JWT** e **gestão de usuários**.

Este projeto roda como **API containerizada** no **Kubernetes/EKS**, persiste em **MongoDB**, e publica eventos assíncronos via **Outbox → SQS**.

## Arquitetura (visão rápida)

```
Client → API Gateway HTTP API (/users/*)
              ↓
         users-api (EKS)
              ↓
           MongoDB

users-api → Outbox (MongoDB) → OutboxPublisher → SQS users-events-queue → users-worker
```

## Stack
- .NET 8 / ASP.NET Core
- MongoDB (driver oficial)
- SQS (publicação via Outbox)
- Kubernetes: liveness/readiness (`/health`, `/ready`)

## Endpoints
- `GET /health`
- `GET /ready`
- `POST /api/Authentication/register`
- `POST /api/Authentication/login`
- `GET/PUT/DELETE /api/Users/*` (dependendo de role/claims)

## Eventos (SQS)
- **Queue**: `users-events-queue` (+ DLQ `users-events-dlq`)
- **Envelope padrão**: `Domain.Events.IntegrationEventEnvelope`
- Eventos publicados (via Outbox):
  - `UserRegistered`
  - `UserLoggedIn`

## Configuração
- Local (dev): `src/appsettings.Development.json`
- Produção (K8s): arquivo montado em `/app/appsettings.Production.json` via Secret `users-appsettings`
  - Passo-a-passo: `fcg-domain/k8s/SECRETS.md`

## Testes

```bash
dotnet test test/users-svc.Tests.csproj -c Release
```

## Docker
- `src/Dockerfile` (API)
- `src/Worker/Dockerfile` (worker)
- Imagens rodam como **non-root** (`USER app`) e sem `HEALTHCHECK` (probes são do k8s)

## Kubernetes
Manifests no repo `fcg-domain`:
- `fcg-domain/k8s/users/*`

