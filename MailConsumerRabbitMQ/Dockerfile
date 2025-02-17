# Runtime image
FROM mcr.microsoft.com/dotnet/runtime:8.0 AS base
WORKDIR /app

# Build image
FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /src

# Projeyi kopyala
COPY ["MailConsumerRabbitMQ/MailConsumerRabbitMQ.csproj", "MailConsumerRabbitMQ/"]

# Baðýmlýlýklarý yükle
RUN dotnet restore "MailConsumerRabbitMQ/MailConsumerRabbitMQ.csproj"

# Kalan dosyalarý kopyala
COPY . . 

# Proje dizinine geçiþ yap
WORKDIR "/src/MailConsumerRabbitMQ"

# Build et
RUN dotnet build "MailConsumerRabbitMQ.csproj" -c Release -o /app/build

# Publish iþlemi
FROM build AS publish
RUN dotnet publish "MailConsumerRabbitMQ.csproj" -c Release -o /app/publish /p:UseAppHost=false

# Final image
FROM base AS final
WORKDIR /app

# Publish'ten kopyalanan dosyalarý al
COPY --from=publish /app/publish .

# Program.cs içindeki Main metodunu çalýþtýr
ENTRYPOINT ["dotnet", "MailConsumerRabbitMQ.dll"]
