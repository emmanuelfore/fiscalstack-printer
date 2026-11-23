using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Forms;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Hosting.WindowsServices;
using PrintInterceptor.Core.Capture;
using PrintInterceptor.Core.Configuration;
using PrintInterceptor.Core.Decoding;
using PrintInterceptor.Core.Diagnostics;
using PrintInterceptor.Core.Fiscalisation;
using PrintInterceptor.Core.Offline;
using PrintInterceptor.Core.Pipeline;
using PrintInterceptor.Core.Printing;
using PrintInterceptor.Core.Reporting;
using PrintInterceptor.Core.State;
using PrintInterceptor.Core.Storage;

namespace PrintInterceptor;

public static class Program
{
    [STAThread]
    public static int Main(string[] args)
    {
        var registerKey = args.Contains("--register-key", StringComparer.OrdinalIgnoreCase);
        var clearKey = args.Contains("--clear-key", StringComparer.OrdinalIgnoreCase);
        var explicitConsole = args.Contains("--console", StringComparer.OrdinalIgnoreCase);
        var explicitTray = args.Contains("--tray", StringComparer.OrdinalIgnoreCase);
        var explicitService = args.Contains("--service", StringComparer.OrdinalIgnoreCase);
        var configureOnly = args.Contains("--configure-only", StringComparer.OrdinalIgnoreCase);
        var statusRequested = args.Contains("--status", StringComparer.OrdinalIgnoreCase);
        var dayReportRequested = DayReportRequested(args);
        var asWindowsService = WindowsServiceHelpers.IsWindowsService();
        var initialSetup = args.Contains("--setup", StringComparer.OrdinalIgnoreCase);
        var configPath = ResolveConfigPath(args);

        // A console window is only allocated for the flows that need one, so a
        // double-click on the exe or the post-install launch shows no terminal.
        var needsConsole =
            !asWindowsService &&
            (registerKey || clearKey || explicitConsole || statusRequested || dayReportRequested ||
             (explicitService && !configureOnly));
        if (needsConsole && OperatingSystem.IsWindows())
        {
            AttachParentConsoleOrAllocate();
        }

        // The tray is the default UI when nothing else was asked for. The Windows
        // service host and the key-management flows never open a tray.
        var trayMode =
            !asWindowsService &&
            !configureOnly &&
            !explicitService &&
            !registerKey &&
            !clearKey &&
            !statusRequested &&
            !dayReportRequested &&
            (explicitTray || !explicitConsole);
        var consoleMode =
            !asWindowsService &&
            !configureOnly &&
            !trayMode &&
            (explicitConsole || statusRequested || needsConsole);

        var configExisted = File.Exists(configPath);

        InterceptorConfig config;
        try
        {
            config = InterceptorConfig.LoadOrCreate(configPath);
        }
        catch (Exception ex)
        {
            WriteStartupError(ex, configPath, trayMode);
            return 1;
        }

        // Read-only automation probe. It must not need an API key, open a tray,
        // start the listener or write a log line: the updater polls it from a
        // SYSTEM scheduled task and relies on stdout being pure JSON.
        if (statusRequested)
        {
            return PrintStatus(config, args);
        }

        var log = new FileLog(Path.Combine(config.ResolveDataDirectory(), "logs"), consoleMode || trayMode);

        if (!configExisted)
        {
            log.Warn($"Created a default configuration at {configPath}. Register the API key with --register-key (or Settings) and set the printing options, then restart.");
        }

        var secrets = new SecretStore(config.ResolveSecretDirectory(), log);

        if (registerKey)
        {
            return RegisterKey(config, secrets, log);
        }

        if (clearKey)
        {
            return ClearKey(config, secrets, log);
        }

        if (dayReportRequested)
        {
            // Owns stdout, so the report can be redirected or piped: the
            // diagnostic lines it writes go to the log file only.
            using var reportLog = new FileLog(Path.Combine(config.ResolveDataDirectory(), "logs"), false);
            return PrintDayReport(config, args, reportLog);
        }

        var resolution = ApiKeyResolver.Resolve(
            config,
            secrets,
            Environment.GetEnvironmentVariable(ApiKeyResolver.EnvironmentVariable));
        config.ApiKey = resolution.Key ?? "";

        if (!resolution.Found)
        {
            log.Warn("No API key is configured. Register it with --register-key (or open Settings in the tray); " +
                     "jobs will be held for review until then.");
        }
        else
        {
            log.Info($"API key source: {DescribeResolution(resolution)}");
            if (resolution.Origin == ApiKeyOrigin.ConfigurationFile)
            {
                MigrateLegacyKey(config, secrets, log, resolution);
            }
        }

        var validationErrors = config.Validate();
        if (validationErrors.Count > 0)
        {
            foreach (var error in validationErrors)
            {
                log.Error($"Configuration error: {error}");
            }

            if (trayMode || configureOnly)
            {
                MessageBox.Show(
                    string.Join(Environment.NewLine, validationErrors),
                    "PrintInterceptor configuration",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Error);
            }

            return 1;
        }

        var modeLabel = configureOnly
            ? "configure-only"
            : trayMode
                ? "tray"
                : asWindowsService
                    ? "service"
                    : "service/console";
        log.Info($"PrintInterceptor starting. Config={configPath} Data={config.ResolveDataDirectory()} Mode={modeLabel}");
        log.Info(
            $"Capture {config.ListenAddress}:{config.ListenPort}; API {config.ApiEndpoint}; " +
            $"printing={(config.Printing.Enabled ? config.Printing.PrinterName is { Length: > 0 } name ? name : "default printer" : "disabled")}, " +
            $"format={config.Printing.Format}, A4 printer={(string.IsNullOrWhiteSpace(config.Printing.A4PrinterName) ? "(fallback)" : config.Printing.A4PrinterName)}.");

        var builder = Host.CreateDefaultBuilder(args);

        if (!consoleMode && !trayMode && !configureOnly)
        {
            builder.UseWindowsService(options => options.ServiceName = "PrintInterceptor");
        }

        builder.ConfigureServices(services =>
        {
            services.AddSingleton(config);
            services.AddSingleton(secrets);
            services.AddSingleton<ILog>(log);
            services.AddSingleton(serviceProvider =>
                new JobLedger(Path.Combine(config.ResolveDataDirectory(), "state"), log));
            services.AddSingleton(serviceProvider =>
                new FiscalChainStore(Path.Combine(config.ResolveDataDirectory(), "state"), log));
            services.AddSingleton(serviceProvider =>
                new OfflineQueue(Path.Combine(config.ResolveDataDirectory(), "state"), log));
            services.AddSingleton<CompanyProfileService>();
            services.AddSingleton<OfflineReceiptService>();
            services.AddSingleton<OfflineSyncEngine>();
            services.AddSingleton<JobDecoder>();
            services.AddSingleton<FiscalStackClient>();
            services.AddSingleton<JobStore>();
            services.AddSingleton<PrintSnapshotStore>();
            services.AddSingleton<FiscalDayReportService>();
            services.AddSingleton<IReceiptPrinter, WindowsGdiReceiptPrinter>();
            services.AddSingleton<PrintJobPipeline>();
            services.AddSingleton<IJobProcessor>(serviceProvider =>
                serviceProvider.GetRequiredService<PrintJobPipeline>());
            services.AddSingleton<PrintJobServer>();
            if (!configureOnly)
            {
                services.AddHostedService<PrintInterceptorHostedService>();
                services.AddHostedService<OfflineSyncService>();
                services.AddHostedService<ReconcileService>();
                services.AddHostedService<CompanyProfileHostedService>();
            }
        });

        using var host = builder.Build();

        if (configureOnly)
        {
            return RunConfigureOnly(host.Services, config, log, secrets, initialSetup || !config.SetupCompleted);
        }

        if (!trayMode)
        {
            host.Run();
            return 0;
        }

        try
        {
            host.Start();
        }
        catch (Exception ex)
        {
            log.Error("Could not start PrintInterceptor in tray mode", ex);
            MessageBox.Show(
                $"PrintInterceptor could not start.{Environment.NewLine}{Environment.NewLine}{ex.Message}",
                "PrintInterceptor",
                MessageBoxButtons.OK,
                MessageBoxIcon.Error);
            return 1;
        }

        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);

