using System.Collections.Specialized;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using LisoP2P.App.ViewModels;

namespace LisoP2P.App.Views;

public partial class ChatView : UserControl
{
    private IConversationViewModel? _bound;
    private bool _userScrolledUp;

    public ChatView()
    {
        InitializeComponent();
        DataContextChanged += OnDataContextChanged;
        Unloaded += (_, _) => Detach();
    }

    private void OnDataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        Detach();

        _bound = e.NewValue as IConversationViewModel;
        _userScrolledUp = false;

        if (_bound is null)
        {
            return;
        }

        _bound.Messages.CollectionChanged += OnMessagesChanged;

        Dispatcher.BeginInvoke(
            new Action(() =>
            {
                MessageInput.Focus();
                ScrollToEnd();
            }),
            DispatcherPriority.Background);
    }

    private void Detach()
    {
        if (_bound is not null)
        {
            _bound.Messages.CollectionChanged -= OnMessagesChanged;
            _bound = null;
        }
    }

    private void OnMessagesChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (!_userScrolledUp)
        {
            Dispatcher.BeginInvoke(new Action(ScrollToEnd), DispatcherPriority.Background);
        }
    }

    private void ScrollToEnd()
    {
        if (MessagesList.Items.Count > 0)
        {
            MessagesList.ScrollIntoView(MessagesList.Items[^1]);
        }
    }

    private void MessagesList_ScrollChanged(object sender, ScrollChangedEventArgs e)
    {
        if (e.ExtentHeightChange != 0)
        {
            return;
        }

        _userScrolledUp = e.VerticalOffset + e.ViewportHeight < e.ExtentHeight - 2;
    }

    private void MessageInput_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter || (Keyboard.Modifiers & ModifierKeys.Shift) != 0)
        {
            return;
        }

        e.Handled = true;

        if (_bound?.SendCommand.CanExecute(null) == true)
        {
            _bound.SendCommand.Execute(null);
        }
    }
}
