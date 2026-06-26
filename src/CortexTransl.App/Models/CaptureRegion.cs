namespace CortexTransl.App.Models;

public sealed record CaptureRegion(int X, int Y, int Width, int Height)
{
    public static CaptureRegion Empty { get; } = new(0, 0, 0, 0);

    public bool IsEmpty => Width <= 0 || Height <= 0;

    public override string ToString()
    {
        return IsEmpty
            ? "No region selected"
            : $"X {X}, Y {Y}, W {Width}, H {Height}";
    }
}
