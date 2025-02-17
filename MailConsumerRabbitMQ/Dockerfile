# Temel imaj olarak .NET SDK kullan
FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build

# Çalýþma dizinini ayarla
WORKDIR /app

# Proje dosyasýný kopyala ve baðýmlýlýklarý yükle
COPY *.csproj ./
RUN dotnet restore

# Uygulamayý derle
COPY . ./
RUN dotnet publish -c Release -o /out

# Çalýþtýrma için temel imaj olarak .NET Runtime kullan
FROM mcr.microsoft.com/dotnet/aspnet:8.0

# Çalýþma dizinini ayarla
WORKDIR /app

# Yayýnlanan dosyalarý kopyala
COPY --from=build /out .

# Uygulamayý çalýþtýr
ENTRYPOINT ["dotnet", "MailConsumerRabbitMQ.dll"]
