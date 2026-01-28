using Avalonia.Controls;
using native_desktop_app.ViewModels;

namespace native_desktop_app.Views;

public partial class MessagesView : UserControl
{
    public MessagesView()
    {
        InitializeComponent();
    }

    protected override void OnAttachedToVisualTree(Avalonia.VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        (DataContext as MessagesViewModel)?.StartPolling();
    }

    protected override void OnDetachedFromVisualTree(Avalonia.VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);
        (DataContext as MessagesViewModel)?.StopPolling();
    }
}
