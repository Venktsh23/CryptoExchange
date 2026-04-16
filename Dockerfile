# Stage 1 — Build
# Uses the full SDK image to compile the app
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

# Copy solution and project files first
# Docker caches this layer — only rebuilds if .csproj files change
# This means dependencies are only restored when they actually change
COPY CryptoExchange.slnx .
COPY Exchange.API/Exchange.API.csproj           Exchange.API/
COPY Exchange.Core/Exchange.Core.csproj         Exchange.Core/
COPY Exchange.TestRunner/Exchange.TestRunner.csproj Exchange.TestRunner/

# Restore dependencies
RUN dotnet restore

# Copy all source code
COPY Client/ Exchange.API/wwwroot/
COPY Exchange.API/     Exchange.API/
COPY Exchange.Core/    Exchange.Core/
COPY Exchange.TestRunner/ Exchange.TestRunner/

# Build and publish in Release mode
RUN dotnet publish Exchange.API/Exchange.API.csproj \
    -c Release \
    -o /app/publish \
    --no-restore

# Stage 2 — Runtime
# Uses only the runtime image — much smaller than SDK
# SDK image: ~800MB, Runtime image: ~200MB
FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime
WORKDIR /app

# Create non-root user for security
# Running as root inside containers is a security risk
RUN useradd -m appuser

# Copy only the published output from build stage
COPY --from=build /app/publish .

# Create logs directory with correct permissions
RUN mkdir -p /app/logs && chown -R appuser:appuser /app

# Switch to non-root user
USER appuser

# Expose the port
EXPOSE 5000

# Set environment to Production
ENV ASPNETCORE_ENVIRONMENT=Production
ENV ASPNETCORE_URLS=http://+:5000

ENTRYPOINT ["dotnet", "Exchange.API.dll"]