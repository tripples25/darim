# syntax=docker/dockerfile:1
FROM mcr.microsoft.com/dotnet/sdk:10.0.403 AS build
WORKDIR /source
COPY . .
# Optional combined CA bundle for builds inside the managed development environment.
RUN --mount=type=secret,id=proxy_ca \
    if [ -f /run/secrets/proxy_ca ]; then export SSL_CERT_FILE=/run/secrets/proxy_ca; fi; \
    dotnet restore src/Wishlist.Server/Wishlist.Server.csproj --locked-mode && \
    dotnet publish src/Wishlist.Server/Wishlist.Server.csproj -c Release --no-restore -o /out
FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime
RUN apt-get update && apt-get install -y --no-install-recommends libgssapi-krb5-2 && \
    rm -rf /var/lib/apt/lists/*
WORKDIR /app
COPY --from=build /out .
ENV ASPNETCORE_HTTP_PORTS=8080
RUN mkdir -p /keys && chown $APP_UID:$APP_UID /keys
USER $APP_UID
EXPOSE 8080
ENTRYPOINT ["dotnet", "Wishlist.Server.dll"]
