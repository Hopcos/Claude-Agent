# ---- Stage 1: build ----
# .NET 10 官方 SDK 镜像。本项目零 NuGet 外部依赖（仅用框架内置包），restore/publish 无需外网。
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY . .
RUN dotnet publish src/ClaudeAgent/ClaudeAgent.csproj -c Release -o /app/publish

# ---- Stage 2: runtime ----
FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime
WORKDIR /app

# curl 用于 compose healthcheck
RUN apt-get update \
    && apt-get install -y --no-install-recommends curl \
    && rm -rf /var/lib/apt/lists/*

# 以 root 运行：便于把宿主机的 Claude Code 凭据文件/卷挂载到 ~/.claude 并支持写回
USER root

COPY --from=build /app/publish .

ENV ASPNETCORE_URLS=http://+:8080
ENV DOTNET_RUNNING_IN_CONTAINER=true
EXPOSE 8080

ENTRYPOINT ["dotnet", "ClaudeAgent.dll"]
