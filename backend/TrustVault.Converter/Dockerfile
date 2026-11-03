FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY backend/TrustVault.Converter/TrustVault.Converter.csproj backend/TrustVault.Converter/
RUN dotnet restore backend/TrustVault.Converter/TrustVault.Converter.csproj
COPY backend/TrustVault.Converter/ backend/TrustVault.Converter/
RUN dotnet publish backend/TrustVault.Converter/TrustVault.Converter.csproj \
    --configuration Release \
    --no-restore \
    --output /out

FROM mcr.microsoft.com/dotnet/aspnet:10.0
ENV ASPNETCORE_HTTP_PORTS=8080 \
    HOME=/tmp \
    TMPDIR=/tmp
RUN apt-get update \
    && apt-get install --yes --no-install-recommends libreoffice-writer \
    && rm -rf /var/lib/apt/lists/*
WORKDIR /app
EXPOSE 8080
COPY --from=build --chown=$APP_UID:$APP_UID /out/ ./
USER $APP_UID
ENTRYPOINT ["dotnet", "TrustVault.Converter.dll"]
