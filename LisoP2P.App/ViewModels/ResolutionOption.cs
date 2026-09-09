namespace LisoP2P.App.ViewModels;

public sealed record ResolutionOption(string Label, int Height)
{
    public override string ToString() => Label;
}

public sealed record PreviewOption(string Label, int Width)
{
    public override string ToString() => Label;
}

/// <summary>The settings screen stores the resolution as text ("Native", "1920x1080").</summary>
public sealed record CaptureResolutionOption(string Label, string Value)
{
    public override string ToString() => Label;
}
