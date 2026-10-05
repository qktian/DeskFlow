namespace DeskFlow.Core.Services;

public static class ReliableCleanup
{
    public static void Run(params (string Name, Action Action)[] steps)
    {
        foreach (var step in steps)
            try { step.Action(); }
            catch (Exception error) { DiagnosticLog.Error("Shutdown", step.Name, error); }
    }
}
