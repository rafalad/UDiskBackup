# ---------- build ----------
FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /src
COPY UDiskBackup/UDiskBackup.csproj ./UDiskBackup/
RUN dotnet restore UDiskBackup/UDiskBackup.csproj
COPY . .
RUN dotnet publish UDiskBackup/UDiskBackup.csproj -c Release -o /out

# ---------- runtime ----------
FROM mcr.microsoft.com/dotnet/aspnet:8.0
ENV ASPNETCORE_URLS=http://0.0.0.0:5101

# narzędzia: lsblk/rsync/du + dbus + udev + udisks2 (dla udisksctl)
RUN apt-get update && apt-get install -y --no-install-recommends \
    util-linux rsync procps coreutils dbus udev udisks2 ca-certificates && \
    rm -rf /var/lib/apt/lists/*

WORKDIR /app
COPY --from=build /out ./
EXPOSE 5101
ENTRYPOINT ["dotnet","UDiskBackup.dll"]

