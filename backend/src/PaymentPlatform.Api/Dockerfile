# syntax=docker/dockerfile:1.7

FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

# Copy csproj files first so `dotnet restore` can cache its layer.
COPY src/PaymentPlatform.Api/PaymentPlatform.Api.csproj src/PaymentPlatform.Api/
COPY src/PaymentPlatform.Application/PaymentPlatform.Application.csproj src/PaymentPlatform.Application/
COPY src/PaymentPlatform.Contracts/PaymentPlatform.Contracts.csproj src/PaymentPlatform.Contracts/
COPY src/PaymentPlatform.Domain/PaymentPlatform.Domain.csproj src/PaymentPlatform.Domain/
COPY src/PaymentPlatform.Infrastructure/PaymentPlatform.Infrastructure.csproj src/PaymentPlatform.Infrastructure/

RUN dotnet restore src/PaymentPlatform.Api/PaymentPlatform.Api.csproj

COPY src/ src/

RUN dotnet publish src/PaymentPlatform.Api/PaymentPlatform.Api.csproj \
    -c Release \
    -o /app/publish \
    --no-restore \
    /p:UseAppHost=false

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime
WORKDIR /app

ENV ASPNETCORE_URLS=http://+:8080 \
    DOTNET_RUNNING_IN_CONTAINER=true \
    DOTNET_USE_POLLING_FILE_WATCHER=false

COPY --from=build /app/publish .

EXPOSE 8080

ENTRYPOINT ["dotnet", "PaymentPlatform.Api.dll"]
