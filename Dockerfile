FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /src

# Sadece proje dosyasını kopyalayıp restore yapalım (Önbellek avantajı için)
COPY ["AdsFinder.csproj", "./"]
RUN dotnet restore "AdsFinder.csproj"

# Geri kalan her şeyi kopyalayalım
COPY . .
RUN dotnet build "AdsFinder.csproj" -c Release -o /app/build

# Yayınlama aşaması
FROM build AS publish
RUN dotnet publish "AdsFinder.csproj" -c Release -o /app/publish /p:UseAppHost=false

# Runtime (Çalışma) aşaması
FROM mcr.microsoft.com/dotnet/aspnet:8.0 AS final
WORKDIR /app
COPY --from=publish /app/publish .

ENV ASPNETCORE_URLS=http://+:10000
EXPOSE 10000

ENTRYPOINT ["dotnet", "AdsFinder.dll"]