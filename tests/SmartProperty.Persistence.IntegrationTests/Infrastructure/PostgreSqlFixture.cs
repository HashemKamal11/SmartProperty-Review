using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using SmartProperty.Persistence.Context;
using Testcontainers.PostgreSql;
using Xunit;

namespace SmartProperty.Persistence.IntegrationTests.Infrastructure;

/// <summary>
/// Owns the one PostgreSQL server this assembly runs against, and hands every test a private database on it.
/// </summary>
/// <remarks>
/// By default Testcontainers owns the server and its cleanup. Explicit external mode instead requires a
/// runner-created container, verified identity, independent run credentials and writable temporary storage.
/// External mode does not construct Testcontainers or its resource reaper; the runner owns container cleanup.
///
/// Sharing the container does not mean sharing data. <see cref="CreateDatabaseAsync"/> gives each test a
/// freshly created database, so one test can never observe another's rows, and test order cannot matter.
///
/// Managed mode retains EnsureCreated; external mode builds a run-owned template using migrations.
/// Each test receives a fresh clone of that template.
/// </remarks>
public sealed class PostgreSqlFixture : IAsyncLifetime
{
    /// <summary>
    /// Matches the major version <c>docker-compose.yml</c> runs for local development, so the model is verified
    /// against the PostgreSQL the project actually targets.
    /// </summary>
    public const string PostgreSqlImage = "postgres:17";

    // The schema-bearing database every per-test database is copied from. Never written to after setup.
    private const string TemplateDatabase = "smartproperty_template";

    private readonly PostgreSqlContainer? container;
    private readonly bool externalMode;
    private ExternalPostgreSqlRun? external;
    private string adminConnectionString = string.Empty;
    private string templateDatabase = TemplateDatabase;

    public PostgreSqlFixture()
    {
        var mode = Environment.GetEnvironmentVariable("SP_MARKETPLACE_PG_MODE");
        if (mode is not null && mode != "external")
            throw new InvalidOperationException("Unknown PostgreSQL test execution mode.");

        externalMode = mode == "external";
        if (!externalMode)
            container = CreateManagedContainer();
    }

    private static PostgreSqlContainer CreateManagedContainer() => new PostgreSqlBuilder(PostgreSqlImage)
        .WithDatabase("smartproperty_admin")
        .WithUsername("smartproperty_test")
        // Test-only credential for a container that exists for the length of one test run and is never
        // published. It is not a secret, and it is not the local development password.
        .WithPassword("smartproperty_test_password")
        .WithCleanUp(true)
        .Build();

    /// <summary>The image tag actually used, for reporting.</summary>
    public string Image => PostgreSqlImage;

    public async Task InitializeAsync()
    {
        if (externalMode)
        {
            var manifest = Environment.GetEnvironmentVariable("SP_MARKETPLACE_PG_MANIFEST");
            if (string.IsNullOrWhiteSpace(manifest) || !Path.IsPathFullyQualified(manifest))
                throw new InvalidOperationException("An absolute run manifest path is required.");

            var token = Environment.GetEnvironmentVariable("SP_MARKETPLACE_PG_RUN_TOKEN");
            var password = Environment.GetEnvironmentVariable("SP_MARKETPLACE_PG_PASSWORD");
            ExternalPostgreSqlRun.ValidateSecrets(token, password);
            external = ExternalPostgreSqlRun.Parse(await File.ReadAllTextAsync(manifest));
            await external.VerifyAsync(token!, password!);
            templateDatabase = "sp_template_" + external.RunId;
            adminConnectionString = new NpgsqlConnectionStringBuilder
            {
                Host = "127.0.0.1", Port = external.Port, Database = "smartproperty_admin",
                Username = "smartproperty_test", Password = password, Pooling = false,
                Timeout = 5, CommandTimeout = 30
            }.ConnectionString;
        }
        else
        {
            await container!.StartAsync();
            adminConnectionString = container.GetConnectionString();
        }

        await ExecuteOnAdminDatabaseAsync($"""CREATE DATABASE "{templateDatabase}";""");

        var templateConnectionString = BuildConnectionString(templateDatabase);

        await using (var context = CreateContext(templateConnectionString))
        {
            if (externalMode)
                await context.Database.MigrateAsync();
            else
                await context.Database.EnsureCreatedAsync();
        }

        // PostgreSQL refuses to use a template that any session is still connected to, and a pooled connection
        // stays open after the context is disposed. Returning the pool to zero is what makes the copy possible.
        NpgsqlConnection.ClearPool(new NpgsqlConnection(templateConnectionString));
    }

