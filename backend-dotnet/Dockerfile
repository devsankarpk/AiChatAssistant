# syntax=docker/dockerfile:1
#
# Build context MUST be the repo root (global.json and .config/dotnet-tools.json live there):
#   docker build -f backend-dotnet/Dockerfile --target api     -t aichat-api:local .
#   docker build -f backend-dotnet/Dockerfile --target migrate -t aichat-migrate:local .

FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /src
COPY global.json ./
COPY .config/dotnet-tools.json .config/
COPY backend-dotnet/src/AiChatAssistant.Api/AiChatAssistant.Api.csproj backend-dotnet/src/AiChatAssistant.Api/
RUN dotnet restore backend-dotnet/src/AiChatAssistant.Api/AiChatAssistant.Api.csproj
COPY backend-dotnet/src backend-dotnet/src
RUN dotnet publish backend-dotnet/src/AiChatAssistant.Api/AiChatAssistant.Api.csproj \
      -c Release --no-restore -o /out/api \
 # appsettings.Development.json carries the local dev DB password - never ship it in an image.
 && rm -f /out/api/appsettings.Development.json


# Self-contained EF Core migration bundle: applies migrations with no SDK and no app startup.
FROM build AS bundle
RUN dotnet tool restore
# AppDbContextFactory (design-time only) means EF never builds the real app host, so no live
# MySQL and no Jwt__Key / PythonService__InternalApiKey are needed here or when the bundle runs -
# only ConnectionStrings__Default. Production keeps appsettings.Development.json out of play.
ENV ASPNETCORE_ENVIRONMENT=Production
RUN dotnet ef migrations bundle \
      --project backend-dotnet/src/AiChatAssistant.Api \
      --configuration Release --self-contained -r linux-x64 \
      --output /out/efbundle --force


FROM mcr.microsoft.com/dotnet/runtime-deps:8.0 AS migrate
WORKDIR /app
ENV ASPNETCORE_ENVIRONMENT=Production
COPY --from=bundle /out/efbundle ./efbundle
USER app
ENTRYPOINT ["./efbundle"]


FROM mcr.microsoft.com/dotnet/aspnet:8.0 AS api
WORKDIR /app
COPY --from=build /out/api .
ENV ASPNETCORE_URLS=http://+:8080 \
    ASPNETCORE_ENVIRONMENT=Production
EXPOSE 8080
USER app
ENTRYPOINT ["dotnet", "AiChatAssistant.Api.dll"]
