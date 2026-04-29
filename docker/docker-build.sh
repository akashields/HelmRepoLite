#!/usr/bin/env bash
# ---------------------------------------------------------------------------
# Build the HelmRepoLite Docker image and push it to a container registry.
#
# Usage:
#   ./docker-build.sh <registry> <username> <password> [version] [image-name]
#
# This script works in two layouts:
#
# 1. From published artifacts (artifacts/docker/ after running build.ps1):
#      ./docker-build.sh ghcr.io/myorg myuser mytoken 1.2.3
#    The binary and Dockerfile are in the same directory as this script.
#
# 2. From the source repository (docker/ directory):
#      ./docker/docker-build.sh ghcr.io/myorg myuser mytoken 1.2.3
#    Run  .\build.ps1 -Targets linux-x64  on Windows first.
# ---------------------------------------------------------------------------
set -euo pipefail

REGISTRY="${1:?ERROR: registry is required. Usage: $0 <registry> <username> <password> [version] [image-name]}"
USERNAME="${2:?ERROR: username is required.}"
PASSWORD="${3:?ERROR: password is required.}"
VERSION="${4:-latest}"
IMAGE_NAME="${5:-helmrepolite}"

FULL_IMAGE="${REGISTRY}/${IMAGE_NAME}:${VERSION}"

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
PARENT_DIR="$(dirname "${SCRIPT_DIR}")"

# Detect layout by checking for the src/ directory that only exists at the repo root.
#
#   Source repo  (./docker/docker-build.sh):   PARENT_DIR is the repo root → src/ is present
#   Artifacts    (artifacts/docker/docker-build.sh): PARENT_DIR is artifacts/ → no src/
if [[ -d "${PARENT_DIR}/src" ]]; then
    # Running from the source repository's docker/ directory
    BUILD_CONTEXT="${PARENT_DIR}"
    DOCKERFILE="${SCRIPT_DIR}/Dockerfile"
    BINARY="${PARENT_DIR}/artifacts/standalone/linux-x64/helmrepolite"
elif [[ -f "${SCRIPT_DIR}/helmrepolite" ]]; then
    # Running from the published artifacts/docker/ directory
    BUILD_CONTEXT="${SCRIPT_DIR}"
    DOCKERFILE="${SCRIPT_DIR}/Dockerfile"
    BINARY="${SCRIPT_DIR}/helmrepolite"
else
    echo "ERROR: Cannot determine layout."
    echo "Run from the repo's docker/ directory or from a published artifacts/docker/ directory."
    exit 1
fi

echo "========================================"
echo " HelmRepoLite Docker Build & Push"
echo "========================================"
echo "  Image  : ${FULL_IMAGE}"
echo "  Context: ${BUILD_CONTEXT}"
echo ""

# Verify the binary exists before attempting the build
if [[ ! -f "${BINARY}" ]]; then
    echo "ERROR: Binary not found at ${BINARY}"
    if [[ -d "${PARENT_DIR}/src" ]]; then
        echo "Run  .\\build.ps1 -Targets linux-x64  from Windows first, then re-run this script."
    else
        echo "The artifacts/docker/ directory appears incomplete. Re-run build.ps1 -Targets linux-x64."
    fi
    exit 1
fi

# Login
echo "--- Logging in to ${REGISTRY} ---"
echo "${PASSWORD}" | docker login "${REGISTRY}" --username "${USERNAME}" --password-stdin

# Build
echo ""
echo "--- Building ${FULL_IMAGE} ---"
docker build \
    --file "${DOCKERFILE}" \
    --tag  "${FULL_IMAGE}" \
    "${BUILD_CONTEXT}"

# Push
echo ""
echo "--- Pushing ${FULL_IMAGE} ---"
docker push "${FULL_IMAGE}"

echo ""
echo "Done: ${FULL_IMAGE}"
echo ""
echo "Deploy to Kubernetes:"
echo "  helm upgrade --install helmrepolite ./helm/helmrepolite \\"
echo "    --set image.repository=${REGISTRY}/${IMAGE_NAME} \\"
echo "    --set image.tag=${VERSION}"