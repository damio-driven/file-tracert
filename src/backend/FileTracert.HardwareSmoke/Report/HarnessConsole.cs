namespace FileTracert.HardwareSmoke;

/// <summary>
/// The harness's only channel to the operator. Abstracted so the semi-automatic scenarios can be
/// exercised in <c>dotnet test</c> without a real console attached.
/// </summary>
public interface IHarnessConsole
{
    void Write(string line);

    /// <summary>
    /// Shows <paramref name="message"/> and blocks until the operator acknowledges. Only ever
    /// called from scenarios gated behind <c>SemiAutomatic=true</c>.
    /// </summary>
    void WaitForOperator(string message);
}

/// <summary>Standard-output implementation used by the runner.</summary>
public sealed class HarnessConsole : IHarnessConsole
{
    public void Write(string line) => Console.WriteLine($"[harness] {line}");

    public void WaitForOperator(string message)
    {
        Console.WriteLine();
        Console.WriteLine($"[harness] >>> {message}");
        Console.WriteLine("[harness] >>> premi INVIO per continuare…");
        Console.ReadLine();
    }
}
