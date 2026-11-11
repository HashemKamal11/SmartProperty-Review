using System.Text;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using SmartProperty.Api.Infrastructure.Authentication;
using SmartProperty.Api.Infrastructure.Development;
using SmartProperty.Domain.Marketplace;
using SmartProperty.Persistence.Context;

// SmartProperty Development tools. DEVELOPMENT DATABASES ONLY.
//
// Every command that writes needs --i-understand-this-is-the-development-database and a database on this machine (or the
// compose service name). Nothing here prints a password, a hash, a token or a connection string.

return await Run(args);

static async Task<int> Run(string[] args)
{
    if (args.Length == 0 || args[0] is "-h" or "--help" or "help")
    {
        Console.WriteLine(Help.Text);

        return args.Length == 0 ? 2 : 0;
    }

    var command = args[0];
    var options = Options.Parse(args.Skip(1));

    try
    {
        if (!options.Has("i-understand-this-is-the-development-database"))
        {
            throw new ProvisioningRefusedException(
                "Refused: add --i-understand-this-is-the-development-database. These commands change a database directly and are for development databases only.");
        }

        var connection = ConnectionResolver.Resolve();
        Console.WriteLine($"Database: {connection.Description}");

        await using var context = new ApplicationDbContext(
            new DbContextOptionsBuilder<ApplicationDbContext>().UseNpgsql(connection.ConnectionString).Options);
        var hasher = new PasswordHasher();
        var now = DateTimeOffset.UtcNow;

        switch (command)
        {
            case "reset-admin-password":
            {
                // Interactive only: the new password is typed here, never passed on a command line or in the environment.
                var password = SecretPrompt.Read($"New password for {DevelopmentAdminPassword.AdministratorEmail}", confirm: true);
                await new DevelopmentAdminPassword(context, hasher).ResetAsync(password, now, CancellationToken.None);
                Console.WriteLine($"Done. The password of {DevelopmentAdminPassword.AdministratorEmail} was replaced and its sessions were revoked. No other account was touched.");

                return 0;
            }

            case "provision":
            {
                var password = options.Get("password-env") is { } variable
                    ? SecretPrompt.FromEnvironment(variable)
                    : null;
                var needsReviewer = !await context.Users.AnyAsync(user => user.Email == DevelopmentActorProvisioner.LegalReviewerEmail);
                password ??= needsReviewer ? SecretPrompt.Read($"Password for the new {DevelopmentActorProvisioner.LegalReviewerEmail}", confirm: true) : null;

                var report = await new DevelopmentActorProvisioner(context, hasher).ProvisionAsync(password, now, CancellationToken.None);
                Print(report);

                return 0;
            }

            case "create-demo-user":
            {
                var password = options.Get("password-env") is { } variable
                    ? SecretPrompt.FromEnvironment(variable)
                    : SecretPrompt.Read($"Password for {options.Require("email")}", confirm: true);
                var roles = (options.Get("role") ?? string.Empty).Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                var report = await new DevelopmentActorProvisioner(context, hasher).CreateDemoUserAsync(
                    options.Require("email"),
                    options.Get("first") ?? "Demo",
                    options.Get("last") ?? "User",
                    options.Get("phone") ?? "+201000000123",
                    password,
                    roles,
                    now,
                    CancellationToken.None);
                Print(report);

                return 0;
            }

            case "issue-grants":
            {
                var purposes = options.Require("purposes")
                    .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                    .Select(text => Enum.TryParse<ListingPurpose>(text, ignoreCase: true, out var purpose) && Enum.IsDefined(purpose)
                        ? purpose
                        : throw new ProvisioningRefusedException($"'{text}' is not a purpose. Use Sale, Rent, or both."))
                    .ToArray();
                var days = int.TryParse(options.Get("days"), out var parsed) ? parsed : 365;
                var report = await new DevelopmentGrantProvisioner(context, new ReviewerRelationshipChecker(context)).IssueAsync(
                    Guid.TryParse(options.Require("property"), out var propertyId) ? propertyId : throw new ProvisioningRefusedException("--property must be a GUID."),
                    options.Require("owner"),
                    options.Get("issuer") ?? DevelopmentAdminPassword.AdministratorEmail,
                    options.Get("legal-reviewer") ?? DevelopmentActorProvisioner.LegalReviewerEmail,
                    purposes,
                    days,
                    now,
                    CancellationToken.None);
                Print(report);

                return 0;
            }

            default:
                Console.Error.WriteLine($"Unknown command '{command}'.\n\n{Help.Text}");

                return 2;
        }
    }
    catch (ProvisioningRefusedException exception)
    {
        Console.Error.WriteLine(exception.Message);

        return 1;
    }
    catch (DbUpdateException exception)
    {
        // The inner message can carry values, so only its type and, when PostgreSQL names one, the violated constraint are shown.
        var constraint = (exception.InnerException as Npgsql.PostgresException)?.ConstraintName;
        Console.Error.WriteLine($"The database refused the change ({exception.InnerException?.GetType().Name ?? exception.GetType().Name}{(constraint is null ? string.Empty : $", constraint {constraint}")}). Nothing was saved.");

        return 1;
    }
}