    public Task DisposeAsync()
    {
        return container?.DisposeAsync().AsTask() ?? Task.CompletedTask;
    }

    /// <summary>
    /// Creates a private database for one test and returns its connection string.
    /// </summary>
    public async Task<string> CreateDatabaseAsync()
    {
        // Hexadecimal only, so the identifier needs no escaping beyond the quoting already applied.
        var databaseName = external is null
            ? $"sp_test_{Guid.NewGuid():n}"
            : $"sp_{external.RunId[..12]}_{Guid.NewGuid():n}";

        await ExecuteOnAdminDatabaseAsync($"""CREATE DATABASE "{databaseName}" TEMPLATE "{templateDatabase}";""");

        return BuildConnectionString(databaseName);
    }

    /// <summary>
    /// Returns a connection string for a database that does not exist on this container. Used to observe how an
    /// infrastructure failure surfaces, without stopping or removing anything.
    /// </summary>
    public string BuildMissingDatabaseConnectionString()
    {
        return BuildConnectionString($"sp_absent_{Guid.NewGuid():n}");
    }

    /// <summary>
    /// A standalone context, outside any service provider. Seeding and verification use these so that the
    /// interceptors a test installs on its own contexts observe only the operation under test.
    /// </summary>
    public static ApplicationDbContext CreateContext(string connectionString)
    {
        var options = new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseNpgsql(connectionString)
            .Options;

        return new ApplicationDbContext(options);
    }

    private string BuildConnectionString(string databaseName)
    {
        // External host and port have been checked against the exact runner-owned container.
        return new NpgsqlConnectionStringBuilder(externalMode ? adminConnectionString : container!.GetConnectionString())
        {
            Database = databaseName,
            MaxPoolSize = 8
        }.ConnectionString;
    }

    private async Task ExecuteOnAdminDatabaseAsync(string sql)
    {
        if (external is not null)
        {
            var token = Environment.GetEnvironmentVariable("SP_MARKETPLACE_PG_RUN_TOKEN");
            var password = Environment.GetEnvironmentVariable("SP_MARKETPLACE_PG_PASSWORD");
            ExternalPostgreSqlRun.ValidateSecrets(token, password);
            await external.VerifyAsync(token!, password!);
        }

        await using var connection = new NpgsqlConnection(adminConnectionString);
        await connection.OpenAsync();

        await using var command = new NpgsqlCommand(sql, connection);
        await command.ExecuteNonQueryAsync();
    }
}

/// <summary>
/// The single collection every test class joins, so one container serves the whole assembly.
/// </summary>
[CollectionDefinition(Name)]
public sealed class PostgreSqlCollection : ICollectionFixture<PostgreSqlFixture>
{
    public const string Name = "PostgreSQL";
}

// The manifest is not ownership evidence on its own. Live inspection must also match independently
// supplied per-run secrets. A caller controlling the Docker daemon/process environment is outside this
// accidental-resource-isolation boundary. The runner must create, rather than adopt, the container.
internal sealed record ExternalPostgreSqlRun(string RunId, string ContainerId, string Image, string Name, int Port)
{
    internal const string ExpectedImage = "sha256:67f41722b7a8cbdb868a44a4995c846eddfdc2973bccb291ce937dce88ad5675";
    internal const string ExpectedDataDirectory = "/var/lib/postgresql/data";

