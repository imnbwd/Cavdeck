namespace Cavdeck.App.Controls;

public interface IDialogSubmitState
{
    bool CanSubmit { get; }
    event EventHandler? CanSubmitChanged;
}

public enum DialogTone
{
    Neutral,
    Danger
}

/// <summary>Danger actions require an explicit choice, never the dialog default Enter key.</summary>
public static class DialogInteractionPolicy
{
    public static bool IsPrimaryDefault(DialogTone tone) => tone != DialogTone.Danger;
}

public enum DialogChoice
{
    Primary,
    Secondary,
    Dismiss
}

public sealed record DialogRequest
{
    public required string Title { get; init; }
    public string? Message { get; init; }
    public object? Content { get; init; }
    public required string PrimaryText { get; init; }
    public string? SecondaryText { get; init; }
    public string? TertiaryText { get; init; }
    public string CloseText { get; init; } = "";
    public DialogTone Tone { get; init; } = DialogTone.Neutral;
}
