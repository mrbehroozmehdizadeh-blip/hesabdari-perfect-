FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

COPY global.json ./
COPY src/Samaneh.Domain/Samaneh.Domain.csproj src/Samaneh.Domain/
COPY src/Samaneh.Api/Samaneh.Api.csproj src/Samaneh.Api/
RUN dotnet restore src/Samaneh.Api/Samaneh.Api.csproj

COPY src/Samaneh.Domain/ src/Samaneh.Domain/
COPY src/Samaneh.Api/ src/Samaneh.Api/
RUN dotnet publish src/Samaneh.Api/Samaneh.Api.csproj -c Release -o /app/publish --no-restore

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime
WORKDIR /app
ENV ASPNETCORE_HTTP_PORTS=8080
EXPOSE 8080
COPY --from=build /app/publish .
USER $APP_UID
ENTRYPOINT ["dotnet", "Samaneh.Api.dll"]
