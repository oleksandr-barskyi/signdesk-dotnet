FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY Directory.Build.props .editorconfig ./
COPY src/SignDesk.Domain/SignDesk.Domain.csproj src/SignDesk.Domain/
COPY src/SignDesk.Infrastructure/SignDesk.Infrastructure.csproj src/SignDesk.Infrastructure/
COPY src/SignDesk.Api/SignDesk.Api.csproj src/SignDesk.Api/
RUN dotnet restore src/SignDesk.Api/SignDesk.Api.csproj
COPY src/ src/
RUN dotnet publish src/SignDesk.Api/SignDesk.Api.csproj -c Release -o /app --no-restore

FROM mcr.microsoft.com/dotnet/aspnet:10.0
WORKDIR /app
COPY --from=build /app .
USER $APP_UID
ENV ASPNETCORE_HTTP_PORTS=8080
EXPOSE 8080
ENTRYPOINT ["dotnet", "SignDesk.Api.dll"]
