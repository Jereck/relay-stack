# Builds any RelayStack .NET host: docker build --build-arg PROJECT=RelayStack.Api -f deploy/Dockerfile .
ARG DOTNET_VERSION=10.0
FROM mcr.microsoft.com/dotnet/sdk:${DOTNET_VERSION} AS build
ARG PROJECT
WORKDIR /src
COPY global.json Directory.Build.props Directory.Packages.props ./
COPY src/ src/
RUN dotnet publish src/${PROJECT}/${PROJECT}.csproj -c Release -o /app /p:UseAppHost=false

FROM mcr.microsoft.com/dotnet/aspnet:${DOTNET_VERSION} AS runtime
ARG PROJECT
ENV APP=${PROJECT} \
    DOTNET_RUNNING_IN_CONTAINER=true \
    ASPNETCORE_URLS=http://+:8080
WORKDIR /app
COPY --from=build /app .
RUN mkdir -p /data/objects && chown -R ${APP_UID} /data
USER ${APP_UID}
EXPOSE 8080
ENTRYPOINT ["sh", "-c", "exec dotnet /app/$APP.dll \"$@\"", "--"]
