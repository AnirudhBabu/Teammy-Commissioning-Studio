# syntax=docker/dockerfile:1.7
ARG SDK_IMAGE=mcr.microsoft.com/dotnet/sdk:10.0.402-noble@sha256:747cbade5bfd918a175ac32f8f1d931edec2c7d0d5397a3de72eaef1ac1f66d2
ARG RUNTIME_IMAGE=mcr.microsoft.com/dotnet/aspnet:10.0.13-noble-chiseled@sha256:5165aeeb688df15e869a56a7584bb94bf16a14e0b3ba77b49fcdbe350f6b8020
FROM ${SDK_IMAGE} AS build
WORKDIR /source
COPY global.json Directory.Build.props Directory.Packages.props NuGet.config ./
COPY src ./src
RUN dotnet restore src/Teammy.Api/Teammy.Api.csproj --locked-mode
RUN dotnet publish src/Teammy.Api/Teammy.Api.csproj --configuration Release --no-restore \
    --output /app /p:UseAppHost=false

FROM ${RUNTIME_IMAGE} AS final
WORKDIR /app
COPY --from=build /app ./
ENV ASPNETCORE_HTTP_PORTS=8080 \
    DOTNET_EnableDiagnostics=0
EXPOSE 8080
USER $APP_UID
ENTRYPOINT ["dotnet", "Teammy.Api.dll"]
