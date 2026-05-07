FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY . .
RUN dotnet publish QUEUEDUP.csproj -c Release -o /app/out

FROM mcr.microsoft.com/dotnet/aspnet:10.0
WORKDIR /app
COPY --from=build /app/out .
RUN mkdir -p /data
ENV DbPath="Data Source=/data/queuedup.db"
EXPOSE 8080
ENTRYPOINT ["dotnet", "QUEUEDUP.dll"]
