FROM mcr.microsoft.com/dotnet/sdk:8.0-alpine3.23 AS build

WORKDIR /app

COPY *.csproj .
RUN dotnet restore

COPY . .

RUN dotnet publish -c Release -o out

FROM mcr.microsoft.com/dotnet/runtime:8.0-alpine3.23-extra AS final

WORKDIR /app

COPY --from=build /app/out .

EXPOSE 4001
ENV BRIDGE_LISTEN_PREFIX="http://+:4001/"

CMD ["dotnet", "websocketserver.dll"]
