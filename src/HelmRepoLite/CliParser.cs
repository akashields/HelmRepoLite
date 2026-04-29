using System.Globalization;

namespace HelmRepoLite;

/// <summary>
/// Tiny CLI parser. Avoids pulling in System.CommandLine to keep the
/// "no third-party packages" property. Supports --flag, --flag=value, --flag value.
/// </summary>
public static class CliParser
{
    public static (ServerOptions Options, int? ExitCode, string? Message) Parse(string[] args)
    {
        if (args.Any(a => a is "-h" or "--help"))
        {
            return (new ServerOptions(), 0, HelpText());
        }
        if (args.Any(a => a is "-v" or "--version"))
        {
            return (new ServerOptions(), 0, typeof(CliParser).Assembly.GetName().Version?.ToString() ?? "0.0.0");
        }

        var dict = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var flags = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        for (int i = 0; i < args.Length; i++)
        {
            var a = args[i];
            if (!a.StartsWith("--", StringComparison.Ordinal))
            {
                return (new ServerOptions(), 2, $"unrecognized argument: {a}");
            }
            var key = a[2..];
            string? value = null;
            var eq = key.IndexOf('=');
            if (eq >= 0)
            {
                value = key[(eq + 1)..];
                key = key[..eq];
            }
            else if (i + 1 < args.Length && !args[i + 1].StartsWith("--", StringComparison.Ordinal))
            {
                value = args[++i];
            }

            if (value is null)
            {
                flags.Add(key);
            }
            else
            {
                dict[key] = value;
            }
        }

        // Env var fallbacks (uppercase, dashes -> underscores).
        string Get(string name, string @default)
        {
            if (dict.TryGetValue(name, out var v)) return v;
            var env = Environment.GetEnvironmentVariable("HELMREPOLITE_" + name.ToUpperInvariant().Replace('-', '_'));
            return env ?? @default;
        }

        bool GetFlag(string name) =>
            flags.Contains(name) ||
            string.Equals(Environment.GetEnvironmentVariable("HELMREPOLITE_" + name.ToUpperInvariant().Replace('-', '_')), "true", StringComparison.OrdinalIgnoreCase);

        int port;
        try
        {
            port = int.Parse(Get("port", "8080"), CultureInfo.InvariantCulture);
        }
        catch
        {
            return (new ServerOptions(), 2, "invalid --port value");
        }

        int httpsPort = 0;
        var httpsPortRaw = Get("https-port", "");
        if (!string.IsNullOrEmpty(httpsPortRaw))
        {
            try { httpsPort = int.Parse(httpsPortRaw, CultureInfo.InvariantCulture); }
            catch { return (new ServerOptions(), 2, "invalid --https-port value"); }
        }

        var opts = new ServerOptions
        {
            Port = port,
            Host = Get("host", "0.0.0.0"),
            StorageDir = Get("storage-dir", "./charts"),
            ChartUrl = Get("chart-url", ""),
            BasicAuthUser = Get("basic-auth-user", ""),
            BasicAuthPass = Get("basic-auth-pass", ""),
            AnonymousGet = !GetFlag("require-auth-get"),
            AllowOverwrite = GetFlag("allow-overwrite"),
            DisableDelete = GetFlag("disable-delete"),
            DisableApi = GetFlag("disable-api"),
            Debug = GetFlag("debug"),
            EnableShutdown = GetFlag("enable-shutdown"),
            HttpsPort = httpsPort,
            HttpsCertFile = Get("https-cert-file", ""),
            HttpsCertPassword = Get("https-cert-password", ""),
            HttpsCertThumbprint = Get("https-cert-thumbprint", ""),
            HttpsCertSubject = Get("https-cert-subject", ""),
        };

        return (opts, null, null);
    }

    public static string HelpText() => """
        helmrepolite - lightweight ChartMuseum-compatible Helm chart repository

        Usage:
          helmrepolite [flags]

        Flags:
          --port <int>             TCP port to listen on (default: 8080)
          --host <ip>              Bind address (default: 0.0.0.0)
          --storage-dir <path>     Directory holding .tgz files and index.yaml (default: ./charts)
          --https-port <int>       HTTPS port; requires one cert option below (default: disabled)
          --https-cert-file <path> Path to a PFX/PKCS#12 certificate file
          --https-cert-password <s> Password for the PFX file (default: empty)
          --https-cert-thumbprint <s> Certificate thumbprint in the Windows cert store
          --https-cert-subject <s> Certificate subject/CN in the Windows cert store
          --chart-url <url>        Absolute base URL for chart downloads in index.yaml
          --basic-auth-user <s>    Enable HTTP Basic auth username (empty disables)
          --basic-auth-pass <s>    HTTP Basic auth password
          --require-auth-get       Require auth on GET routes too (default: anonymous GET allowed)
          --allow-overwrite        Allow re-uploading an existing chart version without ?force=true
          --disable-delete         Disable DELETE /api/charts/{name}/{version}
          --disable-api            Disable all /api routes (read-only mode)
          --enable-shutdown        Enable POST /shutdown to stop the process (for CI pipelines)
          --debug                  Verbose logging
          -h, --help               Show this help
          -v, --version            Show version

        Every flag is also settable as HELMREPOLITE_<UPPER_SNAKE> env var.
        """;
}
