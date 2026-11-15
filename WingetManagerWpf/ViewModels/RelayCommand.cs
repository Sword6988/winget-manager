using System;
using System.Windows.Input;

namespace WingetManagerWpf.ViewModels
{
    /// <summary>
    /// 通用命令实现（只在 ViewModel 内部使用，故依赖 <see cref="ICommand"/> —— 它在
    /// System.dll 的 System.Windows.Input 命名空间里，不引 PresentationFramework）。
    /// <para>
    /// <see cref="CanExecuteChanged"/> 是普通字段事件而非 <c>CommandManager.RequerySuggested</c>：
    /// 后者要求订阅 WPF 的 CommandManager，等于把 WPF 依赖带进 ViewModel；
    /// 本版改为「状态变化时显式调 <see cref="RaiseCanExecuteChanged"/>」，
    /// 控件（Button）会重新询问 CanExecute 并自动置灰。
    /// </para>
    /// </summary>
    public sealed class RelayCommand : ICommand
    {
        private readonly Action<object> _execute;
        private readonly Func<object, bool> _canExecute;

        public RelayCommand(Action execute)
            : this(execute, null)
        {
        }

        public RelayCommand(Action execute, Func<bool> canExecute)
        {
            if (execute == null)
            {
                throw new ArgumentNullException("execute");
            }

            _execute = delegate { execute(); };
            if (canExecute != null)
            {
                _canExecute = delegate { return canExecute(); };
            }
        }

        public RelayCommand(Action<object> execute, Func<object, bool> canExecute)
        {
            if (execute == null)
            {
                throw new ArgumentNullException("execute");
            }

            _execute = execute;
            _canExecute = canExecute;
        }

        public event EventHandler CanExecuteChanged;

        public bool CanExecute(object parameter)
        {
            if (_canExecute == null)
            {
                return true;
            }

            try
            {
                return _canExecute(parameter);
            }
            catch (Exception)
            {
                // CanExecute 抛异常会让整个按钮永久灰掉且毫无提示：退化成「可点」，
                // 真正的问题留给 Execute 里的 try/catch 记日志。
                return true;
            }
        }

        public void Execute(object parameter)
        {
            if (_execute == null)
            {
                return;
            }

            _execute(parameter);
        }

        /// <summary>通知界面重新询问 <see cref="CanExecute"/>（按钮的可用态）。</summary>
        public void RaiseCanExecuteChanged()
        {
            EventHandler handler = CanExecuteChanged;
            if (handler != null)
            {
                handler(this, EventArgs.Empty);
            }
        }
    }
}
