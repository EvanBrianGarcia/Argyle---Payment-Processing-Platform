# syntax=docker/dockerfile:1.7

FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

COPY src/PaymentPlatform.Worker/PaymentPlatform.Worker.csproj src/PaymentPlatform.Worker/
COPY src/PaymentPlatform.Application/PaymentPlatform.Application.csproj src/PaymentPlatform.Application/
COPY src/PaymentPlatform.Contracts/PaymentPlatform.Contracts.csproj src/PaymentPlatform.Contracts/
COPY src/PaymentPlatform.Domain/PaymentPlatform.Domain.csproj src/PaymentPlatform.Domain/
COPY src/PaymentPlatform.Infrastructure/PaymentPlatform.Infrastructure.csproj src/PaymentPlatform.Infrastructure/
COPY src/PaymentPlatform.Messaging/PaymentPlatform.Messaging.csproj src/PaymentPlatform.Messaging/

RUN dotnet restore src/PaymentPlatform.Worker/PaymentPlatform.Worker.csproj

COPY src/ src/

RUN dotnet publish src/PaymentPlatform.Worker/PaymentPlatform.Worker.csproj \
    -c Release \
    -o /app/publish \
    --no-restore \
    /p:UseAppHost=false

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime
WORKDIR /app

ENV DOTNET_RUNNING_IN_CONTAINER=true \
    DOTNET_USE_POLLING_FILE_WATCHER=false

# Worker only listens on its metrics port; nothing else binds Kestrel.
EXPOSE 9090

COPY --from=build /app/publish .

ENTRYPOINT ["dotnet", "PaymentPlatform.Worker.dll"]
