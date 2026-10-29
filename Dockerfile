# syntax=docker/dockerfile:1
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY src/Msg2Dc/Msg2Dc.csproj src/Msg2Dc/
RUN dotnet restore src/Msg2Dc/Msg2Dc.csproj
COPY src/Msg2Dc/ src/Msg2Dc/
RUN dotnet publish src/Msg2Dc/Msg2Dc.csproj -c Release -o /app --no-restore

FROM mcr.microsoft.com/dotnet/aspnet:10.0
WORKDIR /app
COPY --from=build /app .
ENV ASPNETCORE_ENVIRONMENT=Production
EXPOSE 8080
ENTRYPOINT ["dotnet", "Msg2Dc.dll"]
