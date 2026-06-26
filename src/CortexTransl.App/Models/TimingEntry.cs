namespace CortexTransl.App.Models;

public sealed record TimingEntry(string Name, long ElapsedMilliseconds)
{
    public override string ToString()
    {
        return $"{Name}: {ElapsedMilliseconds} ms";
    }
}
