using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace JaClipei.Client.Converters;

[ValueConversion(typeof(bool), typeof(bool))]
public class InverseBoolConverter : IValueConverter
{
    public object Convert(object v, Type t, object p, CultureInfo c) => v is bool b && !b;
    public object ConvertBack(object v, Type t, object p, CultureInfo c) => v is bool b && !b;
}

[ValueConversion(typeof(bool), typeof(string))]
public class BoolToLoginRegisterConverter : IValueConverter
{
    public object Convert(object v, Type t, object p, CultureInfo c)
        => v is bool b && b ? "Criar conta" : "Entrar";
    public object ConvertBack(object v, Type t, object p, CultureInfo c)
        => DependencyProperty.UnsetValue;
}

[ValueConversion(typeof(int), typeof(Visibility))]
public class CountToVisibilityConverter : IValueConverter
{
    public object Convert(object v, Type t, object p, CultureInfo c)
        => v is int i && i > 0 ? Visibility.Visible : Visibility.Collapsed;
    public object ConvertBack(object v, Type t, object p, CultureInfo c)
        => DependencyProperty.UnsetValue;
}

[ValueConversion(typeof(object), typeof(Visibility))]
public class NullToVisibilityConverter : IValueConverter
{
    public object Convert(object v, Type t, object p, CultureInfo c)
        => v == null ? Visibility.Visible : Visibility.Collapsed;
    public object ConvertBack(object v, Type t, object p, CultureInfo c)
        => DependencyProperty.UnsetValue;
}

[ValueConversion(typeof(object), typeof(Visibility))]
public class NotNullToVisibilityConverter : IValueConverter
{
    public object Convert(object v, Type t, object p, CultureInfo c)
        => v != null ? Visibility.Visible : Visibility.Collapsed;
    public object ConvertBack(object v, Type t, object p, CultureInfo c)
        => DependencyProperty.UnsetValue;
}
