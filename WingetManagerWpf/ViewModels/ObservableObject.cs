using System.Collections.Generic;
using System.ComponentModel;

namespace WingetManagerWpf.ViewModels
{
    /// <summary>
    /// 极简 <see cref="INotifyPropertyChanged"/> 基类。
    /// <para>
    /// 为什么不引 MVVM 框架：本项目只有一层 ViewModel（<see cref="MainViewModel"/>），
    /// 为了 4 个属性变化通知引一个 NuGet 包不划算（而且工程约定零 NuGet）。
    /// </para>
    /// 注意：属性名一律**显式传字符串**，不用 [CallerMemberName] —— 显式字符串在
    /// 没有编译器的环境里能靠肉眼/脚本核对「改了属性名却忘了通知」这类错误。
    /// </summary>
    public abstract class ObservableObject : INotifyPropertyChanged
    {
        public event PropertyChangedEventHandler PropertyChanged;

        /// <summary>触发属性变化通知。</summary>
        protected void OnPropertyChanged(string propertyName)
        {
            PropertyChangedEventHandler handler = PropertyChanged;
            if (handler == null || string.IsNullOrEmpty(propertyName))
            {
                return;
            }

            handler(this, new PropertyChangedEventArgs(propertyName));
        }

        /// <summary>
        /// 赋值并在值真的变了时通知。返回 true 表示发生了变化，
        /// 便于调用方把「刷新下游计数」写在 if 里，避免无谓重算。
        /// </summary>
        protected bool SetProperty<T>(ref T field, T value, string propertyName)
        {
            if (EqualityComparer<T>.Default.Equals(field, value))
            {
                return false;
            }

            field = value;
            OnPropertyChanged(propertyName);
            return true;
        }
    }
}
