// One image, two roles (APP_ROLE):
//   api     (default) transactions-api replicas under test: handle HTTP transactions, record
//                     OpenTelemetry metrics in memory, push OTLP to Alloy in the background.
//   driver            the load-test console: home page with Start/Stop, ramps the transaction
//                     rate against the api Service, measures latency/timeouts/errors, and polls
//                     each api replica and Prometheus to show where resources run out.
//   fleet             simulated pods (see Fleet.cs): many independent OTLP senders with production-style
//                     labels, so the agent sees a 300-pod fleet while only 4 real replicas are measured.
// The driver runs as its own Deployment so generating load never steals CPU from the pods under test.

var builder = WebApplication.CreateBuilder(args);
var role = (builder.Configuration["APP_ROLE"] ?? "api").Trim().ToLowerInvariant();

if (role == "driver") Driver.Configure(builder);
else if (role == "fleet") Fleet.Configure(builder);
else Api.Configure(builder);

var app = builder.Build();

if (role == "driver") Driver.Map(app);
else if (role == "fleet") Fleet.Map(app);
else Api.Map(app);

app.Run();
