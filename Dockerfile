FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /src

COPY Cambrian.sln ./
COPY src/Cambrian.Domain/Cambrian.Domain.csproj src/Cambrian.Domain/
COPY src/Cambrian.Application/Cambrian.Application.csproj src/Cambrian.Application/
COPY src/Cambrian.Persistence/Cambrian.Persistence.csproj src/Cambrian.Persistence/
COPY src/Cambrian.Infrastructure/Cambrian.Infrastructure.csproj src/Cambrian.Infrastructure/
COPY src/Cambrian.Api/Cambrian.Api.csproj src/Cambrian.Api/
COPY tests/Cambrian.Api.Tests/Cambrian.Api.Tests.csproj tests/Cambrian.Api.Tests/

RUN dotnet restore Cambrian.sln

COPY . .
RUN dotnet publish src/Cambrian.Api/Cambrian.Api.csproj -c Release -o /app/publish /p:UseAppHost=false

FROM mcr.microsoft.com/dotnet/aspnet:8.0 AS final
WORKDIR /app
COPY --from=build /app/publish .

ENV ASPNETCORE_URLS=http://+:8080
EXPOSE 8080

ENTRYPOINT ["dotnet", "Cambrian.Api.dll"]
