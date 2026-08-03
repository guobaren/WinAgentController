using System.Runtime.InteropServices;

namespace Rc.Cli.Discovery;

/// <summary>
/// Maintains the narrowly scoped Windows Firewall rule required while the controller
/// receives unauthenticated LAN discovery announcements. The rule permits UDP only
/// for the current controller executable; it never opens the Agent control port.
/// </summary>
internal static class DiscoveryFirewallRule
{
    private const string RuleName = "RemoteController Discovery UDP";
    private const string RuleDescription = "Allows Rc.Cli.exe to receive unauthenticated RemoteController LAN discovery announcements on UDP 43000.";
    private const int UdpProtocol = 17;
    private const int InboundDirection = 1;
    private const int AllowAction = 1;
    private const int AllProfiles = int.MaxValue;

    public static void EnsureEnabled(int port)
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var executablePath = Environment.ProcessPath;
        if (string.IsNullOrWhiteSpace(executablePath))
        {
            throw new InvalidOperationException("Unable to determine the controller executable path for the discovery firewall rule.");
        }

        try
        {
            var policyType = Type.GetTypeFromProgID("HNetCfg.FwPolicy2", throwOnError: true)!;
            dynamic policy = Activator.CreateInstance(policyType)!;
            dynamic rules = policy.Rules;

            dynamic? existingRule = null;
            try
            {
                existingRule = rules.Item(RuleName);
            }
            catch (Exception exception) when (exception is COMException or FileNotFoundException)
            {
                // The rule does not exist yet.
            }

            if (existingRule is not null && IsExpectedRule(existingRule, executablePath, port))
            {
                return;
            }

            // This is an application-owned rule name. Recreating it keeps the program
            // path, port, direction, and profile scope correct after an update.
            if (existingRule is not null)
            {
                rules.Remove(RuleName);
            }

            var ruleType = Type.GetTypeFromProgID("HNetCfg.FWRule", throwOnError: true)!;
            dynamic rule = Activator.CreateInstance(ruleType)!;
            rule.Name = RuleName;
            rule.Description = RuleDescription;
            rule.ApplicationName = executablePath;
            rule.Protocol = UdpProtocol;
            rule.LocalPorts = port.ToString(System.Globalization.CultureInfo.InvariantCulture);
            rule.Direction = InboundDirection;
            rule.Action = AllowAction;
            rule.Profiles = AllProfiles;
            rule.Enabled = true;
            rule.Grouping = "RemoteController";
            rules.Add(rule);
        }
        catch (Exception exception) when (exception is UnauthorizedAccessException ||
            exception is COMException comException && (uint)comException.HResult == 0x80070005)
        {
            throw new InvalidOperationException(
                "Unable to enable the LAN discovery firewall rule. Run rcctl discover from an elevated PowerShell or Command Prompt.",
                exception);
        }
    }

    private static bool IsExpectedRule(dynamic rule, string executablePath, int port) =>
        rule.Enabled &&
        rule.Protocol == UdpProtocol &&
        rule.Direction == InboundDirection &&
        rule.Action == AllowAction &&
        rule.Profiles == AllProfiles &&
        string.Equals((string)rule.LocalPorts, port.ToString(System.Globalization.CultureInfo.InvariantCulture), StringComparison.Ordinal) &&
        string.Equals((string)rule.ApplicationName, executablePath, StringComparison.OrdinalIgnoreCase);
}
