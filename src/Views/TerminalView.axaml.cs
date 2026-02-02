using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using NoBSSftp.ViewModels;

namespace NoBSSftp.Views;

public partial class TerminalView : UserControl
{
    public TerminalView()
    {
        InitializeComponent();
        var outputBox = this.FindControl<TextBox>("OutputBox");
        if (outputBox is not null)
        {
            outputBox.TextChanged += (_, _) =>
            {
                var text = outputBox.Text ?? string.Empty;
                outputBox.CaretIndex = text.Length;
                outputBox.SelectionStart = text.Length;
                outputBox.SelectionEnd = text.Length;
            };
        }

        AddHandler(
            InputElement.KeyDownEvent,
            OnInputKeyDownTunnel,
            RoutingStrategies.Tunnel);
        AddHandler(
            InputElement.TextInputEvent,
            OnInputTextInputTunnel,
            RoutingStrategies.Tunnel);
    }

    private void OnInputKeyDownTunnel(object? sender, KeyEventArgs e)
    {
        if (DataContext is not TerminalViewModel vm)
            return;

        if (e.KeyModifiers == KeyModifiers.Control)
        {
            if (e.Key == Key.C)
            {
                vm.SendControlC();
                e.Handled = true;
                return;
            }
            if (e.Key == Key.L)
            {
                vm.SendControlL();
                e.Handled = true;
                return;
            }
        }

        switch (e.Key)
        {
            case Key.Up:
                vm.SendSequence("\x1b[A");
                e.Handled = true;
                break;
            case Key.Down:
                vm.SendSequence("\x1b[B");
                e.Handled = true;
                break;
            case Key.Back:
                vm.SendSequence("\x7f");
                e.Handled = true;
                break;
            case Key.Enter:
                vm.SendSequence("\n");
                e.Handled = true;
                break;
            case Key.Tab:
                vm.SendSequence("\t");
                e.Handled = true;
                break;
        }
    }

    private void OnInputTextInputTunnel(object? sender, TextInputEventArgs e)
    {
        if (DataContext is not TerminalViewModel vm)
            return;

        if (!string.IsNullOrEmpty(e.Text))
        {
            vm.SendSequence(e.Text);
            e.Handled = true;
        }
    }
}