    internal static void ValidateSecrets(string? token, string? password)
    {
        if (token is null || password is null || !Regex.IsMatch(token, "\\A[0-9a-f]{64}\\z") ||
            !Regex.IsMatch(password, "\\A[0-9a-f]{64}\\z") || token == password)
            throw new InvalidOperationException("Independent per-run credentials are required.");
    }

    internal static ExternalPostgreSqlRun Parse(string json)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            var run = new ExternalPostgreSqlRun(
                Required(root, "RunId", JsonValueKind.String).GetString()!,
                Required(root, "ContainerId", JsonValueKind.String).GetString()!,
                Required(root, "Image", JsonValueKind.String).GetString()!,
                Required(root, "Name", JsonValueKind.String).GetString()!,
                Required(root, "Port", JsonValueKind.Number).TryGetInt32(out var port) ? port : 0);
            if (!Regex.IsMatch(run.RunId, "\\A[0-9a-f]{32}\\z") ||
                !Regex.IsMatch(run.ContainerId, "\\A[0-9a-f]{64}\\z") || run.Image != ExpectedImage ||
                run.Name != "sp-marketplace-pg-" + run.RunId || run.Port is < 1 or > 65535)
                throw new InvalidOperationException("Invalid external PostgreSQL run identity.");
            return run;
        }
        catch (JsonException)
        {
            throw new InvalidOperationException("Invalid external PostgreSQL run manifest.");
        }
    }

    private static JsonElement Required(JsonElement parent, string name, JsonValueKind kind)
    {
        if (parent.ValueKind != JsonValueKind.Object || !parent.TryGetProperty(name, out var value) || value.ValueKind != kind)
            throw new InvalidOperationException("Incomplete external PostgreSQL verification metadata.");
        return value;
    }

    internal void VerifyInspection(JsonElement root, string token, string password)
    {
        ValidateSecrets(token, password);
        var config = Required(root, "Config", JsonValueKind.Object);
        var labels = Required(config, "Labels", JsonValueKind.Object);
        var host = Required(root, "HostConfig", JsonValueKind.Object);
        var ports = Required(Required(root, "NetworkSettings", JsonValueKind.Object), "Ports", JsonValueKind.Object);
        var bindings = Required(ports, "5432/tcp", JsonValueKind.Array);
        var mounts = Required(root, "Mounts", JsonValueKind.Array);
        var environment = Required(config, "Env", JsonValueKind.Array).EnumerateArray().ToArray();
        if (environment.Any(value => value.ValueKind != JsonValueKind.String))
            throw new InvalidOperationException("Invalid container environment metadata.");

        bool HasExactly(string key, string value)
        {
            var matches = environment.Where(item => item.GetString()!.StartsWith(key + "=", StringComparison.Ordinal)).ToArray();
            return matches.Length == 1 && matches[0].GetString() == key + "=" + value;
        }

        // Inspection rejects contradictions; only VerifyStorage establishes effective storage.
        var dataMountCount = 0;
        foreach (var mount in mounts.EnumerateArray())
        {
            var type = Required(mount, "Type", JsonValueKind.String).GetString();
            var destination = Required(mount, "Destination", JsonValueKind.String).GetString()!;
            if (type != "tmpfs" || destination != ExpectedDataDirectory)
                throw new InvalidOperationException("Unsafe external PostgreSQL storage.");
            if (destination == ExpectedDataDirectory)
            {
                Required(mount, "RW", JsonValueKind.True);
                dataMountCount++;
            }
        }

        if (dataMountCount > 1 || !HasExactly("PGDATA", ExpectedDataDirectory))
            throw new InvalidOperationException("Effective PostgreSQL temporary storage is not verified.");

        if (host.TryGetProperty("Binds", out var binds) && binds.ValueKind != JsonValueKind.Null &&
            (binds.ValueKind != JsonValueKind.Array || binds.GetArrayLength() != 0))
            throw new InvalidOperationException("Bind mounts are forbidden.");

        var tmpfs = Required(host, "Tmpfs", JsonValueKind.Object);
        if (tmpfs.EnumerateObject().Count() != 1 ||
            Required(tmpfs, ExpectedDataDirectory, JsonValueKind.String).GetString() != "rw" ||
            (host.TryGetProperty("Mounts", out var requests) && requests.ValueKind != JsonValueKind.Null &&
             (requests.ValueKind != JsonValueKind.Array || requests.GetArrayLength() != 0)))
            throw new InvalidOperationException("Unexpected mount configuration.");

        if (Required(root, "Id", JsonValueKind.String).GetString() != ContainerId ||
            Required(root, "Image", JsonValueKind.String).GetString() != Image ||
            Required(root, "Name", JsonValueKind.String).GetString() != "/" + Name ||
            Required(labels, "smartproperty.validation.run", JsonValueKind.String).GetString() != RunId ||
            ports.EnumerateObject().Count() != 1 || bindings.GetArrayLength() != 1 ||
            !HasExactly("SP_MARKETPLACE_PG_RUN_TOKEN", token) || !HasExactly("POSTGRES_PASSWORD", password) ||
            !HasExactly("POSTGRES_USER", "smartproperty_test") || !HasExactly("POSTGRES_DB", "smartproperty_admin"))
            throw new InvalidOperationException("External PostgreSQL ownership or endpoint verification failed.");

        Required(Required(root, "State", JsonValueKind.Object), "Running", JsonValueKind.True);
        Required(host, "Privileged", JsonValueKind.False);
        var binding = bindings[0];
        if (Required(binding, "HostIp", JsonValueKind.String).GetString() != "127.0.0.1" ||
            Required(binding, "HostPort", JsonValueKind.String).GetString() != Port.ToString(System.Globalization.CultureInfo.InvariantCulture))
            throw new InvalidOperationException("External PostgreSQL port is not the verified loopback endpoint.");
    }

    internal static void VerifyStorage(string canonical, string fileSystem, string initMounts, string selfMounts)
    {
        if (canonical.TrimEnd('\r', '\n') != ExpectedDataDirectory || fileSystem.TrimEnd('\r', '\n') != "tmpfs")
            throw new InvalidOperationException("Unexpected effective data path or filesystem.");
        if (ParseMounts(initMounts) != ParseMounts(selfMounts))
            throw new InvalidOperationException("Conflicting mount namespaces.");
    }

    private static string DecodeMountPath(string value)
    {
        if (Regex.IsMatch(value, @"\\(?!040|011|012|134)"))
            throw new InvalidOperationException("Malformed mount path escape.");
        return Regex.Replace(value, @"\\(040|011|012|134)", match =>
            ((char)Convert.ToInt32(match.Groups[1].Value, 8)).ToString());
    }

    private static string ParseMounts(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
            throw new InvalidOperationException("Missing mount namespace evidence.");
        string? target = null;
        var ids = new HashSet<int>();
        foreach (var line in text.TrimEnd('\n').Split('\n'))
        {
            var fields = line.Split(' ');
            var separator = Array.IndexOf(fields, "-");
            if (fields.Any(string.IsNullOrEmpty) || separator < 6 || fields.Length != separator + 4 ||
                !int.TryParse(fields[0], out var id) || id <= 0 || !ids.Add(id) ||
                !int.TryParse(fields[1], out var parent) || parent < 0 ||
                !Regex.IsMatch(fields[2], @"\A[0-9]+:[0-9]+\z"))
                throw new InvalidOperationException("Malformed mount namespace evidence.");
            var root = DecodeMountPath(fields[3]);
            var path = DecodeMountPath(fields[4]);
            if (!root.StartsWith('/') || !path.StartsWith('/'))
                throw new InvalidOperationException("Nonabsolute mount path.");
            if (path.StartsWith(ExpectedDataDirectory + "/", StringComparison.Ordinal))
                throw new InvalidOperationException("Nested data mount rejected.");
            if (path != ExpectedDataDirectory) continue;
            var options = fields[5].Split(',');
            var superOptions = fields[separator + 3].Split(',');
            if (target is not null || root != "/" || fields[separator + 1] != "tmpfs" ||
                options.Count(value => value == "rw") != 1 || options.Contains("ro") ||
                superOptions.Count(value => value == "rw") != 1 || superOptions.Contains("ro"))
                throw new InvalidOperationException("Data mount is not an unambiguous writable tmpfs.");
            target = line;
        }
        return target ?? throw new InvalidOperationException("Missing effective data mount.");
    }

    internal static void VerifyStableInspection(JsonElement before, JsonElement after)
    {
        foreach (var key in new[] { "Id", "Created" })
            if (Required(before, key, JsonValueKind.String).GetString() != Required(after, key, JsonValueKind.String).GetString())
                throw new InvalidOperationException("Container identity changed during probes.");
        var first = Required(before, "State", JsonValueKind.Object);
        var last = Required(after, "State", JsonValueKind.Object);
        if (Required(first, "StartedAt", JsonValueKind.String).GetString() != Required(last, "StartedAt", JsonValueKind.String).GetString() ||
            Required(first, "Pid", JsonValueKind.Number).GetInt64() <= 0 ||
            Required(first, "Pid", JsonValueKind.Number).GetInt64() != Required(last, "Pid", JsonValueKind.Number).GetInt64())
            throw new InvalidOperationException("Container restarted during probes.");
    }

    internal Task VerifyAsync(string token, string password) => VerifyAsync(token, password, DockerReadAsync);

    internal async Task VerifyAsync(string token, string password, Func<string[], Task<string>> read)
    {
        try
        {
            var inspect = new[] { "inspect", "--type", "container", ContainerId };
            using var before = JsonDocument.Parse(await read(inspect));
            if (before.RootElement.ValueKind != JsonValueKind.Array || before.RootElement.GetArrayLength() != 1)
                throw new InvalidOperationException("Expected exactly one container.");
            VerifyInspection(before.RootElement[0], token, password);
            var canonical = await read(["exec", ContainerId, "readlink", "-e", ExpectedDataDirectory]);
            var init = await read(["exec", ContainerId, "cat", "/proc/1/mountinfo"]);
            var self = await read(["exec", ContainerId, "cat", "/proc/self/mountinfo"]);
            var filesystem = await read(["exec", ContainerId, "stat", "-f", "-c", "%T", ExpectedDataDirectory]);
            VerifyStorage(canonical, filesystem, init, self);
            using var after = JsonDocument.Parse(await read(inspect));
            if (after.RootElement.ValueKind != JsonValueKind.Array || after.RootElement.GetArrayLength() != 1)
                throw new InvalidOperationException("Expected exactly one container.");
            VerifyInspection(after.RootElement[0], token, password);
            VerifyStableInspection(before.RootElement[0], after.RootElement[0]);
        }
        catch (Exception exception) when (exception is JsonException or OperationCanceledException or TimeoutException)
        {
            throw new InvalidOperationException("External storage verification failed or timed out.");
        }
    }

    private static async Task<string> DockerReadAsync(string[] arguments)
    {
        var start = new ProcessStartInfo("docker")
        {
            UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true,
            CreateNoWindow = true
        };
        foreach (var argument in new[] { "--context", "desktop-linux" }.Concat(arguments))
            start.ArgumentList.Add(argument);
        using var process = Process.Start(start) ?? throw new InvalidOperationException("Docker inspection could not start.");
        var output = process.StandardOutput.ReadToEndAsync();
        var error = process.StandardError.ReadToEndAsync();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        try
        {
            await process.WaitForExitAsync(timeout.Token);
        }
        catch (OperationCanceledException)
        {
            try { process.Kill(); } catch (InvalidOperationException) { }
            throw new InvalidOperationException("Docker verification probe timed out.");
        }
        var json = await output;
        _ = await error; // Inspection contains credentials: never log either stream.
        if (process.ExitCode != 0)
            throw new InvalidOperationException("Docker ownership inspection failed.");
        return json;
    }
}