static void Print(ProvisioningReport report)
{
    foreach (var line in report.Lines)
    {
        Console.WriteLine(line);
    }

    Console.WriteLine("Done.");
}


internal sealed class Options
{
    private readonly Dictionary<string, string?> _values = new(StringComparer.OrdinalIgnoreCase);

    public static Options Parse(IEnumerable<string> args)
    {
        var options = new Options();
        string? pending = null;

        foreach (var arg in args)
        {
            if (arg.StartsWith("--", StringComparison.Ordinal))
            {
                if (pending is not null)
                {
                    options._values[pending] = null;
                }

                pending = arg[2..];
            }
            else if (pending is not null)
            {
                options._values[pending] = arg;
                pending = null;
            }
            else
            {
                throw new ProvisioningRefusedException($"Unexpected argument '{arg}'.");
            }
        }

        if (pending is not null)
        {
            options._values[pending] = null;
        }

        return options;
    }

    public bool Has(string name) => _values.ContainsKey(name);

    public string? Get(string name) => _values.GetValueOrDefault(name);

    public string Require(string name) =>
        Get(name) is { Length: > 0 } value ? value : throw new ProvisioningRefusedException($"--{name} is required.");
}

internal static class SecretPrompt
{
    public static string FromEnvironment(string variable)
    {
        var value = Environment.GetEnvironmentVariable(variable);

        return string.IsNullOrEmpty(value)
            ? throw new ProvisioningRefusedException($"The environment variable {variable} is empty or not set.")
            : value;
    }

    /// <summary>Reads a password without echoing it. Refuses when input is redirected, so nothing is piped in by accident.</summary>
    public static string Read(string prompt, bool confirm)
    {
        if (Console.IsInputRedirected)
        {
            throw new ProvisioningRefusedException("This command prompts for a secret and needs an interactive terminal.");
        }

        var first = ReadMasked($"{prompt}: ");

        if (confirm && ReadMasked("Type it again to confirm: ") != first)
        {
            throw new ProvisioningRefusedException("The two entries differ. Nothing was changed.");
        }

        return first;
    }

    private static string ReadMasked(string prompt)
    {
        Console.Write(prompt);
        var buffer = new StringBuilder();

        while (true)
        {
            var key = Console.ReadKey(intercept: true);

            if (key.Key == ConsoleKey.Enter)
            {
                Console.WriteLine();

                return buffer.ToString();
            }

            if (key.Key == ConsoleKey.Backspace)
            {
                if (buffer.Length > 0)
                {
                    buffer.Length--;
                }
            }
            else if (!char.IsControl(key.KeyChar))
            {
                buffer.Append(key.KeyChar);
            }
        }
    }
}

