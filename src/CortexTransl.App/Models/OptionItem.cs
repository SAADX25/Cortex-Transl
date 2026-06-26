namespace CortexTransl.App.Models;

public sealed record OptionItem(string Id, string Name)
{
    public override string ToString()
    {
        return Name;
    }
}
