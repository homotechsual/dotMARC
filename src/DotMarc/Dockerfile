FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY ["src/DotMarc/DotMarc.csproj", "src/DotMarc/"]
RUN dotnet restore "src/DotMarc/DotMarc.csproj"
COPY . .
WORKDIR "/src/src/DotMarc"
RUN dotnet publish "DotMarc.csproj" -c Release -o /app/publish

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS final
WORKDIR /app
COPY --from=build /app/publish .
ENV ConnectionStrings__DotMarc="Host=postgres;Database=dotmarc;Username=dotmarc;Password=dotmarc"
EXPOSE 8080
ENTRYPOINT ["dotnet", "DotMarc.dll"]
