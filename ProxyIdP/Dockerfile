FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS base
USER root
RUN mkdir -p /data /app/plugins && chown -R $APP_UID:$APP_UID /data /app/plugins
USER $APP_UID
WORKDIR /app
EXPOSE 8080
EXPOSE 8081

FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
ARG BUILD_CONFIGURATION=Release
WORKDIR /src
COPY ["ProxyIdP/ProxyIdP.csproj", "ProxyIdP/"]
COPY ["ProxyIdP.Plugins.Abstractions/ProxyIdP.Plugins.Abstractions.csproj", "ProxyIdP.Plugins.Abstractions/"]
RUN dotnet restore "ProxyIdP/ProxyIdP.csproj"
COPY . .
WORKDIR "/src/ProxyIdP"
RUN dotnet build "ProxyIdP.csproj" -c $BUILD_CONFIGURATION -o /app/build

FROM build AS publish
ARG BUILD_CONFIGURATION=Release
RUN dotnet publish "ProxyIdP.csproj" -c $BUILD_CONFIGURATION -o /app/publish /p:UseAppHost=false

FROM base AS final
WORKDIR /app
COPY --from=publish /app/publish .

ENV ASPNETCORE_URLS=http://+:8080 \
	ConnectionStrings__Default="Data Source=/data/proxyidp.db" \
	DataProtection__KeyPath=/data/keys \
	Hosting__UseHttpsRedirection=false \
	ForwardedHeaders__Enabled=false \
	Ui__StylePath=/app/style \
	Ui__CustomUi__Enabled=false \
	Ui__CustomUi__Path=/app/customui \
	Plugins__Path=/app/plugins

VOLUME /data
VOLUME /config
VOLUME /app/style
VOLUME /app/customui
VOLUME /app/plugins
ENTRYPOINT ["dotnet", "ProxyIdP.dll"]
