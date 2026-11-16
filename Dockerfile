FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY Assistant.Bot.csproj ./
RUN dotnet restore
COPY . .
RUN dotnet publish -c Release -o /app

FROM mcr.microsoft.com/dotnet/runtime:10.0
# git: opencode의 스냅샷/변경 감지. ripgrep: 첫 grep 시 런타임 다운로드 회피.
RUN apt-get update \
    && apt-get install -y --no-install-recommends git ripgrep \
    && rm -rf /var/lib/apt/lists/*
WORKDIR /app
COPY --from=build /app ./
COPY --from=build /src/scripts /app/scripts
COPY --from=build /src/opencode/opencode.json /etc/assistant/opencode.json
RUN chmod +x /app/scripts/*.sh
USER $APP_UID
ENTRYPOINT ["dotnet", "Assistant.Bot.dll"]
