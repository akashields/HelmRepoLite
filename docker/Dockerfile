# ---------------------------------------------------------------------------
# HelmRepoLite — Linux container image
#
# Build context: repository root.
# The binary must already exist at artifacts/standalone/linux-x64/helmrepolite.
# Run  .\build.ps1 -Targets linux-x64  (or the full build) before docker build.
#
# Usage (from repo root in WSL):
#   docker build -f docker/Dockerfile -t helmrepolite:1.0.0 .
# ---------------------------------------------------------------------------

# runtime-deps is the minimal glibc image for self-contained .NET binaries.
# It has no .NET runtime — the app carries everything it needs.
FROM mcr.microsoft.com/dotnet/runtime-deps:10.0

ARG UID=1001
ARG GID=1001

WORKDIR /app

# Copy the trimmed, self-contained binary produced by build.ps1
COPY artifacts/standalone/linux-x64/helmrepolite ./helmrepolite
RUN chmod +x ./helmrepolite

# Non-root user and group
RUN groupadd --gid ${GID} helmrepo \
 && useradd  --uid ${UID} --gid ${GID} --no-create-home helmrepo

# Chart storage directory; mount a PVC here in Kubernetes
RUN mkdir -p /charts && chown helmrepo:helmrepo /charts

USER helmrepo

# HTTP port (HTTPS can be added via --https-port; see README)
EXPOSE 8080

VOLUME ["/charts"]

ENTRYPOINT ["./helmrepolite"]
# Default args — all overridable via env vars (HELMREPOLITE_*)
CMD ["--storage-dir=/charts", "--port=8080", "--host=0.0.0.0"]
