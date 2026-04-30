# helmrepolite

![Version: 0.1.0](https://img.shields.io/badge/Version-0.1.0-informational?style=flat-square) ![Type: application](https://img.shields.io/badge/Type-application-informational?style=flat-square) ![AppVersion: 1.0.0](https://img.shields.io/badge/AppVersion-1.0.0-informational?style=flat-square)

A lightweight, self-hosted, ChartMuseum-compatible Helm chart repository server. Zero third-party runtime dependencies.

**Homepage:** <https://github.com/akashields/HelmRepoLite>

## Maintainers

| Name | Email | Url |
| ---- | ------ | --- |
| HelmRepoLite Contributors |  |  |

## Source Code

* <https://github.com/akashields/HelmRepoLite>

## Values

| Key | Type | Default | Description |
|-----|------|---------|-------------|
| affinity | object | `{}` | Affinity rules for pod scheduling. |
| fullnameOverride | string | `""` | Override the full resource name prefix. |
| image | object | `{"pullPolicy":"IfNotPresent","repository":"","tag":""}` | Container image configuration. |
| image.pullPolicy | string | `"IfNotPresent"` | Image [pull policy](https://kubernetes.io/docs/concepts/containers/images/#image-pull-policies). |
| image.repository | string | `""` | Image repository. Example: `ghcr.io/myorg/helmrepolite` (**required**). |
| image.tag | string | `""` | Image tag. Defaults to `.Chart.AppVersion` when empty. |
| imagePullSecrets | list | `[]` | Image pull secrets for private registries. |
| ingress | object | `{"annotations":{},"className":"nginx","enabled":false,"host":"","path":"/","pathType":"Prefix","proxyBodySize":"256m","tls":{"enabled":false,"secretName":""}}` | Ingress configuration. **Note:** sub-path deployments (e.g. `example.com/helm`) are not supported. Deploy HelmRepoLite at the root of a hostname. If a sub-path is unavoidable, set `server.chartUrl` to the full external URL manually. |
| ingress.annotations | object | `{}` | Additional annotations merged onto the Ingress resource. |
| ingress.className | string | `"nginx"` | Ingress class name. |
| ingress.enabled | bool | `false` | Create an Ingress resource. |
| ingress.host | string | `""` | Hostname for the Ingress rule (**required** when `enabled: true`). |
| ingress.path | string | `"/"` | URL path. |
| ingress.pathType | string | `"Prefix"` | Path type. |
| ingress.proxyBodySize | string | `"256m"` | nginx `proxy-body-size` annotation value. Must accommodate the largest chart you intend to upload (server hard limit is 256 MiB). |
| ingress.tls | object | `{"enabled":false,"secretName":""}` | TLS configuration for the Ingress. |
| ingress.tls.enabled | bool | `false` | Enable TLS on the Ingress. |
| ingress.tls.secretName | string | `""` | Name of the Kubernetes TLS Secret containing the certificate and key. |
| nameOverride | string | `""` | Override the chart name component of generated resource names. |
| nodeSelector | object | `{}` | Node selector constraints for pod scheduling. |
| persistence | object | `{"accessMode":"ReadWriteOnce","enabled":true,"existingClaim":"","mountPath":"","size":"10Gi","storageClass":""}` | Persistent storage for chart packages. |
| persistence.accessMode | string | `"ReadWriteOnce"` | PVC access mode. |
| persistence.enabled | bool | `true` | Create a PersistentVolumeClaim for chart storage. |
| persistence.existingClaim | string | `""` | Use a pre-existing PVC instead of creating one. |
| persistence.mountPath | string | `""` | Path inside the container where the PVC is mounted. Defaults to `server.storageDir` when empty (PVC root == storage dir). Set this to a parent path when you want `server.storageDir` to be a subdirectory within the PVC — e.g. `mountPath: /data` with `server.storageDir: /data/charts` so files accumulate under `charts/` inside the volume rather than at the volume root. |
| persistence.size | string | `"10Gi"` | PVC size. |
| persistence.storageClass | string | `""` | Storage class name. Uses the cluster default when empty. |
| podAnnotations | object | `{}` | Annotations added to every pod. |
| podSecurityContext | object | `{fsGroup: 1001}` | Pod-level security context. |
| probes | object | `{"liveness":{"failureThreshold":3,"initialDelaySeconds":5,"periodSeconds":30,"timeoutSeconds":3},"readiness":{"failureThreshold":3,"initialDelaySeconds":3,"periodSeconds":10,"timeoutSeconds":3}}` | Liveness and readiness probe timings. |
| probes.liveness | object | `{"failureThreshold":3,"initialDelaySeconds":5,"periodSeconds":30,"timeoutSeconds":3}` | Liveness probe settings. |
| probes.liveness.failureThreshold | int | `3` | Consecutive failures before the pod is restarted. |
| probes.liveness.initialDelaySeconds | int | `5` | Seconds to wait before the first liveness check. |
| probes.liveness.periodSeconds | int | `30` | Seconds between liveness checks. |
| probes.liveness.timeoutSeconds | int | `3` | Seconds before a liveness check times out. |
| probes.readiness | object | `{"failureThreshold":3,"initialDelaySeconds":3,"periodSeconds":10,"timeoutSeconds":3}` | Readiness probe settings. |
| probes.readiness.failureThreshold | int | `3` | Consecutive failures before the pod is removed from service. |
| probes.readiness.initialDelaySeconds | int | `3` | Seconds to wait before the first readiness check. |
| probes.readiness.periodSeconds | int | `10` | Seconds between readiness checks. |
| probes.readiness.timeoutSeconds | int | `3` | Seconds before a readiness check times out. |
| replicaCount | int | `1` | Number of pod replicas.lol |
| resources | object | `{}` | CPU and memory resource requests and limits for the server container. |
| securityContext | object | see values.yaml | Container-level security context. |
| server | object | `{"allowOverwrite":false,"auth":{"enabled":false,"existingSecret":"","password":"","passwordKey":"password","requireAuthGet":false,"username":"","usernameKey":"username"},"chartUrl":"","debug":false,"disableApi":false,"disableDelete":false,"enableShutdown":false,"host":"0.0.0.0","https":{"certPasswordKey":"cert-password","certSecretKey":"cert.pfx","certSecretName":"","enabled":false,"port":8443},"port":8080,"storageDir":"/charts"}` | Server configuration. Each key maps to a CLI flag or `HELMREPOLITE_*` environment variable. |
| server.allowOverwrite | bool | `false` | Allow re-uploading an existing chart version. |
| server.auth | object | `{"enabled":false,"existingSecret":"","password":"","passwordKey":"password","requireAuthGet":false,"username":"","usernameKey":"username"}` | HTTP Basic authentication settings. |
| server.auth.enabled | bool | `false` | Enable HTTP Basic authentication. |
| server.auth.existingSecret | string | `""` | Name of an existing Secret to use instead of generating one. The Secret must contain the keys specified by `usernameKey` and `passwordKey`. |
| server.auth.password | string | `""` | Basic-auth password (stored in a generated Kubernetes Secret). |
| server.auth.passwordKey | string | `"password"` | Key in `existingSecret` that holds the password. |
| server.auth.requireAuthGet | bool | `false` | Require authentication on GET routes. When `false`, anonymous reads are allowed. |
| server.auth.username | string | `""` | Basic-auth username (stored in a generated Kubernetes Secret). |
| server.auth.usernameKey | string | `"username"` | Key in `existingSecret` that holds the username. |
| server.chartUrl | string | `""` | External base URL written into `index.yaml` for chart download links. Auto-detected from `ingress.host` when empty. Set manually when the ingress uses a non-standard port or a path prefix. |
| server.debug | bool | `false` | Enable verbose debug logging. |
| server.disableApi | bool | `false` | Disable all `/api/*` routes (index and download still work). |
| server.disableDelete | bool | `false` | Disable the `DELETE /api/charts/{name}/{version}` endpoint. |
| server.enableShutdown | bool | `false` | Enable `POST /shutdown` endpoint. For ephemeral CI environments only — disable for persistent deployments. |
| server.host | string | `"0.0.0.0"` | Listen address. |
| server.https | object | `{"certPasswordKey":"cert-password","certSecretKey":"cert.pfx","certSecretName":"","enabled":false,"port":8443}` | Pod-level HTTPS settings. In most Kubernetes deployments TLS is terminated at the ingress. Enable only when you need end-to-end encryption or are bypassing the ingress entirely. |
| server.https.certPasswordKey | string | `"cert-password"` | Key within `certSecretName` that holds the PFX password. Leave empty if the certificate has no password. |
| server.https.certSecretKey | string | `"cert.pfx"` | Key within `certSecretName` that holds the PFX bytes. |
| server.https.certSecretName | string | `""` | Name of a Kubernetes Secret containing a PFX/PKCS#12 certificate file (mounted at `/certs/` inside the container). |
| server.https.enabled | bool | `false` | Enable HTTPS on the pod. |
| server.https.port | int | `8443` | HTTPS listen port. |
| server.port | int | `8080` | HTTP listen port. |
| server.storageDir | string | `"/charts"` | Path inside the container where `.tgz` chart files are stored. |
| service | object | `{"port":80,"type":"ClusterIP"}` | Kubernetes Service configuration. |
| service.port | int | `80` | Service port. |
| service.type | string | `"ClusterIP"` | Service type. |
| tolerations | list | `[]` | Tolerations for pod scheduling. |

