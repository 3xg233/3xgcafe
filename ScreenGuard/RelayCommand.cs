using System;
using System.Windows.Input;

namespace ScreenGuard;

public class RelayCommand(Action execute) : ICommand
{
    private readonly Action _execute = execute;

    public event EventHandler? CanExecuteChanged
    {
        add { }
        remove { }
    }

    public bool CanExecute(object? parameter) => true;

    public void Execute(object? parameter) => _execute();
}
