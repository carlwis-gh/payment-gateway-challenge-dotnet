# Payment Gateway API (DESIGN D17). Build from the repository root:
#   docker build -t payment-gateway .
# or run it with the bank simulator:
#   docker compose up --build

# Build stage: the SDK image restores and publishes the API only (tests are not shipped).
FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /src

# Restore on its own layer so a code change does not re-download packages.
COPY src/PaymentGateway.Api/PaymentGateway.Api.csproj src/PaymentGateway.Api/
RUN dotnet restore src/PaymentGateway.Api/PaymentGateway.Api.csproj

COPY src/ src/
RUN dotnet publish src/PaymentGateway.Api/PaymentGateway.Api.csproj \
    --configuration Release --no-restore --output /app

# Runtime stage: the smaller ASP.NET image, running as the image's non-root "app" user.
FROM mcr.microsoft.com/dotnet/aspnet:8.0 AS runtime
WORKDIR /app
COPY --from=build /app .

# Plain HTTP inside the container; TLS is terminated in front of it (Assumption 1).
ENV ASPNETCORE_HTTP_PORTS=8080
EXPOSE 8080
USER app

ENTRYPOINT ["dotnet", "PaymentGateway.Api.dll"]
