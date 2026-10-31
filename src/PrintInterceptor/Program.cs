using System.Runtime.InteropServices;
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
        var asWindowsService = WindowsServiceHelpers.IsWindowsService();
        var initialSetup = args.Contains("--setup", StringComparer.OrdinalIgnoreCase);
        var configPath = ResolveConfigPath(args);

        // A console window is only allocated for the flows that need one, so a
        // double-click on the exe or the post-install launch shows no terminal.
        var needsConsole =
            !asWindowsService &&
            (registerKey || clearKey || explicitConsole || (explicitService && !configureOnly));
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
            (explicitTray || !explicitConsole);
        var consoleMode =
            !asWindowsService &&
            !configureOnly &&
            !trayMode &&
            (explicitConsole || needsConsole);

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

        var log = new FileLog(Path.Combine(config.ResolveDataDirectory(), "logs"), consoleMode || trayMode);

        if (!configExisted)
        {
            log.Warn($"Created a default configuration at {configPath}. Register the API key and set the printing options, then restart.");
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

        var resolution = ApiKeyResolver.Resolve(
            config,
            secrets,
            Environment.GetEnvironmentVariable(ApiKeyResolver.EnvironmentVariable));
        config.ApiKey = resolution.Key ?? "";

        if (!resolution.Found)
        {
            log.Warn("No API key is configured. Jobs will be held for review until one is registered on this till.");
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
            services.AddSingleton<IReceiptPrinter, WindowsGdiReceiptPrinter>();
            services.AddSingleton<PrintJobPipeline>();
            services.AddSingleton<IJobProcessor>(serviceProvider =>
                serviceProvider.GetRequiredService<PrintJobPipeline>());
            services.AddSingleton<PrintJobServer>();
            if (!configureOnly)
            {
                services.AddHostedService<PrintInterceptorHostedService>();
                services.AddHostedService<OfflineSyncService>();
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
            log.Warn("The API key is stored in the configuration file; move it to the secret store.");
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
        ApiKeyOrigin.Store => $"registered on this till at {SecretStore.Describe(resolution.Key)}, {resolution.Detail}",
        ApiKeyOrigin.Environment => $"environment variable {resolution.Detail}, {SecretStore.Describe(resolution.Key)} " +
                                    "(registry-readable; prefer --register-key)",
        ApiKeyOrigin.ConfigurationFile => $"configuration file {resolution.Detail}, {SecretStore.Describe(resolution.Key)} " +
                                          "(plaintext; move it to the secret store)",
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
