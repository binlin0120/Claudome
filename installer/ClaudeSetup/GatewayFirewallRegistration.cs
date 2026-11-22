namespace ClaudeSetup;

public static partial class GatewayFirewall
{
    internal static void RegisterRule(object template,GatewayFirewallRule spec,Func<object,object> addAndAttach,Action<string> phase)
    {
        dynamic initial=template;
        phase("set-name");initial.Name=spec.Name;
        phase("set-program");initial.ApplicationName=spec.Program;
        phase("set-protocol");initial.Protocol=17;
        phase("set-port");initial.LocalPorts=spec.Port.ToString();
        phase("set-direction");initial.Direction=1;
        phase("set-action");initial.Action=0;
        phase("set-profiles");initial.Profiles=7;
        phase("set-enabled");initial.Enabled=false;
        phase("add-disabled-rule");dynamic attached=addAndAttach(template);
        // Microsoft requires a rule retrieved from INetFwRules for live edits.
        phase("set-group");attached.Grouping=Group;
        phase("set-description");attached.Description="Blocks non-loopback packet injection into this Claude VM's UDP tunnel.";
        phase("set-remote");attached.RemoteAddresses=RemoteRange;
        phase("set-profiles");attached.Profiles=int.MaxValue;
        phase("set-interfaces");attached.InterfaceTypes="All";
        phase("verify-remote");if(!RemoteScopeMatches((string)attached.RemoteAddresses))throw new SetupException("外层规则的地址覆盖回读不匹配，保持断网。");
        phase("set-enabled");attached.Enabled=true;
    }
    internal static bool IsOwnedDisabledRule(object rule,GatewayFirewallRule spec,bool createdThisRun=false)
    {
        dynamic current=rule;
        var grouping=(string)current.Grouping;
        return !(bool)current.Enabled&&(string)current.Name==spec.Name&&string.Equals((string)current.ApplicationName,spec.Program,StringComparison.OrdinalIgnoreCase)&&(string)current.LocalPorts==spec.Port.ToString()&&(int)current.Protocol==17&&(int)current.Direction==1&&(int)current.Action==0&&(grouping==Group||(createdThisRun&&string.IsNullOrEmpty(grouping)));
    }
}
