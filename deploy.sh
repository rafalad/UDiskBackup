#!/bin/bash

# Build and Deploy Script for zotac@ubuntu system
# Usage: ./deploy.sh [build|run|stop|restart]

APP_NAME="UDiskBackup"
BUILD_DIR="./UDiskBackup/bin/Release/net8.0/linux-x64"
SERVICE_PORT="5101"

case "${1:-help}" in
  build)
    echo "🔨 Building application for production (linux-x64)..."
    dotnet publish UDiskBackup/UDiskBackup.csproj \
      -c Release \
      -r linux-x64 \
      --self-contained false \
      -p:PublishSingleFile=false
    echo "✅ Build completed in: $BUILD_DIR"
    ;;
    
  run)
    echo "🚀 Starting UDiskBackup on port $SERVICE_PORT..."
    cd UDiskBackup
    ASPNETCORE_ENVIRONMENT=Production \
    ASPNETCORE_URLS="http://0.0.0.0:$SERVICE_PORT" \
    dotnet run
    ;;
    
  stop)
    echo "🛑 Stopping UDiskBackup..."
    pkill -f "dotnet.*UDiskBackup" && echo "✅ Stopped" || echo "❌ Process not found"
    ;;
    
  restart)
    $0 stop
    sleep 2
    $0 run
    ;;
    
  status)
    echo "📊 UDiskBackup Status:"
    ps aux | grep -E "dotnet.*UDiskBackup" | grep -v grep || echo "❌ Not running"
    if curl -s http://localhost:$SERVICE_PORT/api/debug/version >/dev/null 2>&1; then
      echo "✅ Service responding on port $SERVICE_PORT"
    else
      echo "❌ Service not responding on port $SERVICE_PORT"
    fi
    ;;
    
  logs)
    echo "📋 Recent logs:"
    journalctl -u udiskbackup -n 50 --no-pager 2>/dev/null || \
    find /var/log -name "*udisk*" -o -name "*backup*" 2>/dev/null | head -5
    ;;
    
  help|*)
    echo "📚 UDiskBackup Deploy Script"
    echo ""
    echo "Commands:"
    echo "  build    - Build application for production (linux-x64)"
    echo "  run      - Start application in production mode"
    echo "  stop     - Stop running application"
    echo "  restart  - Stop and start application"
    echo "  status   - Check application status"
    echo "  logs     - Show recent logs"
    echo ""
    echo "🖥️  Designed for: zotac@ubuntu system"
    echo "📁 Source path: /mnt/shared"
    echo "🔌 Port: $SERVICE_PORT"
    ;;
esac