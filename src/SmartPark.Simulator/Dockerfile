FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /src
COPY src/SmartPark.Contracts/SmartPark.Contracts.csproj src/SmartPark.Contracts/
COPY src/SmartPark.Simulator/SmartPark.Simulator.csproj src/SmartPark.Simulator/
RUN dotnet restore src/SmartPark.Simulator/SmartPark.Simulator.csproj
COPY src/SmartPark.Contracts/ src/SmartPark.Contracts/
COPY src/SmartPark.Simulator/ src/SmartPark.Simulator/
RUN dotnet publish src/SmartPark.Simulator/SmartPark.Simulator.csproj -c Release -o /app --no-restore

FROM mcr.microsoft.com/dotnet/runtime:8.0
WORKDIR /app
COPY --from=build /app .
ENTRYPOINT ["dotnet", "SmartPark.Simulator.dll"]
