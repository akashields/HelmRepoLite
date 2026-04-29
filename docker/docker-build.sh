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

# Auto-detect layout:
#   Published artifacts  -- helmrepolite binary sits next to this script
#   Source repo          -- binary is in artifacts/standalone/linux-x64/
if [[ -f "${SCRIPT_DIR}/helmrepolite" ]]; then
    BUILD_CONTEXT="${SCRIPT_DIR}"
    DOCKERFILE="${SCRIPT_DIR}/Dockerfile"
    BINARY="${SCRIPT_DIR}/helmrepolite"
else
    REPO_ROOT="$(dirname "${SCRIPT_DIR}")"
    BUILD_CONTEXT="${REPO_ROOT}"
    DOCKERFILE="${SCRIPT_DIR}/Dockerfile"
    BINARY="${REPO_ROOT}/artifacts/standalone/linux-x64/helmrepolite"
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
    if [[ "${BUILD_CONTEXT}" == "${SCRIPT_DIR}" ]]; then
        echo "The artifacts/docker/ directory appears incomplete. Re-run build.ps1 -Targets linux-x64."
    else
        echo "Run  .\build.ps1 -Targets linux-x64  from Windows first, then re-run this script."
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