// No collection/fixture association: these tests do not call Docker or open database connections.
public sealed class ExternalPostgreSqlSafetyTests
{
    private static readonly string Token = new('c', 64);
    private static readonly string Password = new('d', 64);

    [Theory]
    [InlineData("{}")]
    [InlineData("null")]
    [InlineData("{\"RunId\":\"shared\"}")]
    public void InvalidManifestIsRejected(string json)
    {
        Assert.Throws<InvalidOperationException>(() => ExternalPostgreSqlRun.Parse(json));
    }

    [Fact]
    public void ActualWritableTmpfsAndExactIdentityAreAccepted()
    {
        var run = ValidRun();
        using var document = JsonDocument.Parse(Inspection(run));
        run.VerifyInspection(document.RootElement, Token, Password);
    }

    [Theory]
    [InlineData("duplicate-runtime-tmpfs")]
    [InlineData("readonly-tmpfs")]
    [InlineData("wrong-tmpfs-destination")]
    [InlineData("missing-pgdata")]
    [InlineData("wrong-pgdata")]
    [InlineData("duplicate-pgdata")]
    [InlineData("wrong-image")]
    [InlineData("wrong-token")]
    [InlineData("wrong-password")]
    [InlineData("bind")]
    [InlineData("named-volume")]
    [InlineData("anonymous-volume")]
    [InlineData("nested-mount")]
    [InlineData("wrong-owner")]
    [InlineData("public-port")]
    [InlineData("wrong-port")]
    [InlineData("stopped")]
    public void UnsafeInspectionIsRejected(string scenario)
    {
        var run = ValidRun();
        var root = JsonNode.Parse(Inspection(run))!;
        var environment = root["Config"]!["Env"]!.AsArray();
        var mount = root["Mounts"]![0]!;
        var port = root["NetworkSettings"]!["Ports"]!["5432/tcp"]![0]!;
        switch (scenario)
        {
            case "duplicate-runtime-tmpfs": root["Mounts"]!.AsArray().Add(mount.DeepClone()); break;
            case "readonly-tmpfs": mount["RW"] = false; break;
            case "wrong-tmpfs-destination": mount["Destination"] = "/unrelated"; break;
            case "missing-pgdata": environment.RemoveAt(4); break;
            case "wrong-pgdata": environment[4] = "PGDATA=/persistent"; break;
            case "duplicate-pgdata": environment.Add("PGDATA=" + ExternalPostgreSqlRun.ExpectedDataDirectory); break;
            case "wrong-image": root["Image"] = "sha256:" + new string('e', 64); break;
            case "wrong-token": environment[0] = "SP_MARKETPLACE_PG_RUN_TOKEN=" + new string('e', 64); break;
            case "wrong-password": environment[1] = "POSTGRES_PASSWORD=" + new string('e', 64); break;
            case "bind": mount["Type"] = "bind"; mount["Source"] = "/host/data"; break;
            case "named-volume": mount["Type"] = "volume"; mount["Name"] = "shared"; break;
            case "anonymous-volume": mount["Type"] = "volume"; mount["Name"] = new string('e', 64); break;
            case "nested-mount":
                root["Mounts"]!.AsArray().Add(new JsonObject
                {
                    ["Type"] = "tmpfs", ["Destination"] = ExternalPostgreSqlRun.ExpectedDataDirectory + "/nested", ["RW"] = true
                });
                break;
            case "wrong-owner": root["Config"]!["Labels"]!["smartproperty.validation.run"] = new string('e', 32); break;
            case "public-port": port["HostIp"] = "0.0.0.0"; break;
            case "wrong-port": port["HostPort"] = "54322"; break;
            case "stopped": root["State"]!["Running"] = false; break;
            default: throw new ArgumentOutOfRangeException(nameof(scenario));
        }

        // Parse/setup errors occur outside the assertion and therefore cannot pass the safety test.
        using var document = JsonDocument.Parse(root.ToJsonString());
        Assert.Throws<InvalidOperationException>(() => run.VerifyInspection(document.RootElement, Token, Password));
    }

