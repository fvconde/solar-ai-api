# Multi-stage: o SDK (~800 MB) so existe na etapa de build. A imagem final
# carrega apenas o runtime ASP.NET (~220 MB) e o publish -- nada de codigo-fonte.
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

# Copiar so o csproj antes do resto: enquanto as dependencias nao mudarem, o
# Docker reaproveita a camada de restore em cache e o rebuild leva segundos.
COPY global.json ./
COPY src/Solar.Api/Solar.Api.csproj src/Solar.Api/
RUN dotnet restore src/Solar.Api/Solar.Api.csproj

COPY src/ src/
RUN dotnet publish src/Solar.Api/Solar.Api.csproj -c Release -o /app/publish --no-restore

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime
WORKDIR /app

# O Npgsql sonda a biblioteca de Kerberos ao abrir a primeira conexao. A imagem
# aspnet nao a carrega, e o que sai e um par de linhas em stderr que parece
# falha de banco e nao e -- a conexao segue por senha e funciona. Instalar aqui
# custa ~1 MB e tira do log de boot a unica linha que grita erro sem haver erro.
# Antes do USER: apt precisa de root.
RUN apt-get update \
    && apt-get install -y --no-install-recommends libgssapi-krb5-2 \
    && rm -rf /var/lib/apt/lists/*

COPY --from=build /app/publish .

# Sem HTTPS dentro do container: o certificado de dev do .NET nao existe aqui e
# quem termina TLS em producao e o Cloud Run (S-26), nao a aplicacao.
ENV ASPNETCORE_HTTP_PORTS=8080
EXPOSE 8080

# Usuario nao-root que ja vem na imagem oficial.
USER $APP_UID
ENTRYPOINT ["dotnet", "Solar.Api.dll"]
