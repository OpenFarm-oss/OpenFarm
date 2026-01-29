using Avalonia.Controls;
using Avalonia.Markup.Xaml;

namespace NativeDesktopApp.Views;

public partial class PrintJobsView : UserControl
{
    public PrintJobsView()
    {
        InitializeComponent();
    }
    
    private void InitializeComponent()
    {
        AvaloniaXamlLoader.Load(this);
    }
}