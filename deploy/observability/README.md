# Local telemetry example

Start the ordinary development stack first to create its local configuration:

```bash
bash scripts/dev-up.sh
docker compose -f compose.yaml -f deploy/observability/compose.yaml up -d
python3 scripts/smoke-api.py --demo
```

Open `http://localhost:9090` for Prometheus. Inspect `http_server_request_duration_seconds_count` and the `teammy_workspace_*` instruments. `docker compose -f compose.yaml -f deploy/observability/compose.yaml logs collector` shows basic received trace summaries. No external telemetry service, pager or production datasource is configured.

The collector receives OTLP HTTP only inside the Compose network. Its Prometheus exporter copies resource attributes such as `service.name` into metric labels. The supplied alerts demonstrate fast/slow burn rates for a 99.9% availability objective. Check the actual metric/label translation and provide sustained test traffic before evaluating these rules; there is no uptime claim and no notification integration.

Prometheus data is temporary and retained for at most two hours. The stack is a local inspection tool, not a durable production monitoring deployment. Shutdown requires the same two Compose files.
