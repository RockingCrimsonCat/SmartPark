# Збирання та запуск серверного застосунку. Контекст – корінь репозиторію.
FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /src
COPY src/SmartPark.Contracts/SmartPark.Contracts.csproj src/SmartPark.Contracts/
COPY src/SmartPark.Data/SmartPark.Data.csproj src/SmartPark.Data/
COPY src/SmartPark.Server/SmartPark.Server.csproj src/SmartPark.Server/
RUN dotnet restore src/SmartPark.Server/SmartPark.Server.csproj
COPY src/ src/
RUN dotnet publish src/SmartPark.Server/SmartPark.Server.csproj -c Release -o /app --no-restore

FROM mcr.microsoft.com/dotnet/aspnet:8.0
# Шрифт з кирилицею для PDF-чеків і часові пояси.
RUN apt-get update && apt-get install -y --no-install-recommends fonts-dejavu-core tzdata \
    && rm -rf /var/lib/apt/lists/*
WORKDIR /app
COPY --from=build /app .
ENV ASPNETCORE_URLS=http://+:5000
EXPOSE 5000
ENTRYPOINT ["dotnet", "SmartPark.Server.dll"]
