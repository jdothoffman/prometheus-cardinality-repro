// One image, two roles (APP_ROLE):
//   api     (default) transactions-api replicas under test: handle HTTP transactions, record
//                     OpenTelemetry metrics in memory, push OTLP to Alloy in the background.
//   driver            the load-test console: home page with Start/Stop, ramps the transaction
//                     rate against the api Service, measures latency/timeouts/errors, and polls
//                     each api replica and Prometheus to show where resources run out.
// The driver runs as its own Deployment so generating load never steals CPU from the pods under test.

var builder = WebApplication.CreateBuilder(args);
var isDriver = string.Equals(builder.Configuration["APP_ROLE"], "driver", StringComparison.OrdinalIgnoreCase);

if (isDriver) Driver.Configure(builder); else Api.Configure(builder);

var app = builder.Build();

if (isDriver) Driver.Map(app); else Api.Map(app);

app.Run();