        using var context = new TrayApplicationContext(
            host.Services,
            config,
            log,
            initialSetup || !config.SetupCompleted);
        Application.Run(context);

        try
        {
            host.StopAsync(TimeSpan.FromSeconds(10)).GetAwaiter().GetResult();
        }
        catch (Exception ex)
        {
            log.Warn($"Stopping the host reported: {ex.Message}");
        }

        return 0;
    }

    /// <summary>
    /// Opens the Settings dialog on its own, without starting the print listener
    /// or the offline sync worker. Used by the installer so the operator can
    /// finish configuration after the first install, whether the service or the
    /// tray mode was selected. A service and this dialog share the same
    /// config.json, so a save from here applies to the next receipt.
    /// </summary>
    private static int RunConfigureOnly(
        IServiceProvider services,
        InterceptorConfig config,
        ILog log,
        SecretStore secrets,
        bool highlightFirstRun)
    {
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);

        var offline = services.GetRequiredService<OfflineReceiptService>();
        using var form = new SettingsForm(config, log, offline, secrets);
        form.Text = highlightFirstRun
            ? "PrintInterceptor - first-run setup"
            : "PrintInterceptor settings";

        var result = form.ShowDialog();
        log.Info(
            result == DialogResult.OK
                ? "Configuration saved from the setup dialog."
                : "Setup dialog closed without saving.");
        return 0;
    }

    private const string StatusFlag = "--status";

    /// <summary>
    /// Machine-readable snapshot for the updater and support tools. Only local
    /// state is inspected: no API call is made and no log file is created, so a
    /// till with a dead gateway can still report why it must not be upgraded yet.
    /// </summary>
    private static int PrintStatus(InterceptorConfig config, string[] args)
    {
        var stateDirectory = Path.Combine(config.ResolveDataDirectory(), "state");
        var serviceName = ResolveServiceName(args);
        var serviceState = QueryServiceState(serviceName, out var serviceInstalled);
        var listenerReady = IsListenerReady(config);
        var pendingOffline = CountJsonFiles(Path.Combine(stateDirectory, "offline-queue"));
        var recoveryPending = CountJsonFiles(Path.Combine(stateDirectory, "offline-recovery"));
        var offlinePaused = File.Exists(Path.Combine(stateDirectory, "offline-sync-pause.json"));
        var (staleInFlight, attentionRequired, ledgerError) = ReadLedgerStatus(stateDirectory);

        var runMode = serviceInstalled ? "service" : "tray-or-console";
        var healthy =
            listenerReady &&
            pendingOffline == 0 &&
            recoveryPending == 0 &&
            !offlinePaused &&
            staleInFlight == 0 &&
            attentionRequired == 0 &&
            ledgerError is null &&
            (!serviceInstalled || string.Equals(serviceState, "RUNNING", StringComparison.OrdinalIgnoreCase));

        var version = System.Reflection.Assembly.GetExecutingAssembly().GetName().Version?.ToString() ?? "0.0.0.0";
        var payload = new
        {
            Version = version,
            ServiceName = serviceName,
            ServiceInstalled = serviceInstalled,
            ServiceState = serviceState,
            RunMode = runMode,
            ListenerReady = listenerReady,
            PendingOffline = pendingOffline,
            RecoveryPending = recoveryPending,
            OfflinePaused = offlinePaused,
            StaleInFlight = staleInFlight,
            AttentionRequired = attentionRequired,
            LedgerError = ledgerError,
            Healthy = healthy,
            ConfigPath = config.ConfigPath,
            DataDirectory = config.ResolveDataDirectory(),
            GeneratedAt = DateTimeOffset.Now
        };

        var options = new System.Text.Json.JsonSerializerOptions
        {
            PropertyNamingPolicy = System.Text.Json.JsonNamingPolicy.CamelCase,
            WriteIndented = true
        };
        Console.Out.Write(System.Text.Json.JsonSerializer.Serialize(payload, options));
        return 0;
    }

    private static string ResolveServiceName(string[] args)
    {
        const string prefix = "--service-name=";
        var value = args.FirstOrDefault(argument =>
            argument.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))?[prefix.Length..];
        if (string.IsNullOrWhiteSpace(value))
        {
            return "PrintInterceptor";
        }

        // The value is passed to sc.exe. Allow only characters that are legal in a
        // Windows service name, so a status caller cannot inject command arguments.
        return value.All(ch => char.IsAsciiLetterOrDigit(ch) || ch is '-' or '_' or '.')
            ? value
            : "PrintInterceptor";
    }

    private static string QueryServiceState(string serviceName, out bool installed)
    {
        installed = false;
        if (!OperatingSystem.IsWindows())
        {
            return "NOT_WINDOWS";
        }

        try
        {
            var startInfo = new System.Diagnostics.ProcessStartInfo("sc.exe", $"query \"{serviceName}\"")
            {
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            };

            using var process = System.Diagnostics.Process.Start(startInfo);
            if (process is null)
            {
                return "UNKNOWN";
            }

            var output = process.StandardOutput.ReadToEnd();
            process.StandardError.ReadToEnd();
            process.WaitForExit(3000);
            if (process.ExitCode != 0)
            {
                return "NOT_INSTALLED";
            }

            installed = true;
            foreach (var line in output.Split('\n'))
            {
                if (line.IndexOf("STATE", StringComparison.OrdinalIgnoreCase) < 0)
                {
                    continue;
                }

                var separator = line.IndexOf(':');
                if (separator >= 0)
                {
                    var state = line[(separator + 1)..].Trim();
                    return string.IsNullOrEmpty(state) ? "UNKNOWN" : state.ToUpperInvariant();
                }
            }

            return "UNKNOWN";
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            return "UNKNOWN";
        }
    }

    private static bool IsListenerReady(InterceptorConfig config)
    {
        try
        {
            foreach (var listener in System.Net.NetworkInformation.IPGlobalProperties
                         .GetIPGlobalProperties().GetActiveTcpListeners())
            {
                if (listener.Port != config.ListenPort)
                {
                    continue;
                }

                var wildcard = listener.Address.Equals(System.Net.IPAddress.Any) ||
                               listener.Address.Equals(System.Net.IPAddress.IPv6Any);
                if (!wildcard &&
                    System.Net.IPAddress.IsLoopback(listener.Address) !=
                    System.Net.IPAddress.IsLoopback(System.Net.IPAddress.Parse(config.ListenAddress)))
                {
                    continue;
                }

                if (wildcard ||
                    string.Equals(listener.Address.ToString(), config.ListenAddress, StringComparison.Ordinal))
                {
                    return true;
                }
            }
        }
        catch (Exception ex) when (ex is System.Net.NetworkInformation.NetworkInformationException or
                                       FormatException or InvalidOperationException)
        {
            return false;
        }

        return false;
    }

    private static int CountJsonFiles(string directory)
    {
        try
        {
            return Directory.Exists(directory) ? Directory.EnumerateFiles(directory, "*.json").Count() : 0;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return -1;
        }
    }

    private static (int StaleInFlight, int AttentionRequired, string? Error) ReadLedgerStatus(string stateDirectory)
    {
        try
        {
            var ledger = new JobLedger(stateDirectory, NullLog.Instance);
            var staleAfter = TimeSpan.FromMinutes(10);
            var now = DateTimeOffset.Now;
            var stale = 0;
            var attention = 0;

            foreach (var entry in ledger.All())
            {
                switch (entry.Status)
                {
                    case LedgerStatus.InFlight:
                        attention++;
                        if (now - entry.UpdatedAt > staleAfter) stale++;
                        break;
                    case LedgerStatus.Rejected:
                    case LedgerStatus.NeedsReview:
                    case LedgerStatus.AmbiguousFailure:
                        attention++;
                        break;
                }
            }

            return (stale, attention, null);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            return (-1, -1, ex.Message);
        }
    }

    private static void WriteStartupError(Exception ex, string configPath, bool trayMode)
    {
        var message = $"Could not load configuration from {configPath}: {ex.Message}";
        if (trayMode || !OperatingSystem.IsWindows())
        {
            MessageBox.Show(message, "PrintInterceptor", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
        else
        {
            Console.Error.WriteLine(message);
        }
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool AttachConsole(int dwProcessId);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool AllocConsole();

    /// <summary>
    /// Reuses the parent console when the exe was started from PowerShell or
    /// cmd so --register-key can still read a piped stdin key. Falls back to
    /// allocating a fresh console window when there is no parent. Must be
    /// called before anything touches <see cref="Console"/> so the lazy
    /// stream initialisation picks up the new handles.
    /// </summary>
    private static void AttachParentConsoleOrAllocate()
    {
        if (!AttachConsole(-1))
        {
            AllocConsole();
        }
    }

    /// <summary>
    /// Reads a key from stdin and writes it to the secret store. The key never
    /// appears in the command line, the shell history or a process listing, which
    /// is what makes it usable from an installer or a managed deployment.
    /// </summary>
    private static int RegisterKey(InterceptorConfig config, SecretStore secrets, ILog log)
    {
        Console.Error.Write("Enter the FiscalStack API key (hidden): ");
        var key = ReadHiddenLine();
        Console.Error.WriteLine();

        if (string.IsNullOrWhiteSpace(key))
        {
            Console.Error.WriteLine("No key was supplied; nothing changed.");
            return 2;
        }

        if (!secrets.TrySet(SecretStore.ApiKeyEntry, key.Trim(), out var error))
        {
            Console.Error.WriteLine($"Could not store the key: {error}");
            log.Error($"Could not store the API key: {error}");
            return 1;
        }

        key = "";
        config.ApiKey = "";
        config.Save();
        log.Info($"API key registered for this till in {secrets.DirectoryPath}.");
        Console.Error.WriteLine($"API key stored securely for this machine at {secrets.DirectoryPath}.");
        return 0;
    }

    private static int ClearKey(InterceptorConfig config, SecretStore secrets, ILog log)
    {
        if (!secrets.TryRemove(SecretStore.ApiKeyEntry, out var error))
        {
            Console.Error.WriteLine($"Could not remove the stored key: {error}");
            log.Error($"Could not remove the stored API key: {error}");
            return 1;
        }

        config.ApiKey = "";
        config.Save();
        log.Warn("The API key was removed from this till. Receipts will be held for review.");
        Console.Error.WriteLine("Stored API key removed.");
        return 0;
    }

    /// <summary>
    /// Moves a key found in <c>config.json</c> into the secret store and rewrites
    /// the file without it, so an upgraded till stops shipping the credential in
    /// every backup and support bundle. Nothing is deleted if the store write
    /// fails - the key must keep working.
    /// </summary>
    private static void MigrateLegacyKey(
        InterceptorConfig config,
        SecretStore secrets,
        ILog log,
        ApiKeyResolution resolution)
    {
        if (!SecretStore.Supported || string.IsNullOrWhiteSpace(resolution.Key))
        {
            log.Warn("The API key is stored in the configuration file; register it with --register-key (or save from Settings).");
            return;
        }

        if (!secrets.TrySet(SecretStore.ApiKeyEntry, resolution.Key, out var error))
        {
            log.Warn($"The API key is still in {resolution.Detail} and could not be moved to the secret store: {error}");
            return;
        }

        config.Save();
        log.Info(
            $"The API key was moved out of {resolution.Detail} into the secret store at {secrets.DirectoryPath}; " +
            "the configuration file no longer contains it.");
    }

    private static string DescribeResolution(ApiKeyResolution resolution) => resolution.Origin switch
    {
        ApiKeyOrigin.Store => $"registered on this till at {resolution.Detail}, {SecretStore.Describe(resolution.Key)} (preferred)",
        ApiKeyOrigin.Environment => $"environment variable {resolution.Detail}, {SecretStore.Describe(resolution.Key)} " +
                                    "(registry-readable; register with --register-key or Settings instead)",
        ApiKeyOrigin.ConfigurationFile => $"configuration file {resolution.Detail}, {SecretStore.Describe(resolution.Key)} " +
                                          "(plaintext; register with --register-key or save from Settings)",
        _ => "not configured"
    };

    /// <summary>
    /// Echoless line read for the console. Falls back to a plain read when input
    /// is redirected, which is how an installer can pipe the key in.
    /// </summary>
    private static string ReadHiddenLine()
    {
        if (Console.IsInputRedirected)
        {
            return Console.In.ReadLine() ?? "";
        }

        try
        {
            return ReadSecretLine();
        }
        catch (InvalidOperationException)
        {
            return Console.In.ReadLine() ?? "";
        }
    }

    private static string ReadSecretLine()
    {
        var buffer = new System.Text.StringBuilder();
        while (true)
        {
            var key = Console.ReadKey(intercept: true);
            if (key.Key is ConsoleKey.Enter or ConsoleKey.Tab)
            {
                Console.WriteLine();
                return buffer.ToString();
            }

            if (key.Key == ConsoleKey.Backspace && buffer.Length > 0)
            {
                buffer.Length -= 1;
                continue;
            }

            if (key.KeyChar != '\0' && !char.IsControl(key.KeyChar))
            {
                buffer.Append(key.KeyChar);
            }
        }
    }

    private const string DayReportFlag = "--day-report";
    private const string CsvFlag = "--csv";

    private static bool DayReportRequested(string[] arguments) =>
        arguments.Any(argument =>
            argument.Equals(DayReportFlag, StringComparison.OrdinalIgnoreCase) ||
            argument.StartsWith(DayReportFlag + "=", StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// Prints the fiscal day report for one business date and exits. A till running only as a
    /// service has no tray to open the report window from, so this is how its operator closes a
    /// day and hands the accountant a file:
    /// PrintInterceptor.exe --day-report=2026-10-31 --csv=C:\fiscal\2026-10-31.csv
    /// The report reads local state only and never sends anything, so it works with the network
    /// down and needs no API key.
    /// </summary>
    private static int PrintDayReport(InterceptorConfig config, string[] arguments, ILog log)
    {
        var date = FiscalDayReportService.Today;
        string? csvPath = null;

        foreach (var argument in arguments)
        {
            if (argument.StartsWith(DayReportFlag + "=", StringComparison.OrdinalIgnoreCase))
            {
                var value = argument[(DayReportFlag.Length + 1)..];
                if (!DateOnly.TryParseExact(
                        value, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out date))
                {
                    Console.Error.WriteLine($"--day-report needs a date as yyyy-MM-dd, not \"{value}\".");
                    return 2;
                }
            }
            else if (argument.StartsWith(CsvFlag + "=", StringComparison.OrdinalIgnoreCase))
            {
                csvPath = argument[(CsvFlag.Length + 1)..];
                if (csvPath.Length == 0)
                {
                    Console.Error.WriteLine("--csv needs a file path, as --csv=C:\\fiscal\\lines.csv");
                    return 2;
                }
            }
        }

        var ledger = new JobLedger(Path.Combine(config.ResolveDataDirectory(), "state"), log);
        var report = new FiscalDayReportService(
            config,
            ledger,
            new PrintSnapshotStore(config, log),
            log).Build(date);

        Console.Out.Write(report.Render());

        if (csvPath is null)
        {
            return 0;
        }

        if (report.Lines.Count == 0)
        {
            Console.Error.WriteLine($"{report.BusinessDate} has no fiscalised lines; no CSV was written.");
            return 3;
        }

        try
        {
            var fullPath = Path.GetFullPath(csvPath);
            Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
            File.WriteAllText(fullPath, report.Csv(), new UTF8Encoding(true));
            Console.Error.WriteLine($"Wrote {report.Lines.Count} fiscalised line(s) to {fullPath}.");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Console.Error.WriteLine($"Could not write {csvPath}: {ex.Message}");
            return 1;
        }

        return 0;
    }

    private static string ResolveConfigPath(string[] arguments)
    {
        for (var i = 0; i < arguments.Length - 1; i++)
        {
            if (string.Equals(arguments[i], "--config", StringComparison.OrdinalIgnoreCase))
            {
                return Path.GetFullPath(arguments[i + 1]);
            }
        }

        var fromEnvironment = Environment.GetEnvironmentVariable("PRINTINTERCEPTOR_CONFIG");
        return string.IsNullOrWhiteSpace(fromEnvironment)
            ? InterceptorConfig.DefaultConfigPath()
            : Path.GetFullPath(fromEnvironment);
    }
}
