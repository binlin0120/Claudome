namespace ClaudeSetup;

internal sealed class DesktopConnectionPolicy
{
    public bool Suspended { get; private set; }
    public bool RunningSeen { get; private set; }
    public bool WindowSeen { get; private set; }
    public bool LaunchIssued { get; private set; }
    public void ResumeExplicitly() { Suspended=false; RunningSeen=false; WindowSeen=false; LaunchIssued=false; }
    public void ObserveRunning() => RunningSeen=true;
    public void ObserveStopped() { if(RunningSeen) Suspend(); }
    public void ObserveWindow() => WindowSeen=true;
    public void Suspend() => Suspended=true;
    public bool TryLaunchFrontend()
    {
        if(Suspended||WindowSeen||LaunchIssued)return false;
        LaunchIssued=true;return true;
    }
}
