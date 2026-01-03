# Users Service (users-svc)

Serviço **Users** do FCG — cadastro/login/gestão de perfis, emissão de **JWT**, e persistência em **MongoDB Atlas**. Feito para rodar **serverless** em **AWS Lambda** atrás do **API Gateway** com **observabilidade via X-Ray**.  
Este README é um guia completo: **como rodar local**, **como configurar segredos**, **como fazer deploy**, **como testar**, **como observar**, e **como depurar**.

---

## Sumário

- [Arquitetura (visão rápida)](#arquitetura-visão-rápida)
- [Stack / Tecnologias](#stack--tecnologias)
- [Rotas Principais](#rotas-principais)
- [Pré-requisitos](#pré-requisitos)
- [Configuração (appsettings)](#configuração-appsettings)
- [Configuração Local (Dev)](#configuração-local-dev)
- [Execução Local](#execução-local)
- [Deploy na AWS (Serverless)](#deploy-na-aws-serverless)
- [Observabilidade (X-Ray + Logs)](#observabilidade-x-ray--logs)
- [Testes Rápidos (cURL)](#testes-rápidos-curl)
- [Estrutura do Projeto](#estrutura-do-projeto)
- [Políticas/IAM mínimas esperadas](#políticasiam-mínimas-esperadas)
- [Dicas e Troubleshooting](#dicas-e-troubleshooting)
- [Limpeza de Infra](#limpeza-de-infra)
---

## Arquitetura (visão rápida)

```
Client → API Gateway (REST proxy)
                ↓
            AWS Lambda (users-svc, .NET 8)
                ↓
           MongoDB Atlas (Users)
```

- **JWT** emitido pelo Users e validado pelos demais serviços (Games/Payments).  
- **Configuração** via `appsettings` (local) e `appsettings.Production.json` montado no Kubernetes.  
- **Tracing** habilitado (**X-Ray**) e **logs** via CloudWatch.

---

## Stack / Tecnologias

- **.NET 8** (ASP.NET Core Minimal API + Controllers).
- **AWS Lambda** + **API Gateway (REST)**.
- **MongoDB Atlas** (driver oficial `MongoDB.Driver`).
- **JWT** (`Microsoft.AspNetCore.Authentication.JwtBearer`).
- Configuração por `appsettings`.
- **AWS X-Ray** (traces) + **CloudWatch Logs**.
- Ferramentas:
  - `AWS CLI`, `Amazon.Lambda.Tools` (`dotnet lambda`).
  - Postman/Insomnia (opcional) para testes.

---

## Rotas Principais

> Os caminhos abaixo assumem o **Invoke URL** do API Gateway como `API_U`.

| Método | Rota                                  | Auth              | Descrição |
|-------:|---------------------------------------|-------------------|-----------|
| GET    | `/`                                   | público           | Health check simples. |
| POST   | `/api/Authentication/login`           | público           | Autentica usuário e retorna **JWT**. |
| POST   | `/api/Authentication/register`        | público           | Registra usuário (retorna `userId`). |
| GET    | `/api/Users`                          | **Bearer (Admin)**| Lista usuários. |
| GET    | `/api/Users/{id}`                     | **Bearer**        | Busca usuário por id. |
| PUT    | `/api/Users/{id}`                     | **Bearer**        | Atualiza dados do usuário (o próprio ou admin). |
| DELETE | `/api/Users/{id}`                     | **Bearer (Admin)**| Remove usuário. |

> **Observação**: os nomes exatos dos DTOs e constraints (ex.: campos obrigatórios) seguem o código; este README foca o **uso**.  
> O token contém *claims* padrão: `sub` (userId), `email`, `name`, `role`.

---

## Pré-requisitos

- **Windows, Linux ou macOS** com **.NET 8 SDK** instalado.
- **AWS CLI** configurado:
  ```bash
  aws configure
  # informe AWS Access Key, Secret Access Key, region (us-east-1) e output (json)
  ```
- **Ferramentas do Lambda para .NET**:
  ```bash
  dotnet tool install -g Amazon.Lambda.Tools
  dotnet tool update -g Amazon.Lambda.Tools
  ```
- **Conta MongoDB Atlas** com **cluster** criado (copie sua **Connection String**).

---

## Configuração (appsettings)

- **Local**: `appsettings.Development.json` no repositório.
- **Prod (Kubernetes)**: `appsettings.Production.json` montado no pod via `k8s/secrets.yaml` (chave `appsettings.Production.json`).

---

## Configuração Local (Dev)

Em **dev**, você pode usar `appsettings.Development.json` como fallback (útil quando estiver sem AWS creds).  
Formato esperado (se usar arquivo local):

```json
{
  "MongoDB": {
    "ConnectionString": "mongodb+srv://<user>:<pass>@<cluster>.mongodb.net/<db>?retryWrites=true&w=majority&appName=<app>"
  },
  "JwtOptions": {
    "Key": "<uma-chave-bem-aleatória-32+ chars>",
    "Issuer": "fcg-auth",
    "Audience": "fcg-clients"
  }
}
```

> Em produção (Kubernetes), os valores vêm do `appsettings.Production.json` montado no pod.

---

## Execução Local

```bash
# na pasta do projeto (onde está o .csproj)
dotnet restore
dotnet build
dotnet run
# a API sobe em http://localhost:5000 (ou conforme launchSettings.json)
```

- **Health**: `GET http://localhost:5000/health`
- **Swagger** (se ativo): `http://localhost:5000/swagger`

---

## Deploy na AWS (Serverless)

> O projeto já vem com o template serverless (SAM) gerado pelo `dotnet lambda`.

1. **(Uma vez)** crie um bucket para artifacts (se ainda não existir):
   ```bash
   aws s3 mb s3://lambda-artifacts-users-fcg-us-east-1 --region us-east-1
   ```

2. **Deploy**:
   ```bash
   # a partir da pasta do projeto (src/users-svc)
   dotnet lambda deploy-serverless
   # Responda:
   # - CloudFormation Stack Name: users-svc
   # - S3 Bucket: lambda-artifacts-users-fcg-us-east-1
   # Aguarde CREATE_COMPLETE
   ```

3. **Obter o Invoke URL**:
   ```bash
   aws cloudformation describe-stacks \
     --stack-name users-svc \
     --query "Stacks[0].Outputs[?OutputKey=='ApiURL'].OutputValue" \
     --output text
   # Ex.: https://abc123.execute-api.us-east-1.amazonaws.com/Prod/
   ```

> O template publica uma **Lambda** com **API Gateway (REST)** em modo “proxy”, e já pode habilitar **Tracing: Active** (X-Ray).  
> Garanta que a **Role** da Lambda possui as policies **AWSLambdaBasicExecutionRole** e **AWSXRayDaemonWriteAccess**.

---

## Observabilidade (X-Ray + Logs)

### Habilitar X-Ray
- Lambda (users-svc) → **Configuration → Monitoring and operations tools → AWS X-Ray → Active tracing**.
- API Gateway → sua API → **Stages (Prod) → Logs/Tracing → X-Ray Tracing = Enabled**.
- (Opcional) definir env var `AWS_XRAY_TRACING_NAME=users-svc` para nome amigável no mapa.

### Ver o Service Map
- Gere tráfego (login, register, GET /api/Users).
- AWS Console → **X-Ray → Service map** → ver **API Gateway → users-svc**.

### Logs
- CloudWatch Logs:
  ```bash
  aws logs describe-log-groups --log-group-name-prefix /aws/lambda/
  aws logs tail /aws/lambda/<NOME-REAL-DA-FUNÇÃO> --follow
  ```

Para descobrir o **nome real** da função:
```bash
aws cloudformation describe-stack-resources \
  --stack-name users-svc \
  --query "StackResources[?ResourceType=='AWS::Lambda::Function'].PhysicalResourceId" \
  --output text
```

---

## Testes Rápidos (cURL)

> **Windows PowerShell**: use `curl.exe` (não o alias).

### 1) Registrar usuário
```bash
curl -X POST "$API_U/api/Users/register" \
  -H "Content-Type: application/json" \
  -d '{"name":"Buyer 1","email":"buyer+1@fcg.com","password":"Test@123"}'
```

### 2) Login (pegar token)
```bash
TOKEN=$(curl -s -X POST "$API_U/api/Authentication/login" \
  -H "Content-Type: application/json" \
  -d '{"email":"buyer+1@fcg.com","password":"Test@123"}' | jq -r '.token')
echo $TOKEN
```

### 3) Listar (precisa ser Admin)
```bash
curl -H "Authorization: Bearer $TOKEN" "$API_U/api/Users"
```

### 4) Health
```bash
curl "$API_U/health"
```

> Você pode importar uma **collection Postman** própria com essas chamadas (veja o repo raiz do projeto para a versão completa).

---

## Estrutura do Projeto

```
src/users-svc/
  Application/
    DTO/ ...                 # contratos de entrada/saída (Register, Login, etc.)
    Services/AuthenticationService.cs
  Domain/
    Entities/User.cs
    Interfaces/Repositories/IUserRepository.cs
    Interfaces/Services/IAuthenticationService.cs
    Models/Response/ResponseModel.cs
  Infraestructure/
    Repositories/UserRepository.cs
    Migration/MongoSeeder.cs  # seed opcional ao subir
  Controllers/
    AuthenticationController.cs
    UsersController.cs
  Helpers/
    ObjectIdJsonConverter.cs  # conversor JSON p/ ObjectId nos Controllers
  Program.cs
  aws-lambda-tools-defaults.json
  serverless.template (ou equivalente gerado)
  appsettings*.json
  users-svc.csproj
```

---

## Políticas/IAM mínimas esperadas

- **Role da Lambda (users-svc)**:
  - `AWSLambdaBasicExecutionRole` (logs)
  - `AWSXRayDaemonWriteAccess` (traces)
  - (Kubernetes) use Secrets/ConfigMaps para configurações e segredos.

> **Princípio do menor privilégio**: evite policies amplas (ex.: `*FullAccess`).  
> O serviço **Users** usa apenas configuração via `appsettings`.

---

## Dicas e Troubleshooting

**1) `The security token included in the request is invalid`**  
- AWS CLI/profile não configurado no ambiente local.  
- Em Lambda, a **role** não tem permissão `ssm:GetParameter`.  
- Parâmetros criados em **outra região**.

**2) `Database name must be specified in the connection string`**  
- A URI do Atlas não tem o nome do DB. Use:  
  `...mongodb.net/<db>?retryWrites=true...`

**3) `ParameterNotFound`**  
- Garanta que as configurações (Mongo/JWT) foram definidas via `appsettings`.

**4) `401 Unauthorized` nos outros serviços (Games/Payments)**  
- **Mismatch** de JWT (Issuer/Audience/Secret) — padronize **exatamente os mesmos valores** nos três serviços.

**5) Swagger abre mas “Authorize” falha**  
- Confirme no `Program.cs` que o **JwtBearer** está configurado e que o **token** foi emitido pelo **Users** com o mesmo Issuer/Audience/Secret.

**6) X-Ray não mostra nada**  
- Habilite **Active tracing** na Lambda **e** no Stage do API Gateway.  
- Verifique a role com `AWSXRayDaemonWriteAccess`.  
- Gere tráfego e aumente o range para **15 min** no X-Ray.

---

## Limpeza de Infra

> Execute **após** gravar o vídeo de entrega para evitar custos.

```bash
aws cloudformation delete-stack --stack-name users-svc
# (repita para games-svc e payments-svc nos outros repositórios)
```

Se criou bucket de artifacts dedicado:
```bash
aws s3 rb s3://lambda-artifacts-users-fcg-us-east-1 --force
```
