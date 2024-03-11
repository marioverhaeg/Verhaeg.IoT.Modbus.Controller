#See https://aka.ms/customizecontainer to learn how to customize your debug container and how Visual Studio uses this Dockerfile to build your images for faster debugging.

FROM mcr.microsoft.com/dotnet/runtime:7.0 AS base
WORKDIR /app

FROM mcr.microsoft.com/dotnet/sdk:7.0 AS build
WORKDIR /src
COPY ["Verhaeg.IoT.Modbus.Controller.csproj", "."]
RUN dotnet restore "./Verhaeg.IoT.Modbus.Controller.csproj"
COPY . .
WORKDIR "/src/."
RUN dotnet build "Verhaeg.IoT.Modbus.Controller.csproj" -c Release -o /app/build

FROM build AS publish
RUN dotnet publish "Verhaeg.IoT.Modbus.Controller.csproj" -c Release -o /app/publish /p:UseAppHost=false

FROM base AS final
WORKDIR /app
COPY --from=publish /app/publish .
ENTRYPOINT ["dotnet", "Verhaeg.IoT.Modbus.Controller.dll"]