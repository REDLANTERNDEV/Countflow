using System.Globalization;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;

namespace Countflow.Simulator;

/// <summary>
/// The command-line half of the simulator. The interesting half is
/// <see cref="WarehouseSimulator"/>, which takes three signed-in <c>HttpClient</c>s
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

        var supervisor = await AuthenticateAsync(settings.BaseAddress, settings.Supervisor);
        var operatorClient = await AuthenticateAsync(settings.BaseAddress, settings.Operator);
        var admin = await AuthenticateAsync(settings.BaseAddress, settings.Admin);

        if (supervisor is null || operatorClient is null || admin is null)
        {
            supervisor?.Dispose();
            operatorClient?.Dispose();
            admin?.Dispose();

            Console.Error.WriteLine("Could not authenticate. Check the credentials and that the API is up.");
            return 1;
        }

        Console.WriteLine(
            $"authenticated as {settings.Supervisor.Describe()}, "
            + $"{settings.Operator.Describe()} and {settings.Admin.Describe()}");

        try
        {
            var simulator = new WarehouseSimulator(supervisor, operatorClient, admin, Console.WriteLine);

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
            admin.Dispose();
        }
    }

    /// <summary>
    /// One of the two ways this API lets a caller prove who they are.
    /// <para>
    /// <b>A machine credential is the default and a password is the fallback</b>,
    /// which is the right way round for a client that is not a person. The
    /// simulator is the only thing in this repository that consumes the API the way
    /// an outside integrator does, so it is the acceptance test for Phase 7's
    /// claim: a service account's key reaches every endpoint a person reaches, is
    /// judged by the same roles, and needs no second vocabulary to describe what it
    /// may do. If the loops below run on a key, they run for an integrator.
    /// </para>
    /// </summary>
    private abstract record Credential(string Role)
    {
        public abstract string Describe();

        public sealed record Key(string Role, string Secret) : Credential(Role)
        {
            // Enough of the key to recognise in a log beside the one the
            // administrator issued, and nowhere near enough to use.
            public override string Describe() =>
                $"{Role} (key {Secret[..Math.Min(Secret.Length, 10)]}…)";
        }

        public sealed record Password(string Role, string Identifier, string Secret) : Credential(Role)
        {
            public override string Describe() => $"{Role} ({Identifier}, password)";
        }
    }

    private static async Task<HttpClient?> AuthenticateAsync(Uri baseAddress, Credential credential)
    {
        var client = new HttpClient { BaseAddress = baseAddress, Timeout = TimeSpan.FromMinutes(5) };

        if (credential is Credential.Key key)
        {
            // No handshake at all: a key is presented on every request rather than
            // exchanged for something shorter-lived. So the smoke test is an
            // explicit one — GET /auth/me is what an integrator runs first, and
            // failing here rather than at the first receipt is worth the round trip.
            client.DefaultRequestHeaders.Add("X-Api-Key", key.Secret);

            using var me = await client.GetAsync($"{WarehouseSimulator.Surface}/auth/me");

            if (me.IsSuccessStatusCode)
            {
                return client;
            }

            Console.Error.WriteLine(
                $"the {credential.Role} API key was refused ({(int)me.StatusCode}). "
                + "Has it been revoked, or does the account hold the wrong role?");

            client.Dispose();
            return null;
        }

        var password = (Credential.Password)credential;

        using var response = await client.PostAsJsonAsync(
            $"{WarehouseSimulator.Surface}/auth/login",
            new { identifier = password.Identifier, password = password.Secret });

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
        Credential Supervisor,
        Credential Operator,
        Credential Admin,
        SimulationPlan Plan);

    private static class Args
    {
        public const string Usage = """
            Countflow warehouse simulator — runs a warehouse forwards until it looks like one.

              dotnet run --project src/Countflow.Simulator -- [options]

            Options
              --url <uri>            API base address (default http://localhost:8080)
              --warehouse <code>     Warehouse to work (default WH1)
              --supervisor <user>    Supervisor identifier, when signing in with a password
              --operator <user>      Operator identifier, when signing in with a password
              --admin <user>         Admin identifier, when signing in with a password
              --days <n>             Days to simulate (default 4)
              --seed <n>             Random seed; the same seed builds the same warehouse
              --receipts <n>         Receipts per day (default 5)
              --orders <n>           Orders per day (default 3)
              --counts <n>           Counts raised per day (default 10)
              --keep-quiet-period    Do not lower the counting quiet period for this warehouse

            Credentials come from the environment, never from the command line — an
            argument is visible in the process table.

              COUNTFLOW_SUPERVISOR_API_KEY   preferred: a service account's key
              COUNTFLOW_OPERATOR_API_KEY
              COUNTFLOW_ADMIN_API_KEY

              COUNTFLOW_SUPERVISOR_SECRET    fallback: a person's password
              COUNTFLOW_OPERATOR_SECRET
              COUNTFLOW_ADMIN_SECRET

            Each role is resolved independently, so the three may be mixed. Three
            credentials rather than one, and that is load-bearing: promoting a rule
            set is admin-only, and a client holding an administrator's authority in
            the supervisor's slot would never discover it. A simulator whose
            credentials are stronger than the ones it claims to use is not testing
            the API an integrator has.
            """;

        public static Settings? Parse(string[] args)
        {
            if (args.Contains("--help") || args.Contains("-h"))
            {
                return null;
            }

            var supervisor = Resolve("SUPERVISOR", Value(args, "--supervisor") ?? "supervisor");
            var operatorCredential = Resolve("OPERATOR", Value(args, "--operator") ?? "operator");
            var admin = Resolve("ADMIN", Value(args, "--admin") ?? "admin");

            if (supervisor is null || operatorCredential is null || admin is null)
            {
                Console.Error.WriteLine(
                    "Set an API key or a password for each of the supervisor, operator and admin roles "
                    + "— COUNTFLOW_<ROLE>_API_KEY or COUNTFLOW_<ROLE>_SECRET.");

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
                supervisor,
                operatorCredential,
                admin,
                plan);
        }

        private static Credential? Resolve(string role, string identifier)
        {
            var key = Environment.GetEnvironmentVariable($"COUNTFLOW_{role}_API_KEY");
            if (!string.IsNullOrWhiteSpace(key))
            {
                return new Credential.Key(role.ToLowerInvariant(), key.Trim());
            }

            var secret = Environment.GetEnvironmentVariable($"COUNTFLOW_{role}_SECRET");

            return string.IsNullOrWhiteSpace(secret)
                ? null
                : new Credential.Password(role.ToLowerInvariant(), identifier, secret);
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
