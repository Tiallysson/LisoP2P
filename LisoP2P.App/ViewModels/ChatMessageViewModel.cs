using CommunityToolkit.Mvvm.ComponentModel;
using LisoP2P.Storage;

namespace LisoP2P.App.ViewModels;

public sealed partial class ChatMessageViewModel : ObservableObject
{
    public Guid MessageId { get; }
    public string Text { get; }
    public bool IsOutgoing { get; }
    public string TimeText { get; }

    [ObservableProperty]
    private bool _delivered;

    private ChatMessageViewModel(Guid messageId, string text, bool isOutgoing, DateTimeOffset sentAt, bool delivered)
    {
        MessageId = messageId;
        Text = text;
        IsOutgoing = isOutgoing;
        TimeText = sentAt.ToLocalTime().ToString("HH:mm");
        _delivered = delivered;
    }

    public static ChatMessageViewModel From(StoredMessage message) =>
        new(message.MessageId, message.Text, message.IsOutgoing, message.SentAt, message.Delivered);

    public void MarkDelivered() => Delivered = true;
}
