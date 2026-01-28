# Stage 1: Base runtime image
FROM mcr.microsoft.com/dotnet/runtime:10.0 AS base
WORKDIR /app

# Install kerberos dependencies for GSSAPI support
RUN apt-get update && apt-get install -y --no-install-recommends \
    libgssapi-krb5-2 \
    && rm -rf /var/lib/apt/lists/*

# Stage 2: Build stage - restore dependencies and compile
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
ARG BUILD_CONFIGURATION=Release
WORKDIR /src

# Copy project file first for layer caching on dependency restore
COPY ["EmailService/EmailService.csproj", "EmailService/"]
RUN dotnet restore "EmailService/EmailService.csproj"

# Copy remaining source and build
COPY . .
WORKDIR "/src/EmailService"
RUN dotnet build "./EmailService.csproj" -c $BUILD_CONFIGURATION -o /app/build

# Stage 3: Publish stage - create optimized release artifacts
FROM build AS publish
ARG BUILD_CONFIGURATION=Release
RUN dotnet publish "./EmailService.csproj" -c $BUILD_CONFIGURATION -o /app/publish /p:UseAppHost=false

# Stage 4: Final runtime image
FROM base AS final
WORKDIR /app

# Copy published artifacts from publish stage
COPY --from=publish /app/publish .

# Create non-root user for security
RUN groupadd -r appuser && useradd -r -g appuser appuser \
    && chown -R appuser:appuser /app
USER appuser

ENTRYPOINT ["dotnet", "EmailService.dll"]
