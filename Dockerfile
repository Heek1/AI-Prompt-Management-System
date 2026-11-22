# syntax=docker/dockerfile:1

# ---- build ----
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

# Restore first, from project files only, so the layer is cached until a
# package reference changes.
COPY Directory.Build.props ./
COPY src/PromptManager/PromptManager.csproj src/PromptManager/
RUN dotnet restore src/PromptManager/PromptManager.csproj

COPY src/PromptManager/ src/PromptManager/
RUN dotnet publish src/PromptManager/PromptManager.csproj \
    --configuration Release --no-restore --output /app

# ---- runtime ----
FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime
WORKDIR /app

# All state, including the data-protection keys, lives in PostgreSQL
# (ConnectionStrings__DefaultConnection), so the container needs no volume.
ENV ASPNETCORE_HTTP_PORTS=8080

COPY --from=build /app .

USER $APP_UID
EXPOSE 8080
ENTRYPOINT ["dotnet", "PromptManager.dll"]
