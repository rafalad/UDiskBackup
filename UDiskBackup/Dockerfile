# ---------- build ----------
FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /src
COPY UDiskBackup.csproj ./
RUN dotnet restore
COPY . .
RUN dotnet publish -c Release -o /out

# ---------- runtime ----------
FROM mcr.microsoft.com/dotnet/aspnet:8.0
ENV ASPNETCORE_URLS=http://0.0.0.0:8080

# narzędzia: lsblk/rsync/du + dbus + udev (dla udevadm)
RUN apt-get update && apt-get install -y --no-install-recommends \
    util-linux rsync procps coreutils dbus udev ca-certificates && \
    rm -rf /var/lib/apt/lists/*

WORKDIR /app
COPY --from=build /out ./
EXPOSE 8080
ENTRYPOINT ["dotnet","UDiskBackup.dll"]

