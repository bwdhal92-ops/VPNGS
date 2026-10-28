using System.Windows;

namespace FlareVpn;

public partial class ProfileSetupWindow : Window
{
    public string DisplayName => new string(DisplayNameBox.Text.Where(character => !char.IsControl(character)).ToArray()).Trim();

    public ProfileSetupWindow()
    {
        InitializeComponent();
        Loaded += (_, _) => DisplayNameBox.Focus();
    }

    public ProfileSetupWindow(string? initialName) : this()
    {
        DisplayNameBox.Text = initialName ?? "";
    }

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        if (DisplayName.Length < 2)
        {
            System.Windows.MessageBox.Show("اكتب اسم عرض من حرفين على الأقل.", "اسم العرض مطلوب", MessageBoxButton.OK, MessageBoxImage.Information);
            DisplayNameBox.Focus();
            return;
        }
        DialogResult = true;
    }
}
