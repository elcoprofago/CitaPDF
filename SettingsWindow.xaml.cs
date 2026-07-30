using System.Windows;
using CitaPDF.Servicios;

namespace CitaPDF
{
    public partial class SettingsWindow : Window
    {
        private readonly AppSettings _settings;

        public SettingsWindow(AppSettings settings)
        {
            InitializeComponent();
            _settings = settings;
            TxtFilasVisibles.Text = _settings.FilasVisiblesEnGrid.ToString();
            TxtCarpetaDatos.Text = Biblioteca.GetDataDir();
        }

        private void BtnGuardar_Click(object sender, RoutedEventArgs e)
        {
            if (!int.TryParse(TxtFilasVisibles.Text, out int filas) || filas < 3)
            {
                MessageBox.Show(this, "Ingresá un número entero de 3 o más.", "Valor inválido",
                    MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            _settings.FilasVisiblesEnGrid = filas;
            DialogResult = true;
        }

        private void BtnCancelar_Click(object sender, RoutedEventArgs e)
        {
            DialogResult = false;
        }
    }
}
