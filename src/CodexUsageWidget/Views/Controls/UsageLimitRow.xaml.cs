namespace CodexUsageWidget.Views.Controls;

public partial class UsageLimitRow : System.Windows.Controls.UserControl
{
    public static readonly System.Windows.DependencyProperty ShowWeeklyReferenceProperty =
        System.Windows.DependencyProperty.Register(
            nameof(ShowWeeklyReference), typeof(bool), typeof(UsageLimitRow),
            new System.Windows.PropertyMetadata(false));

    public bool ShowWeeklyReference
    {
        get => (bool)GetValue(ShowWeeklyReferenceProperty);
        set => SetValue(ShowWeeklyReferenceProperty, value);
    }

    public UsageLimitRow()
    {
        InitializeComponent();
    }
}
