FROM node:24-bookworm-slim AS styles
WORKDIR /work
COPY src/Homestead/package*.json ./
RUN npm ci --no-audit --no-fund
COPY src/Homestead/Pages ./Pages
COPY src/Homestead/Program.cs ./Program.cs
COPY src/Homestead/Styles ./Styles
RUN npm run build:css

FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /source
COPY global.json Directory.Build.props Homestead.slnx ./
COPY src/Homestead/Homestead.csproj src/Homestead/
RUN dotnet restore src/Homestead/Homestead.csproj
COPY src/Homestead/ src/Homestead/
COPY --from=styles /work/wwwroot/css/site.css src/Homestead/wwwroot/css/site.css
RUN dotnet publish src/Homestead/Homestead.csproj -c Release --no-restore -p:BuildFrontend=false -o /app

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime
USER root
RUN mkdir /data && chown app:app /data && chmod 700 /data
WORKDIR /app
COPY --from=build /app ./
ENV ASPNETCORE_HTTP_PORTS=8080 HOMESTEAD_DATA_PATH=/data ASPNETCORE_ENVIRONMENT=Production
USER app
EXPOSE 8080
ENTRYPOINT ["dotnet", "Homestead.dll"]
