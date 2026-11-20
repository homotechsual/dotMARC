# Build on the machine's own architecture and cross-compile for the target one, so a multi-platform build
# doesn't run the whole .NET SDK under emulation for arm64. The final image stays on the target platform.
FROM --platform=$BUILDPLATFORM mcr.microsoft.com/dotnet/sdk:10.0 AS build
ARG TARGETARCH
WORKDIR /src
COPY ["src/DotMarc/DotMarc.csproj", "src/DotMarc/"]
RUN dotnet restore "src/DotMarc/DotMarc.csproj" -a $TARGETARCH
COPY . .
WORKDIR "/src/src/DotMarc"
RUN dotnet publish "DotMarc.csproj" -c Release -a $TARGETARCH -o /app/publish

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS final
# Set true for the "next" image so it shows the dev banner and a (dev) version wherever it runs. Released images leave it false.
ARG DEV_BUILD=false
WORKDIR /app
COPY --from=build /app/publish .
ENV DevBuild__Enabled=$DEV_BUILD
ENV ConnectionStrings__DotMarc="Host=postgres;Database=dotmarc;Username=dotmarc;Password=dotmarc"
EXPOSE 8080
ENTRYPOINT ["dotnet", "DotMarc.dll"]
