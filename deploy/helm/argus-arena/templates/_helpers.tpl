{{/* Labels on everything the release makes. */}}
{{- define "arena.labels" -}}
app.kubernetes.io/part-of: argus-arena
app.kubernetes.io/instance: {{ .Release.Name }}
app.kubernetes.io/managed-by: {{ .Release.Service }}
helm.sh/chart: {{ .Chart.Name }}-{{ .Chart.Version }}
{{- end }}

{{/* A service's selector: (dict "root" $ "name" "app"). */}}
{{- define "arena.selector" -}}
app.kubernetes.io/name: {{ .name }}
app.kubernetes.io/instance: {{ .root.Release.Name }}
{{- end }}

{{/* An image: (dict "root" $ "image" .Values.images.app); the tag defaults to the chart's appVersion. */}}
{{- define "arena.image" -}}
{{- if .image.digest -}}
{{ .image.repository }}@{{ .image.digest }}
{{- else -}}
{{ .image.repository }}:{{ .image.tag | default .root.Chart.AppVersion }}
{{- end }}
{{- end }}

{{/* The Secret holding the stack's secrets. */}}
{{- define "arena.secretName" -}}
{{ .Values.secrets.existingSecret | default "arena-secrets" }}
{{- end }}

{{/* An environment variable from the stack's Secret: (list $ "ENV_NAME" "KEY"). */}}
{{- define "arena.secretEnv" -}}
- name: {{ index . 1 }}
  valueFrom: { secretKeyRef: { name: {{ include "arena.secretName" (index . 0) }}, key: {{ index . 2 }} } }
{{- end }}

{{/* The same, for a key the Secret may not have. */}}
{{- define "arena.optionalSecretEnv" -}}
- name: {{ index . 1 }}
  valueFrom: { secretKeyRef: { name: {{ include "arena.secretName" (index . 0) }}, key: {{ index . 2 }}, optional: true } }
{{- end }}

{{/* Where Postgres is: this chart's, or the external one. */}}
{{- define "arena.dbHost" -}}
{{- if .Values.postgresql.enabled }}postgres{{ else }}{{ required "externalDatabase.host is needed when postgresql.enabled is false" .Values.externalDatabase.host }}{{ end }}
{{- end }}
{{- define "arena.dbPort" -}}
{{- if .Values.postgresql.enabled }}5432{{ else }}{{ .Values.externalDatabase.port }}{{ end }}
{{- end }}
{{- define "arena.dbUser" -}}
{{- if .Values.postgresql.enabled }}arena{{ else }}{{ .Values.externalDatabase.user }}{{ end }}
{{- end }}
{{- define "arena.dbSslMode" -}}
{{- if .Values.postgresql.enabled }}prefer{{ else }}{{ .Values.externalDatabase.sslMode }}{{ end }}
{{- end }}

{{/* A volume from a claim of the release: (list "volume-name" "claim-name"). */}}
{{- define "arena.claim" -}}
- name: {{ index . 0 }}
  persistentVolumeClaim: { claimName: {{ index . 1 }} }
{{- end }}

{{/* The model library's claim. */}}
{{- define "arena.modelsClaim" -}}
{{ .Values.persistence.models.existingClaim | default "arena-models" }}
{{- end }}

{{/* Pod settings every pod shares. */}}
{{- define "arena.podDefaults" -}}
enableServiceLinks: false
{{- with .Values.images.pullSecrets }}
imagePullSecrets:
{{- range . }}
  - name: {{ . }}
{{- end }}
{{- end }}
{{- end }}

{{- /* A serving Deployment's availability: its replicas spread over nodes (preferred, or required with
availability.strictSpread), and rolled one new pod at a time with none taken away before it is ready. */}}
{{- define "arena.spread" -}}
affinity:
  podAntiAffinity:
    {{- if .root.Values.availability.strictSpread }}
    requiredDuringSchedulingIgnoredDuringExecution:
      - topologyKey: kubernetes.io/hostname
        labelSelector:
          matchLabels: {{- include "arena.selector" (dict "root" .root "name" .name) | nindent 12 }}
    {{- else }}
    preferredDuringSchedulingIgnoredDuringExecution:
      - weight: 100
        podAffinityTerm:
          topologyKey: kubernetes.io/hostname
          labelSelector:
            matchLabels: {{- include "arena.selector" (dict "root" .root "name" .name) | nindent 14 }}
    {{- end }}
{{- end }}

{{- define "arena.rolling" -}}
strategy: { type: RollingUpdate, rollingUpdate: { maxUnavailable: 0, maxSurge: 1 } }
{{- end }}