    [Fact]
    public void ManifestCannotSubstituteForIndependentRunToken()
    {
        var run = ValidRun();
        using var document = JsonDocument.Parse(Inspection(run));
        Assert.Throws<InvalidOperationException>(() => run.VerifyInspection(document.RootElement, new string('e', 64), Password));
    }

    [Fact]
    public void ManifestCannotAdoptAnotherContainerIdentity()
    {
        var run = ValidRun();
        using var document = JsonDocument.Parse(Inspection(run));
        var other = run with { ContainerId = new string('e', 64) };
        Assert.Throws<InvalidOperationException>(() => other.VerifyInspection(document.RootElement, Token, Password));
    }

    private static ExternalPostgreSqlRun ValidRun() => new(
        new string('a', 32), new string('b', 64), ExternalPostgreSqlRun.ExpectedImage,
        "sp-marketplace-pg-" + new string('a', 32), 54321);

    private static string Inspection(ExternalPostgreSqlRun run) => JsonSerializer.Serialize(new
    {
        Id = run.ContainerId, run.Image, Name = "/" + run.Name, Created = "2026-10-27T12:00:00Z",
        State = new { Running = true, StartedAt = "2026-10-27T12:00:01Z", Pid = 123 },
        Config = new
        {
            Labels = new Dictionary<string, string> { ["smartproperty.validation.run"] = run.RunId },
            Env = new[] { "SP_MARKETPLACE_PG_RUN_TOKEN=" + Token, "POSTGRES_PASSWORD=" + Password,
                "POSTGRES_USER=smartproperty_test", "POSTGRES_DB=smartproperty_admin",
                "PGDATA=" + ExternalPostgreSqlRun.ExpectedDataDirectory }
        },
        HostConfig = new
        {
            Privileged = false,
            Tmpfs = new Dictionary<string, string> { [ExternalPostgreSqlRun.ExpectedDataDirectory] = "rw" }
        },
        NetworkSettings = new
        {
            Ports = new Dictionary<string, object>
            {
                ["5432/tcp"] = new[] { new { HostIp = "127.0.0.1", HostPort = "54321" } }
            }
        },
        Mounts = new[] { new { Type = "tmpfs", Destination = ExternalPostgreSqlRun.ExpectedDataDirectory, RW = true } }
    });

