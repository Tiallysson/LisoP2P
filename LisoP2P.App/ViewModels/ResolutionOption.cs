namespace LisoP2P.App.ViewModels;

public sealed record ResolutionOption(string Label, int Height)
{
    public override string ToString() => Label;
}

public sealed record PreviewOption(string Label, int Width)
{
    public override string ToString() => Label;
}
