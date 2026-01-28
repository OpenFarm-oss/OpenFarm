FROM mcr.microsoft.com/dotnet/runtime:10.0 AS base
USER $APP_UID
WORKDIR /app

FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
ARG BUILD_CONFIGURATION=Release
WORKDIR /src
COPY ["GoogleFormListener/GoogleFormListener.csproj", "GoogleFormListener/"]
RUN dotnet restore "GoogleFormListener/GoogleFormListener.csproj"
COPY . .
WORKDIR "/src/GoogleFormListener"
RUN dotnet build "GoogleFormListener.csproj" -c $BUILD_CONFIGURATION -o /app/build

FROM build AS publish
ARG BUILD_CONFIGURATION=Release
RUN dotnet publish "GoogleFormListener.csproj" -c $BUILD_CONFIGURATION -o /app/publish /p:UseAppHost=false

FROM base AS final
WORKDIR /app
COPY --from=publish /app/publish .
COPY /GoogleFormListener/${GOOGLE_SERVICE_ACCOUNT_CRED_FILE} .
ENTRYPOINT ["dotnet", "GoogleFormListener.dll"]
