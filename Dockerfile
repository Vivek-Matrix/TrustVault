FROM node:22-alpine AS frontend-build
WORKDIR /src/frontend
COPY frontend/package.json frontend/package-lock.json ./
RUN npm ci
COPY frontend/ ./
RUN npm run build

FROM mcr.microsoft.com/dotnet/sdk:10.0 AS backend-build
WORKDIR /src
COPY TrustVault.sln ./
COPY backend/TrustVault.Api/TrustVault.Api.csproj backend/TrustVault.Api/
COPY backend/TrustVault.Application/TrustVault.Application.csproj backend/TrustVault.Application/
COPY backend/TrustVault.Domain/TrustVault.Domain.csproj backend/TrustVault.Domain/
COPY backend/TrustVault.Infrastructure/TrustVault.Infrastructure.csproj backend/TrustVault.Infrastructure/
COPY backend/TrustVault.Migrator/TrustVault.Migrator.csproj backend/TrustVault.Migrator/
COPY backend/TrustVault.Tests/TrustVault.Tests.csproj backend/TrustVault.Tests/
COPY backend/TrustVault.Converter/TrustVault.Converter.csproj backend/TrustVault.Converter/
RUN dotnet restore TrustVault.sln
COPY backend/ backend/
RUN dotnet publish backend/TrustVault.Api/TrustVault.Api.csproj --configuration Release --no-restore --output /out/api
RUN dotnet publish backend/TrustVault.Migrator/TrustVault.Migrator.csproj --configuration Release --no-restore --output /out/migrator

FROM mcr.microsoft.com/dotnet/aspnet:10.0-alpine AS runtime
WORKDIR /app
ENV ASPNETCORE_HTTP_PORTS=10000
EXPOSE 10000
COPY --from=backend-build --chown=$APP_UID:$APP_UID /out/api/ ./
COPY --from=backend-build --chown=$APP_UID:$APP_UID /out/migrator/ ./migrator/
COPY --from=frontend-build --chown=$APP_UID:$APP_UID /src/frontend/dist/trustvault-web/browser/ ./wwwroot/
USER $APP_UID
ENTRYPOINT ["dotnet", "TrustVault.Api.dll"]
