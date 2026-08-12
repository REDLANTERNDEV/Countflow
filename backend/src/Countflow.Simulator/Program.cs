using System.Globalization;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;

namespace Countflow.Simulator;

/// <summary>
/// The command-line half of the simulator. The interesting half is
/// <see cref="WarehouseSimulator"/>, which takes two signed-in <c>HttpClient</c>s
/// and knows nothing about where they came from — which is what lets the
/// integration tests point it at a Testcontainers-backed API and get the same
/// warehouse this produces against a real one.
/// <para>
/// An explicit <c>Main</c> rather than top-level statements, and deliberately.
/// Top-level statements generate a <c>Program</c> class in the global namespace,
/// and the integration tests resolve the API's host through
/// <c>WebApplicationFactory&lt;Program&gt;</c> — a second one in scope makes that
/// ambiguous. The migrator is kept out of the test project for exactly this reason;
/// the simulator has to be <i>in</i> it, so it gives up its generated entry point
/// instead.
/// </para>
/// </summary>
internal static class SimulatorProgram
{
    private static async Task<int> Main(string[] args)
    {
        var settings = Args.Parse(args);

        if (settings is null)
        {
            Console.WriteLine(Args.Usage);
            return 2;
        }

        Console.WriteLine($"Countflow simulator → {settings.BaseAddress}");

        var supervisor = await SignInAsync(settings.BaseAddress, settings.Supervisor, settings.SupervisorSecret);
        var operatorClient = await SignInAsync(settings.BaseAddress, settings.Operator, settings.OperatorSecret);

        if (supervisor is null || operatorClient is null)
        {
            supervisor?.Dispose();
            operatorClient?.Dispose();

            Console.Error.WriteLine("Could not sign in. Check the credentials and that the API is up.");
            return 1;
        }

        try
        {
            var simulator = new WarehouseSimulator(supervisor, operatorClient, Console.WriteLine);

            var report = await simulator.RunAsync(settings.Plan);

            Console.WriteLine();
            Console.WriteLine(report.ToString());

            if (report.Notes.Count > 0)
            {
                Console.WriteLine();
                Console.WriteLine($"{report.Notes.Count} thing(s) the warehouse refused along the way:");

                foreach (var note in report.Notes.Take(20))
                {
                    Console.WriteLine($"  · {note}");
                }
            }

            return 0;
        }
        finally
        {
            supervisor.Dispose();
            operatorClient.Dispose();
        }
    }

    private static async Task<HttpClient?> SignInAsync(Uri baseAddress, string identifier, string secret)
    {
        var client = new HttpClient { BaseAddress = baseAddress, Timeout = TimeSpan.FromMinutes(5) };

        using var response = await client.PostAsJsonAsync("/api/auth/login", new { identifier, password = secret });

        if (!response.IsSuccessStatusCode)
        {
            client.Dispose();
            return null;
        }

        var body = await response.Content.ReadFromJsonAsync<JsonElement>();

        if (!body.TryGetProperty("accessToken", out var token) || token.GetString() is not { } accessToken)
        {
            client.Dispose();
            return null;
        }

        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);

        return client;
    }

    private sealed record Settings(
        Uri BaseAddress,
        string Supervisor,
        string SupervisorSecret,
        string Operator,
        string OperatorSecret,
        SimulationPlan Plan);

    private static class Args
    {
        public const string Usage = """
            Countflow warehouse simulator — runs a warehouse forwards until it looks like one.

              dotnet run --project src/Countflow.Simulator -- [options]

            Options
              --url <uri>            API base address (default http://localhost:8080)
              --warehouse <code>     Warehouse to work (default WH1)
              --supervisor <user>    Supervisor identifier
              --operator <user>      Operator identifier
              --days <n>             Days to simulate (default 4)
              --seed <n>             Random seed; the same seed builds the same warehouse
              --receipts <n>         Receipts per day (default 5)
              --orders <n>           Orders per day (default 3)
              --counts <n>           Counts raised per day (default 10)
              --keep-quiet-period    Do not lower the counting quiet period for this warehouse

            Secrets come from COUNTFLOW_SUPERVISOR_SECRET and COUNTFLOW_OPERATOR_SECRET,
            never from the command line — an argument is visible in the process table.
            """;

        public static Settings? Parse(string[] args)
        {
            if (args.Contains("--help") || args.Contains("-h"))
            {
                return null;
            }

            var supervisorSecret = Environment.GetEnvironmentVariable("COUNTFLOW_SUPERVISOR_SECRET");
            var operatorSecret = Environment.GetEnvironmentVariable("COUNTFLOW_OPERATOR_SECRET");

            if (string.IsNullOrWhiteSpace(supervisorSecret) || string.IsNullOrWhiteSpace(operatorSecret))
            {
                Console.Error.WriteLine(
                    "Set COUNTFLOW_SUPERVISOR_SECRET and COUNTFLOW_OPERATOR_SECRET before running the simulator.");

                return null;
            }

            var plan = new SimulationPlan(
                WarehouseCode: Value(args, "--warehouse") ?? "WH1",
                Days: Number(args, "--days") ?? 4,
                ReceiptsPerDay: Number(args, "--receipts") ?? 5,
                OrdersPerDay: Number(args, "--orders") ?? 3,
                CountsPerDay: Number(args, "--counts") ?? 10,
                Seed: Number(args, "--seed") ?? 20260812,
                LowerCountQuietPeriod: !args.Contains("--keep-quiet-period"));

            return new Settings(
                new Uri(Value(args, "--url") ?? "http://localhost:8080"),
                Value(args, "--supervisor") ?? "supervisor",
                supervisorSecret,
                Value(args, "--operator") ?? "operator",
                operatorSecret,
                plan);
        }

        private static string? Value(string[] args, string name)
        {
            var index = Array.IndexOf(args, name);

            return index >= 0 && index + 1 < args.Length ? args[index + 1] : null;
        }

        private static int? Number(string[] args, string name) =>
            int.TryParse(Value(args, name), NumberStyles.Integer, CultureInfo.InvariantCulture, out var value)
                ? value
                : null;
    }
}
