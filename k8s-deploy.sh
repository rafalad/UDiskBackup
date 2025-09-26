#!/bin/bash

# UDiskBackup Deployment Script for k3s
# Deploys to 'apps' namespace on zotac@ubuntu system

set -e

PROJECT_NAME="udiskbackup"
IMAGE_NAME="udiskbackup:latest"
NAMESPACE="apps"
REMOTE_HOST="zotac@192.168.1.109"
APP_DIR="/home/zotac/udiskbackup"

echo "🚀 UDiskBackup Deployment to k3s"
echo "================================="

# Check if we're deploying locally or remotely
if [ "$1" = "local" ]; then
    echo "📦 Building Docker image locally..."
    cd UDiskBackup
    docker build -t $IMAGE_NAME .
    cd ..
    
    echo "🔧 Creating namespace if it doesn't exist..."
    kubectl create namespace $NAMESPACE --dry-run=client -o yaml | kubectl apply -f -
    
    echo "🚢 Deploying to k3s..."
    kubectl apply -f UDiskBackup/k8s.yaml
    
    echo "⏳ Waiting for deployment..."
    kubectl wait --for=condition=available --timeout=300s deployment/$PROJECT_NAME -n $NAMESPACE
    
    echo "📋 Checking deployment status..."
    kubectl get pods -n $NAMESPACE -l app=$PROJECT_NAME
    kubectl get service -n $NAMESPACE -l app=$PROJECT_NAME
    kubectl get ingress -n $NAMESPACE -l app=$PROJECT_NAME
    
    echo "✅ Local deployment completed!"
    echo "🌐 Access the application at: http://udiskbackup.local"
    echo "📊 Check logs with: kubectl logs -n $NAMESPACE -l app=$PROJECT_NAME -f"
    
else
    echo "🔄 Deploying to remote k3s cluster..."
    
    # Create deployment directory on remote host
    echo "📁 Creating deployment directory on $REMOTE_HOST..."
    ssh $REMOTE_HOST "mkdir -p $APP_DIR"
    
    # Copy project files to remote host
    echo "📤 Copying project files to $REMOTE_HOST..."
    rsync -av --exclude='.git' --exclude='bin' --exclude='obj' --exclude='*.log' \
        ./ $REMOTE_HOST:$APP_DIR/
    
    # Execute remote deployment
    echo "🏗️ Building and deploying on $REMOTE_HOST..."
    ssh $REMOTE_HOST << EOF
        set -e
        cd $APP_DIR/UDiskBackup
        
        echo "📦 Building Docker image..."
        sudo docker build -t $IMAGE_NAME .
        
        echo "🔧 Creating namespace if it doesn't exist..."
        sudo k3s kubectl create namespace $NAMESPACE --dry-run=client -o yaml | sudo k3s kubectl apply -f -
        
        echo "🚢 Deploying to k3s..."
        sudo k3s kubectl apply -f k8s.yaml
        
        echo "⏳ Waiting for deployment..."
        sudo k3s kubectl wait --for=condition=available --timeout=300s deployment/$PROJECT_NAME -n $NAMESPACE
        
        echo "📋 Checking deployment status..."
        sudo k3s kubectl get pods -n $NAMESPACE -l app=$PROJECT_NAME
        sudo k3s kubectl get service -n $NAMESPACE -l app=$PROJECT_NAME
        sudo k3s kubectl get ingress -n $NAMESPACE -l app=$PROJECT_NAME
        
        echo "✅ Remote deployment completed!"
EOF
    
    echo "🌐 Application should be accessible at: http://udiskbackup.local"
    echo "📊 Check logs with: ssh $REMOTE_HOST 'sudo k3s kubectl logs -n $NAMESPACE -l app=$PROJECT_NAME -f'"
fi

echo ""
echo "🔧 Management commands:"
echo "  - Restart: kubectl rollout restart deployment/$PROJECT_NAME -n $NAMESPACE"
echo "  - Scale: kubectl scale deployment/$PROJECT_NAME --replicas=1 -n $NAMESPACE"
echo "  - Delete: kubectl delete -f UDiskBackup/k8s.yaml"
echo "  - Logs: kubectl logs -n $NAMESPACE -l app=$PROJECT_NAME -f"