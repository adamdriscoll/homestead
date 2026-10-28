FROM node:24-bookworm-slim AS styles
WORKDIR /work
COPY src/Homestead/package*.json ./
RUN npm ci --no-audit --no-fund
COPY src/Homestead/Pages ./Pages
COPY src/Homestead/Components ./Components
COPY src/Homestead/Themes ./Themes
COPY src/Homestead/Program.cs ./Program.cs
COPY src/Homestead/wwwroot/js/article-editor.js ./wwwroot/js/article-editor.js
COPY src/Homestead/wwwroot/js/settings.js ./wwwroot/js/settings.js
COPY src/Homestead/wwwroot/js/admin-appearance.js ./wwwroot/js/admin-appearance.js
COPY src/Homestead/Features/Publishing/SafeMarkdown.cs ./Features/Publishing/SafeMarkdown.cs
COPY src/Homestead/Features/Publishing/FitnessRendering.cs ./Features/Publishing/FitnessRendering.cs
COPY src/Homestead/Features/Export/HugoTemplates.cs ./Features/Export/HugoTemplates.cs
COPY src/Homestead/Styles ./Styles
COPY src/Homestead/build-icons.mjs ./build-icons.mjs
COPY src/Homestead/build-maps.mjs ./build-maps.mjs
COPY src/Homestead/build-diagrams.mjs ./build-diagrams.mjs
RUN npm run build:css

FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /source
COPY global.json Directory.Build.props Homestead.slnx ./
COPY src/Homestead/Homestead.csproj src/Homestead/
RUN dotnet restore src/Homestead/Homestead.csproj
COPY src/Homestead/ src/Homestead/
COPY --from=styles /work/wwwroot/css/site.css src/Homestead/wwwroot/css/site.css
COPY --from=styles /work/wwwroot/icons/ src/Homestead/wwwroot/icons/
COPY --from=styles /work/Maps/leaflet/ src/Homestead/Maps/leaflet/
COPY --from=styles /work/Diagrams/vendor/ src/Homestead/Diagrams/vendor/
ARG HOMESTEAD_VERSION
RUN set --; \
    if [ -n "$HOMESTEAD_VERSION" ]; then set -- "-p:Version=$HOMESTEAD_VERSION"; fi; \
    dotnet publish src/Homestead/Homestead.csproj -c Release --no-restore -p:BuildFrontend=false -o /app "$@"

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime
USER root
RUN mkdir /data && chown app:app /data && chmod 700 /data
WORKDIR /app
COPY --from=build /app ./
ENV ASPNETCORE_HTTP_PORTS=8080 HOMESTEAD_DATA_PATH=/data ASPNETCORE_ENVIRONMENT=Production
USER app
EXPOSE 8080
ENTRYPOINT ["dotnet", "Homestead.dll"]
