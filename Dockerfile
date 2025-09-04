# .NET 8 ASP.NET Core API - multi-stage build

# Build stage
FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /src

# Copy solution and project files first (for better layer caching)
COPY nuuz.api/Nuuz.sln nuuz.api/
COPY nuuz.api/Nuuz.Api.csproj nuuz.api/
COPY Nuuz.Application/Nuuz.Application.csproj Nuuz.Application/
COPY Nuuz.Common/Nuuz.Common.csproj Nuuz.Common/
COPY Nuuz.Domain/Nuuz.Domain.csproj Nuuz.Domain/
COPY Nuuz.Infrastructure/Nuuz.Infrastructure.csproj Nuuz.Infrastructure/

# Restore dependencies
RUN dotnet restore "nuuz.api/Nuuz.sln"

# Copy the rest of the source
COPY . .

# Publish the API
WORKDIR /src/nuuz.api
RUN dotnet publish "Nuuz.Api.csproj" -c Release -o /app/publish /p:UseAppHost=false


# Runtime image
FROM mcr.microsoft.com/dotnet/aspnet:8.0 AS final
WORKDIR /app

# DigitalOcean App Platform expects services to listen on 8080
ENV ASPNETCORE_URLS=http://+:8080
EXPOSE 8080

COPY --from=build /app/publish .

ENTRYPOINT ["dotnet", "Nuuz.Api.dll"]

