using System.ComponentModel;
using System.Diagnostics;

namespace OverInk.Remote;

public enum FirewallState
{
    /// <summary>An allow rule (or a disabled firewall) lets inbound connections through.</summary>
    Open,
    /// <summary>No rule covers this app or port, so Windows will drop connections from other devices.</summary>
    NoRule,
    /// <summary>An explicit block rule applies (often created when "Cancel" was chosen on the first-run prompt).</summary>
    Blocked,
    /// <summary>The firewall could not be queried; do not warn.</summary>
    Unknown
}

public sealed record FirewallCheckResult(FirewallState State, string Message);

/// <summary>A flattened inbound/outbound rule from Windows Firewall, so rule matching can be tested without COM.</summary>
public sealed record FirewallRuleInfo(
    string Name,
    bool Enabled,
    bool Inbound,
    bool Allow,
    string? Application,
    string? Service,
    string? LocalPorts,
    int Protocol,
    int Profiles);

public static class FirewallHelper
{
    public const string RuleName = "OverInk Remote";

    private const int ProtocolTcp = 6;
    private const int ProtocolAny = 256;
    private const int ProfileDomain = 1;
    private const int ProfilePrivate = 2;
    private const int ProfilePublic = 4;
    private const int ErrorCancelled = 1223;

    /// <summary>Reads Windows Firewall through COM. Can take a second or two; call off the UI thread.</summary>
    public static FirewallCheckResult Check(int port, string? exePath)
    {
        try
        {
            var type = Type.GetTypeFromProgID("HNetCfg.FwPolicy2");
            if (type is null)
            {
                return new FirewallCheckResult(FirewallState.Unknown, string.Empty);
            }

            dynamic policy = Activator.CreateInstance(type)!;
            int active = policy.CurrentProfileTypes;

            var anyEnabled = false;
            foreach (var profile in new[] { ProfileDomain, ProfilePrivate, ProfilePublic })
            {
                if ((active & profile) != 0 && (bool)policy.FirewallEnabled[profile])
                {
                    anyEnabled = true;
                }
            }

            if (!anyEnabled)
            {
                return new FirewallCheckResult(FirewallState.Open, "Windows Firewall is off for the current network.");
            }

            var rules = new List<FirewallRuleInfo>();
            foreach (dynamic rule in policy.Rules)
            {
                rules.Add(new FirewallRuleInfo(
                    (string?)rule.Name ?? string.Empty,
                    (bool)rule.Enabled,
                    (int)rule.Direction == 1,
                    (int)rule.Action == 1,
                    (string?)rule.ApplicationName,
                    (string?)rule.ServiceName,
                    (string?)rule.LocalPorts,
                    (int)rule.Protocol,
                    (int)rule.Profiles));
            }

            return Evaluate(rules, port, exePath, active);
        }
        catch (Exception ex) when (ex is System.Runtime.InteropServices.COMException or
                                       InvalidOperationException or
                                       Microsoft.CSharp.RuntimeBinder.RuntimeBinderException or
                                       UnauthorizedAccessException)
        {
            return new FirewallCheckResult(FirewallState.Unknown, string.Empty);
        }
    }

    /// <summary>Decides whether inbound TCP to this app on <paramref name="port"/> is allowed, from a rule snapshot.</summary>
    public static FirewallCheckResult Evaluate(IEnumerable<FirewallRuleInfo> rules, int port, string? exePath, int activeProfiles)
    {
        var matching = rules
            .Where(r => r.Enabled && r.Inbound &&
                        (r.Profiles & activeProfiles) != 0 &&
                        string.IsNullOrEmpty(r.Service) &&
                        (r.Protocol == ProtocolTcp || r.Protocol == ProtocolAny) &&
                        RuleTargetsApp(r, port, exePath))
            .ToList();

        if (matching.Any(r => !r.Allow))
        {
            return new FirewallCheckResult(
                FirewallState.Blocked,
                "Windows Firewall has a rule that blocks OverInk, so other devices can't connect.");
        }

        if (matching.Any(r => r.Allow))
        {
            return new FirewallCheckResult(FirewallState.Open, "Windows Firewall allows connections to OverInk.");
        }

        return new FirewallCheckResult(
            FirewallState.NoRule,
            $"Windows Firewall has no rule allowing OverInk (port {port}), so other devices may be blocked.");
    }

    public static bool PortListContains(string? localPorts, int port)
    {
        if (string.IsNullOrWhiteSpace(localPorts))
        {
            return false;
        }

        foreach (var part in localPorts.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var dash = part.IndexOf('-');
            if (dash > 0)
            {
                if (int.TryParse(part.AsSpan(0, dash), out var lo) &&
                    int.TryParse(part.AsSpan(dash + 1), out var hi) &&
                    port >= lo && port <= hi)
                {
                    return true;
                }
            }
            else if (int.TryParse(part, out var single) && single == port)
            {
                return true;
            }
        }

        return false;
    }

    private static bool RuleTargetsApp(FirewallRuleInfo rule, int port, string? exePath)
    {
        var wildcardPorts = string.IsNullOrWhiteSpace(rule.LocalPorts) || rule.LocalPorts.Trim() == "*";

        if (!string.IsNullOrWhiteSpace(rule.Application))
        {
            return !string.IsNullOrEmpty(exePath) &&
                   string.Equals(Environment.ExpandEnvironmentVariables(rule.Application), exePath, StringComparison.OrdinalIgnoreCase) &&
                   (wildcardPorts || PortListContains(rule.LocalPorts, port));
        }

        // A rule with no program only counts when it names our port explicitly; "any program, any port" rules
        // are too broad to say anything about this app.
        return !wildcardPorts && PortListContains(rule.LocalPorts, port);
    }

    /// <summary>
    /// Adds an inbound allow rule for the remote port, limited to the local subnet. Shows the Windows admin prompt.
    /// When <paramref name="replaceProgramRules"/> is set, existing inbound rules for this program (including the
    /// block rules Windows creates when the first-run prompt is cancelled) are removed first.
    /// </summary>
    public static Task<(bool Ok, string Message)> AddAllowRuleAsync(int port, string? exePath, bool replaceProgramRules)
    {
        return Task.Run(() =>
        {
            var commands = new List<string>
            {
                $"netsh advfirewall firewall delete rule name=\"{RuleName}\" >nul 2>&1"
            };

            if (replaceProgramRules && !string.IsNullOrEmpty(exePath))
            {
                commands.Add($"netsh advfirewall firewall delete rule name=all dir=in program=\"{exePath}\" >nul 2>&1");
            }

            commands.Add(
                $"netsh advfirewall firewall add rule name=\"{RuleName}\" dir=in action=allow protocol=TCP " +
                $"localport={port} profile=any remoteip=LocalSubnet");

            var startInfo = new ProcessStartInfo("cmd.exe", "/c " + string.Join(" & ", commands))
            {
                Verb = "runas",
                UseShellExecute = true,
                WindowStyle = ProcessWindowStyle.Hidden
            };

            try
            {
                using var process = Process.Start(startInfo);
                if (process is null)
                {
                    return (false, "Could not start the firewall change.");
                }

                process.WaitForExit();
                return process.ExitCode == 0
                    ? (true, $"Firewall rule added for port {port}.")
                    : (false, $"Windows reported an error adding the firewall rule (code {process.ExitCode}).");
            }
            catch (Win32Exception ex) when (ex.NativeErrorCode == ErrorCancelled)
            {
                return (false, "The firewall change was cancelled.");
            }
            catch (Win32Exception ex)
            {
                return (false, $"Could not change the firewall: {ex.Message}");
            }
        });
    }
}
