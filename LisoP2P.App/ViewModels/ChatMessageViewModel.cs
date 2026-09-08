using CommunityToolkit.Mvvm.ComponentModel;
using LisoP2P.Storage;

namespace LisoP2P.App.ViewModels;

public sealed partial class ChatMessageViewModel : ObservableObject
{
    public Guid MessageId { get; }
    public string Text { get; }
    public bool IsOutgoing { get; }
    public string TimeText { get; }

    /// <summary>
    /// Who wrote it. Implicit in a 1:1 conversation, so it is only shown in a room where several
    /// people share the same message list.
    /// </summary>
    public string SenderName { get; }

    public bool ShowSender => SenderName.Length > 0;

    [ObservableProperty]
    private bool _delivered;

    private ChatMessageViewModel(
        Guid messageId,
        string text,
        bool isOutgoing,
        DateTimeOffset sentAt,
        bool delivered,
        string senderName)
    {
        MessageId = messageId;
        Text = text;
        IsOutgoing = isOutgoing;
        TimeText = sentAt.ToLocalTime().ToString("HH:mm");
        _delivered = delivered;
        SenderName = senderName;
    }

    public static ChatMessageViewModel From(StoredMessage message, string senderName = "") =>
        new(message.MessageId, message.Text, message.IsOutgoing, message.SentAt, message.Delivered, senderName);

    public void MarkDelivered() => Delivered = true;
}
