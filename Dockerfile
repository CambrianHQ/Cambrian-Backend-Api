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
COPY --from=build /app/publish .

# Render injects PORT=10000; locally defaults to 8080
ENV PORT=8080
EXPOSE 8080

# Use shell form so $PORT is expanded at runtime
ENTRYPOINT sh -c "ASPNETCORE_URLS=http://+:$PORT dotnet Cambrian.Api.dll"
