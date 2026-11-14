CLUSTER        ?= cardinality
CTX            := k3d-$(CLUSTER)
K              := kubectl --context $(CTX)
H              := helm --kube-context $(CTX)
IMAGE          ?= transactions-api:local
KPS_VERSION    ?= 93.3.0
ALLOY_VERSION  ?= 1.15.0
AGENT_POOL_MEM ?= 1280m
RATE           ?= 300
DURATION       ?= 30m
PROM_POD       := prometheus-kps-prometheus-0
APP            := deploy/transactions-api

.PHONY: help prereqs up monitoring agent app all ui load load-stop load-logs explode calm churn \
        unchurn reset-prometheus agent-down agent-up guard-agent unguard-agent fix-app unfix-app \
        cap-app uncap-app status watch down

help: ## Show targets
	@grep -E '^[a-z-]+:.*##' $(MAKEFILE_LIST) | awk -F':.*## ' '{printf "  %-16s %s\n", $$1, $$2}'

prereqs: ## Install k3d and helm with Homebrew
	brew install k3d helm

up: ## Create the cluster: control plane + "app" pool (2 nodes) + tainted "agent" pool (1 node)
	k3d cluster create --config cluster/k3d-config.yaml
	k3d node create agentpool -c $(CLUSTER) --role agent --memory $(AGENT_POOL_MEM) \
	  --k3s-node-label pool=agent --k3s-arg "--node-taint=pool=agent:NoSchedule"
	$(K) wait --for=condition=Ready nodes --all --timeout=180s
	$(K) get nodes -L pool

monitoring: ## Install kube-prometheus-stack (Prometheus w/ remote-write receiver, Grafana) + dashboard
	helm repo add prometheus-community https://prometheus-community.github.io/helm-charts --force-update
	$(H) upgrade --install kps prometheus-community/kube-prometheus-stack \
	  --version $(KPS_VERSION) -n monitoring --create-namespace \
	  -f monitoring/values.yaml --wait --timeout 10m
	$(K) apply -k monitoring/dashboards

agent: ## Install Grafana Alloy on the agent pool (unguarded config)
	$(MAKE) _alloy CONFIG=agent/config.alloy

_alloy:
	helm repo add grafana https://grafana.github.io/helm-charts --force-update
	$(H) upgrade --install alloy grafana/alloy --version $(ALLOY_VERSION) -n alloy --create-namespace \
	  -f agent/values.yaml --set-file alloy.configMap.content=$(CONFIG) --wait --timeout 5m
	$(K) -n alloy rollout restart deploy/alloy
	$(K) -n alloy rollout status deploy/alloy --timeout=120s

app: ## Build the .NET app, import into k3d, deploy to the app pool
	docker build -t $(IMAGE) app
	k3d image import $(IMAGE) -c $(CLUSTER)
	$(K) apply -f k8s/app.yaml
	$(K) -n demo rollout restart $(APP)
	$(K) -n demo rollout status $(APP) --timeout=180s

all: up monitoring agent app ## up + monitoring + agent + app

ui: ## Port-forward Grafana :3000 and Prometheus :9090 (Ctrl-C to stop)
	@echo "Grafana    http://localhost:3000  (admin/admin) -> Dashboards > Cardinality Repro"
	@echo "Prometheus http://localhost:9090"
	@trap 'kill 0' INT TERM; \
	  $(K) -n monitoring port-forward svc/kps-grafana 3000:80 & \
	  while true; do $(K) -n monitoring port-forward svc/kps-prometheus 9090:9090; sleep 3; done & \
	  wait

load: ## Start k6 load (RATE=req/s, default 300; DURATION=30m)
	$(K) -n demo delete job k6-load --ignore-not-found
	sed -e 's/__RATE__/$(RATE)/' -e 's/__DURATION__/$(DURATION)/' k8s/load-k6.yaml | $(K) apply -f -

load-stop: ## Stop k6 load
	$(K) -n demo delete job k6-load --ignore-not-found

load-logs: ## Show k6 output
	$(K) -n demo logs -f job/k6-load

explode: ## THE BUG: add response_time (float) as a label
	$(K) -n demo set env $(APP) CARDINALITY_MODE=unbounded
	$(K) -n demo rollout status $(APP) --timeout=180s

calm: ## Remove the response_time label
	$(K) -n demo set env $(APP) CARDINALITY_MODE=bounded
	$(K) -n demo rollout status $(APP) --timeout=180s

churn: ## Simulate rollouts: ~20% of the 200 pod names change every 60s
	$(K) -n demo set env $(APP) POD_CHURN_SECONDS=60
	$(K) -n demo rollout status $(APP) --timeout=180s

unchurn: ## Stop pod-name churn
	$(K) -n demo set env $(APP) POD_CHURN_SECONDS=0
	$(K) -n demo rollout status $(APP) --timeout=180s

reset-prometheus: ## Delete the Prometheus pod (wipes emptyDir TSDB/WAL) to break a WAL-replay OOM loop
	$(K) -n monitoring delete pod $(PROM_POD)

agent-down: ## Stop Alloy: proves app HTTP transactions don't block on the metrics pipeline
	$(K) -n alloy scale deploy/alloy --replicas=0

agent-up: ## Start Alloy again
	$(K) -n alloy scale deploy/alloy --replicas=1
	$(K) -n alloy rollout status deploy/alloy --timeout=120s

guard-agent: ## Mitigation (agent): memory_limiter + drop series that carry response_time
	$(MAKE) _alloy CONFIG=agent/config-guarded.alloy

unguard-agent: ## Restore the unguarded Alloy config
	$(MAKE) _alloy CONFIG=agent/config.alloy

fix-app: ## Mitigation (app, the real fix): OTel View keeps only bounded tag keys
	$(K) -n demo set env $(APP) OTEL_ALLOWED_TAGS=consume_type,state,product,pod
	$(K) -n demo rollout status $(APP) --timeout=180s

unfix-app: ## Remove the OTel tag allow-list
	$(K) -n demo set env $(APP) OTEL_ALLOWED_TAGS-
	$(K) -n demo rollout status $(APP) --timeout=180s

cap-app: ## Mitigation (app safety net): OTel SDK default cap of 2000 series/metric
	$(K) -n demo set env $(APP) OTEL_CARDINALITY_LIMIT=2000
	$(K) -n demo rollout status $(APP) --timeout=180s

uncap-app: ## Raise the OTel cap back to 200000 (production-like)
	$(K) -n demo set env $(APP) OTEL_CARDINALITY_LIMIT=200000
	$(K) -n demo rollout status $(APP) --timeout=180s

status: ## Pod state, last termination reasons, node pools and usage
	@$(K) get nodes -L pool
	@echo; $(K) get pods -n monitoring -l app.kubernetes.io/name=prometheus -o wide
	@$(K) get pods -n alloy -o wide
	@$(K) get pods -n demo -o wide
	@echo; for ns in monitoring alloy demo; do \
	  $(K) -n $$ns get pods -o jsonpath='{range .items[*]}{range .status.containerStatuses[*]}{.name}{"\t"}restarts={.restartCount}{"\t"}last={.lastState.terminated.reason}{"\n"}{end}{end}' \
	  | grep -E '^(prometheus|alloy|app)\b' | sort | uniq -c; done
	@echo; $(K) top nodes 2>/dev/null || true

watch: ## Watch Prometheus, Alloy and app pods
	$(K) get pods -A -w -l 'app.kubernetes.io/name in (prometheus,alloy)'

down: ## Delete the cluster
	k3d cluster delete $(CLUSTER)
