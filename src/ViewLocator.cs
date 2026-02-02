using Avalonia.Controls;
using Avalonia.Controls.Templates;
using NoBSSftp.ViewModels;
using NoBSSftp.Views;

namespace NoBSSftp;

public class ViewLocator : IDataTemplate
{
    public Control? Build(object? data)
    {
        if (data is null)
            return null;

        if (data is MainWindowViewModel)
            return new MainWindow();
            
        if (data is SessionViewModel)
            return new FileBrowserView();
            
        if (data is TerminalViewModel)
            return new TerminalView();

        return new TextBlock { Text = "Not Found: " + data.GetType().Name };
    }

    public bool Match(object? data)
    {
        return data is ViewModelBase;
    }
}