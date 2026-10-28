FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS base
USER root
RUN mkdir -p /data && chown -R $APP_UID:$APP_UID /data
USER $APP_UID
WORKDIR /app
EXPOSE 8080
EXPOSE 8081

FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
ARG BUILD_CONFIGURATION=Release
WORKDIR /src
COPY ["ProxyIdP/ProxyIdP.csproj", "ProxyIdP/"]
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
	ForwardedHeaders__Enabled=false

VOLUME /data
ENTRYPOINT ["dotnet", "ProxyIdP.dll"]
