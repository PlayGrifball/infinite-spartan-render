{{- define "infinite-spartan-render.fullname" -}}
{{- if contains .Chart.Name .Release.Name -}}
{{- .Release.Name | trunc 63 | trimSuffix "-" -}}
{{- else -}}
{{- printf "%s-%s" .Release.Name .Chart.Name | trunc 63 | trimSuffix "-" -}}
{{- end -}}
{{- end -}}

{{- define "infinite-spartan-render.selectorLabels" -}}
app.kubernetes.io/name: {{ .Chart.Name }}
app.kubernetes.io/instance: {{ .Release.Name }}
{{- end -}}

{{- define "infinite-spartan-render.labels" -}}
{{ include "infinite-spartan-render.selectorLabels" . }}
helm.sh/chart: {{ printf "%s-%s" .Chart.Name .Chart.Version | replace "+" "_" }}
app.kubernetes.io/version: {{ .Values.image.tag | default .Chart.AppVersion | quote }}
app.kubernetes.io/managed-by: {{ .Release.Service }}
{{- end -}}

{{- define "infinite-spartan-render.image" -}}
{{- $i := .Values.image -}}
{{- if $i.digest -}}
{{ $i.repository }}@{{ $i.digest }}
{{- else -}}
{{ $i.repository }}:{{ $i.tag | default .Chart.AppVersion }}
{{- end -}}
{{- end -}}

{{- define "infinite-spartan-render.claimName" -}}
{{- .Values.persistence.existingClaim | default (printf "%s-data" (include "infinite-spartan-render.fullname" .)) -}}
{{- end -}}

{{/* Nested map -> Section__Key env vars; lists become __0, __1; null values are skipped. */}}
{{- define "infinite-spartan-render.flatten" -}}
{{- $prefix := .prefix -}}
{{- range $k, $v := .value }}
{{- $name := ternary $k (printf "%s__%s" $prefix $k) (eq $prefix "") }}
{{- if kindIs "map" $v }}
{{- include "infinite-spartan-render.flatten" (dict "prefix" $name "value" $v) }}
{{- else if kindIs "slice" $v }}
{{- range $i, $item := $v }}
{{- if kindIs "map" $item }}
{{- include "infinite-spartan-render.flatten" (dict "prefix" (printf "%s__%d" $name $i) "value" $item) }}
{{- else }}
- name: {{ printf "%s__%d" $name $i }}
  value: {{ $item | toString | quote }}
{{- end }}
{{- end }}
{{- else if not (kindIs "invalid" $v) }}
- name: {{ $name }}
  value: {{ $v | toString | quote }}
{{- end }}
{{- end }}
{{- end -}}

{{/*
PromQL label matchers selecting the service's pushed series, for its alerts: prometheusRule.selector, else
k8s_namespace_name="<release namespace>", job=~"(.+/)?<telemetry.serviceName or release name>" (the labels an OpenTelemetry
Collector promotes from the resource attributes the chart sets). The service name's regex metacharacters are quoted as
character classes (a[.]b), not with backslashes.
*/}}
{{- define "infinite-spartan-render.metricsSelector" -}}
{{- $svc := .Values.telemetry.serviceName | default .Release.Name | toString -}}
{{- if .Values.prometheusRule.selector -}}
{{- .Values.prometheusRule.selector -}}
{{- else -}}
{{- if regexMatch "[\\\\^]" $svc }}{{ fail (printf "telemetry.serviceName %s: no ^ or \\ in it, or set prometheusRule.selector" $svc) }}{{ end -}}
{{- printf "k8s_namespace_name=%q, job=~%q" .Release.Namespace (printf "(.+/)?%s" (regexReplaceAll "[.+*?()|{}\\[$]" $svc "[${0}]")) -}}
{{- end -}}
{{- end }}

{{- define "infinite-spartan-render.cacheClaimName" -}}
{{- .Values.cache.existingClaim | default (printf "%s-cache" (include "infinite-spartan-render.fullname" .)) -}}
{{- end -}}
