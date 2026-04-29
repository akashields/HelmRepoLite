{{/*
Expand the name of the chart.
*/}}
{{- define "helmrepolite.name" -}}
{{- default .Chart.Name .Values.nameOverride | trunc 63 | trimSuffix "-" }}
{{- end }}

{{/*
Create a default fully qualified app name.
*/}}
{{- define "helmrepolite.fullname" -}}
{{- if .Values.fullnameOverride }}
{{- .Values.fullnameOverride | trunc 63 | trimSuffix "-" }}
{{- else }}
{{- $name := default .Chart.Name .Values.nameOverride }}
{{- if contains $name .Release.Name }}
{{- .Release.Name | trunc 63 | trimSuffix "-" }}
{{- else }}
{{- printf "%s-%s" .Release.Name $name | trunc 63 | trimSuffix "-" }}
{{- end }}
{{- end }}
{{- end }}

{{/*
Create chart label.
*/}}
{{- define "helmrepolite.chart" -}}
{{- printf "%s-%s" .Chart.Name .Chart.Version | replace "+" "_" | trunc 63 | trimSuffix "-" }}
{{- end }}

{{/*
Common labels.
*/}}
{{- define "helmrepolite.labels" -}}
helm.sh/chart: {{ include "helmrepolite.chart" . }}
{{ include "helmrepolite.selectorLabels" . }}
{{- if .Chart.AppVersion }}
app.kubernetes.io/version: {{ .Chart.AppVersion | quote }}
{{- end }}
app.kubernetes.io/managed-by: {{ .Release.Service }}
{{- end }}

{{/*
Selector labels.
*/}}
{{- define "helmrepolite.selectorLabels" -}}
app.kubernetes.io/name: {{ include "helmrepolite.name" . }}
app.kubernetes.io/instance: {{ .Release.Name }}
{{- end }}

{{/*
Derive the external chart URL:
  1. Use server.chartUrl if explicitly set.
  2. Auto-detect from ingress host + TLS flag.
  3. Empty string if ingress is not enabled (Kestrel auto-detects on startup).
*/}}
{{- define "helmrepolite.chartUrl" -}}
{{- if .Values.server.chartUrl -}}
  {{- .Values.server.chartUrl -}}
{{- else if and .Values.ingress.enabled .Values.ingress.host -}}
  {{- $scheme := ternary "https" "http" .Values.ingress.tls.enabled -}}
  {{- printf "%s://%s" $scheme .Values.ingress.host -}}
{{- end -}}
{{- end }}

{{/*
Name of the Basic-auth Secret.
*/}}
{{- define "helmrepolite.authSecretName" -}}
{{- if .Values.server.auth.existingSecret -}}
  {{- .Values.server.auth.existingSecret -}}
{{- else -}}
  {{- printf "%s-auth" (include "helmrepolite.fullname" .) -}}
{{- end -}}
{{- end }}

{{/*
Validate configuration that cannot be expressed as type constraints.
*/}}
{{- define "helmrepolite.validate" -}}
{{- if and .Values.ingress.enabled (not .Values.ingress.host) -}}
  {{- fail "ingress.host is required when ingress.enabled is true" -}}
{{- end -}}
{{- if and .Values.ingress.tls.enabled (not .Values.ingress.tls.secretName) -}}
  {{- fail "ingress.tls.secretName is required when ingress.tls.enabled is true" -}}
{{- end -}}
{{- if and .Values.ingress.enabled (ne .Values.ingress.path "/") (not .Values.server.chartUrl) -}}
  {{- fail "server.chartUrl must be set explicitly when ingress.path is not '/'. HelmRepoLite does not support URL path prefixes — deploy at the root of a dedicated hostname instead (e.g. helm.example.com)." -}}
{{- end -}}
{{- if and .Values.server.auth.enabled (not .Values.server.auth.existingSecret) (not .Values.server.auth.username) -}}
  {{- fail "server.auth.username is required when server.auth.enabled and no existingSecret is provided" -}}
{{- end -}}
{{- if and .Values.server.auth.enabled (not .Values.server.auth.existingSecret) (not .Values.server.auth.password) -}}
  {{- fail "server.auth.password is required when server.auth.enabled and no existingSecret is provided" -}}
{{- end -}}
{{- if not .Values.image.repository -}}
  {{- fail "image.repository is required" -}}
{{- end -}}
{{- end }}
