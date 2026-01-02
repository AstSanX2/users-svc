# ===== STAGE 1: build/publish =====
FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /src

# Argumento pra escolher Debug/Release
ARG BUILD_CONFIGURATION=Release

COPY users-svc.csproj ./
RUN dotnet restore

COPY . .
RUN dotnet publish -c $BUILD_CONFIGURATION -o /app /p:UseAppHost=false

# ===== STAGE 2: runtime =====
FROM mcr.microsoft.com/dotnet/aspnet:8.0 AS final
WORKDIR /app

COPY --from=build /app .

# Args para tunar comportamento em runtime
ARG ASPNETCORE_ENVIRONMENT=Production
ARG ENABLE_DIAGNOSTICS=false

ENV ASPNETCORE_URLS=http://+:8080
ENV ASPNETCORE_ENVIRONMENT=${ASPNETCORE_ENVIRONMENT}

# Somente em prod eu desabilito diagnostico,
# mas aqui deixo parametrizado:
ENV DOTNET_EnableDiagnostics=${ENABLE_DIAGNOSTICS}
ENV DOTNET_TieredPGO=1
ENV DOTNET_TC_QuickJitForLoops=1

EXPOSE 8080

ENTRYPOINT ["dotnet", "UsersSvc.dll"]