internal sealed record ResolvedConnection(string ConnectionString, string Description);

internal static class ConnectionResolver
{
    private static readonly string[] AllowedHosts = ["localhost", "127.0.0.1", "::1", "postgres"];

    /// <summary>
    /// The connection from <c>ConnectionStrings__Database</c>, or built from the compose <c>.env</c> (the database password
    /// stays in memory). Refuses any host that is not this machine.
    /// </summary>
    public static ResolvedConnection Resolve()
    {
        var configured = Environment.GetEnvironmentVariable("ConnectionStrings__Database");
        var builder = new NpgsqlConnectionStringBuilder();

        if (!string.IsNullOrWhiteSpace(configured))
        {
            builder.ConnectionString = configured;
        }
        else
        {
            var env = ReadDotEnv();
            builder.Host = "localhost";
            builder.Port = int.TryParse(Environment.GetEnvironmentVariable("SMARTPROPERTY_DB_PORT"), out var port) ? port : 5434;
            builder.Database = "smart_property";
            builder.Username = "smart_property";
            builder.Password = env.GetValueOrDefault("POSTGRES_PASSWORD")
                ?? throw new ProvisioningRefusedException("POSTGRES_PASSWORD was not found in .env. Set ConnectionStrings__Database instead.");
        }

        var host = (builder.Host ?? string.Empty).Split(',')[0].Trim();

        if (!AllowedHosts.Contains(host, StringComparer.OrdinalIgnoreCase))
        {
            throw new ProvisioningRefusedException($"Refused: the database host '{host}' is not this machine. These tools are for a local development database only.");
        }

        return new ResolvedConnection(builder.ConnectionString, $"{host}:{builder.Port}/{builder.Database} (credentials not shown)");
    }

    private static Dictionary<string, string> ReadDotEnv()
    {
        var directory = new DirectoryInfo(Directory.GetCurrentDirectory());

        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "docker-compose.yml")))
        {
            directory = directory.Parent;
        }

        var path = directory is null ? null : Path.Combine(directory.FullName, ".env");

        if (path is null || !File.Exists(path))
        {
            throw new ProvisioningRefusedException("No .env next to docker-compose.yml was found. Run from the repository, or set ConnectionStrings__Database.");
        }

        var values = new Dictionary<string, string>(StringComparer.Ordinal);

        foreach (var line in File.ReadAllLines(path))
        {
            var trimmed = line.Trim();

            if (trimmed.Length == 0 || trimmed.StartsWith('#') || !trimmed.Contains('='))
            {
                continue;
            }

            var index = trimmed.IndexOf('=');
            values[trimmed[..index].Trim()] = trimmed[(index + 1)..].Trim();
        }

        return values;
    }
}


internal static class Help
{
    public const string Text = """
        SmartProperty Development tools (development databases only)

        Every command needs:  --i-understand-this-is-the-development-database

          reset-admin-password
              Prompts (hidden) for a new password and replaces it for admin@smartproperty.local only.

          provision [--password-env VAR]
              Creates the Development permissions and roles, gives the administrator marketplace.settings.manage and the
              issuer authority, and creates the Development legal reviewer (prompts for its password, or reads VAR).

          create-demo-user --email E [--first F] [--last L] [--phone +20...] [--role "Platform Admin"] [--password-env VAR]
              Creates an Active demo account, optionally with existing platform roles.

          issue-grants --property GUID --owner EMAIL [--issuer EMAIL] [--legal-reviewer EMAIL] --purposes Sale,Rent [--days 365]
              Records Sale/Rent grants for the real actors through the Domain rules. The legal-review provenance is a
              labelled simulation.

        The database is taken from ConnectionStrings__Database, or built from POSTGRES_PASSWORD in the repository .env
        (localhost:5434 unless SMARTPROPERTY_DB_PORT is set). Credentials are never printed.
        """;
}