    private const string MountEvidence = "20 1 0:21 / /var/lib/postgresql/data rw,nosuid - tmpfs tmpfs rw,size=65536k\n";

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CompleteIndependentEvidenceIsRequiredEvenWithEmptyDockerMounts(bool empty)
    {
        var run = ValidRun();
        var root = JsonNode.Parse(Inspection(run))!;
        if (empty) root["Mounts"] = new JsonArray();
        var calls = new List<string>();
        await run.VerifyAsync(Token, Password, arguments =>
        {
            calls.Add(string.Join(' ', arguments));
            Assert.Equal(run.ContainerId, arguments[0] == "inspect" ? arguments[3] : arguments[1]);
            return Task.FromResult(arguments[0] == "inspect" ? "[" + root.ToJsonString() + "]" :
                arguments[2] == "readlink" ? ExternalPostgreSqlRun.ExpectedDataDirectory + "\n" :
                arguments[2] == "stat" ? "tmpfs\n" : MountEvidence);
        });
        Assert.Equal(6, calls.Count);
    }

    [Fact]
    public void EscapedUnrelatedMountPathsAreParsedStructurally()
    {
        var text = "10 1 0:10 / /path\\040with\\134escape rw - tmpfs tmpfs rw\n" + MountEvidence;
        ExternalPostgreSqlRun.VerifyStorage(ExternalPostgreSqlRun.ExpectedDataDirectory, "tmpfs", text, text);
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("truncated")]
    [InlineData("escape")]
    [InlineData("overlay")]
    [InlineData("ext4")]
    [InlineData("readonly")]
    [InlineData("readonly-super")]
    [InlineData("nested")]
    [InlineData("stacked")]
    [InlineData("conflicting")]
    [InlineData("symlink")]
    [InlineData("stat")]
    public void UnsafeFilesystemEvidenceIsRejected(string scenario)
    {
        var path = ExternalPostgreSqlRun.ExpectedDataDirectory;
        var fs = "tmpfs";
        var first = MountEvidence;
        var second = MountEvidence;
        switch (scenario)
        {
            case "missing": first = ""; break;
            case "truncated": first = "20 1 0:21 /"; break;
            case "escape": first = first.Replace("/var", @"/bad\777/var", StringComparison.Ordinal); break;
            case "overlay": first = first.Replace("- tmpfs", "- overlay", StringComparison.Ordinal); break;
            case "ext4": first = first.Replace("- tmpfs", "- ext4", StringComparison.Ordinal); break;
            case "readonly": first = first.Replace("rw,nosuid", "ro,nosuid", StringComparison.Ordinal); break;
            case "readonly-super": first = first.Replace("rw,size", "ro,size", StringComparison.Ordinal); break;
            case "nested": first += "21 20 0:22 / /var/lib/postgresql/data/child rw - tmpfs tmpfs rw\n"; break;
            case "stacked": first += first.Replace("20 1", "21 20", StringComparison.Ordinal); break;
            case "conflicting": second = second.Replace("0:21", "0:22", StringComparison.Ordinal); break;
            case "symlink": path = "/persistent"; break;
            case "stat": fs = "overlayfs"; break;
            default: throw new ArgumentOutOfRangeException(nameof(scenario));
        }
        Assert.Throws<InvalidOperationException>(() => ExternalPostgreSqlRun.VerifyStorage(path, fs, first, second));
    }

