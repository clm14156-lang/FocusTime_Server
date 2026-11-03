FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /source
COPY global.json Directory.Build.props NuGet.Config ./
COPY src/FocusTime.Api/FocusTime.Api.csproj src/FocusTime.Api/
RUN dotnet restore src/FocusTime.Api/FocusTime.Api.csproj
COPY src/ src/
RUN dotnet publish src/FocusTime.Api/FocusTime.Api.csproj -c Release --no-restore -o /app/publish /p:UseAppHost=false

FROM mcr.microsoft.com/dotnet/aspnet:10.0
WORKDIR /app
COPY --from=build /app/publish/ ./
RUN mkdir -p /app/.artifacts/mail && chown -R app:app /app/.artifacts && chmod 700 /app/.artifacts
ENV ASPNETCORE_HTTP_PORTS=8080
USER app
EXPOSE 8080
ENTRYPOINT ["dotnet", "FocusTime.Api.dll"]
