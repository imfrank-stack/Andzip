using System.Windows.Input;

namespace Andzip.App;

/// <summary>轻量命令（避免引入 MVVM 框架，控制体积）。</summary>
internal sealed class DelegateCommand(Action execute) : ICommand
{
    public event EventHandler? CanExecuteChanged
    {
        add { }
        remove { }
    }

    public bool CanExecute(object? parameter) => true;

    public void Execute(object? parameter) => execute();
}