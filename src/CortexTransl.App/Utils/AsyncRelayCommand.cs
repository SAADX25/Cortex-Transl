using System.Windows.Input;

namespace CortexTransl.App.Utils;

public sealed class AsyncRelayCommand : ICommand
{
    private readonly Func<object?, Task> _execute;
    private readonly Predicate<object?>? _canExecute;
    private readonly bool _allowConcurrentExecution;
    private bool _isExecuting;

    public AsyncRelayCommand(
        Func<object?, Task> execute,
        Predicate<object?>? canExecute = null,
        bool allowConcurrentExecution = false)
    {
        _execute = execute;
        _canExecute = canExecute;
        _allowConcurrentExecution = allowConcurrentExecution;
    }

    public event EventHandler? CanExecuteChanged;

    public bool IsExecuting
    {
        get => _isExecuting;
        private set
        {
            if (_isExecuting == value)
            {
                return;
            }

            _isExecuting = value;
            RaiseCanExecuteChanged();
        }
    }

    public bool CanExecute(object? parameter)
    {
        return (_allowConcurrentExecution || !IsExecuting) && (_canExecute?.Invoke(parameter) ?? true);
    }

    public async void Execute(object? parameter)
    {
        await ExecuteAsync(parameter);
    }

    public async Task ExecuteAsync(object? parameter = null)
    {
        if (!CanExecute(parameter))
        {
            return;
        }

        try
        {
            if (!_allowConcurrentExecution)
            {
                IsExecuting = true;
            }

            await _execute(parameter);
        }
        finally
        {
            if (!_allowConcurrentExecution)
            {
                IsExecuting = false;
            }
        }
    }

    public void RaiseCanExecuteChanged()
    {
        CanExecuteChanged?.Invoke(this, EventArgs.Empty);
    }
}
