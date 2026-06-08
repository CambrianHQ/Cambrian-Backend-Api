FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /src

# Copy only the project files needed for restore (tests excluded via .dockerignore)
COPY src/Cambrian.Domain/Cambrian.Domain.csproj src/Cambrian.Domain/
COPY src/Cambrian.Application/Cambrian.Application.csproj src/Cambrian.Application/
COPY src/Cambrian.Persistence/Cambrian.Persistence.csproj src/Cambrian.Persistence/
COPY src/Cambrian.Infrastructure/Cambrian.Infrastructure.csproj src/Cambrian.Infrastructure/
COPY src/Cambrian.Api/Cambrian.Api.csproj src/Cambrian.Api/

RUN dotnet restore src/Cambrian.Api/Cambrian.Api.csproj

COPY . .
RUN dotnet publish src/Cambrian.Api/Cambrian.Api.csproj -c Release -o /app/publish /p:UseAppHost=false

FROM mcr.microsoft.com/dotnet/aspnet:8.0 AS final
WORKDIR /app

# ffmpeg powers the Release Ready mastering pipeline (FfmpegEngine two-pass loudnorm).
RUN apt-get update \
    && apt-get install -y --no-install-recommends ffmpeg \
    && rm -rf /var/lib/apt/lists/*

COPY --from=build /app/publish .

# Run as non-root user for container security
RUN addgroup --gid 1001 appgroup && \
    adduser --uid 1001 --ingroup appgroup --no-create-home --disabled-password --gecos "" appuser && \
    chown -R appuser:appgroup /app
USER appuser

# Render injects PORT=10000; locally defaults to 8080
ENV PORT=8080
EXPOSE 8080

# Use exec form with exec so SIGTERM reaches the .NET process for graceful shutdown
ENTRYPOINT ["sh", "-c", "exec env ASPNETCORE_URLS=http://+:$PORT dotnet Cambrian.Api.dll"]