    [Theory]
    [InlineData("Id")]
    [InlineData("Created")]
    [InlineData("StartedAt")]
    [InlineData("Pid")]
    public void ChangedIdentityOrRestartDuringProbesIsRejected(string key)
    {
        var json = Inspection(ValidRun());
        var changed = JsonNode.Parse(json)!;
        if (key == "Pid") changed["State"]![key] = 456;
        else if (key == "StartedAt") changed["State"]![key] = "2026-10-27T13:00:00Z";
        else changed[key] = "changed";
        using var before = JsonDocument.Parse(json);
        using var after = JsonDocument.Parse(changed.ToJsonString());
        Assert.Throws<InvalidOperationException>(() => ExternalPostgreSqlRun.VerifyStableInspection(before.RootElement, after.RootElement));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ProbeFailureOrTimeoutPreventsAcceptance(bool timeout)
    {
        var run = ValidRun();
        var calls = 0;
        await Assert.ThrowsAsync<InvalidOperationException>(() => run.VerifyAsync(Token, Password, arguments =>
        {
            calls++;
            if (arguments[0] == "inspect") return Task.FromResult("[" + Inspection(run) + "]");
            return Task.FromException<string>(timeout ? new TimeoutException() : new InvalidOperationException("Probe failed."));
        }));
        Assert.Equal(2, calls);
    }
}
