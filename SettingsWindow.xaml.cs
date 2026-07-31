using System.IO;
using System.Windows;
using CitaPDF.Servicios;
using Microsoft.Win32;

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

            switch (_settings.Tema)
            {
                case "Oscuro": RbTemaOscuro.IsChecked = true; break;
                case "Textura": RbTemaTextura.IsChecked = true; break;
                default: RbTemaClaro.IsChecked = true; break;
            }
            TxtRutaTextura.Text = _settings.RutaTextura ?? "";
            ActualizarEstadoTextura();
        }

        private void Tema_Changed(object sender, RoutedEventArgs e)
        {
            ActualizarEstadoTextura();
        }

        private void ActualizarEstadoTextura()
        {
            bool esTextura = RbTemaTextura.IsChecked == true;
            TxtRutaTextura.IsEnabled = esTextura;
            BtnExaminarTextura.IsEnabled = esTextura;
        }

        private void BtnExaminarTextura_Click(object sender, RoutedEventArgs e)
        {
            var dlg = new OpenFileDialog
            {
                Filter = "Imágenes (*.png;*.jpg;*.jpeg;*.bmp)|*.png;*.jpg;*.jpeg;*.bmp",
            };
            if (dlg.ShowDialog() == true)
                TxtRutaTextura.Text = dlg.FileName;
        }

        private void BtnGuardar_Click(object sender, RoutedEventArgs e)
        {
            if (!int.TryParse(TxtFilasVisibles.Text, out int filas) || filas < 3)
            {
                MessageBox.Show(this, "Ingresá un número entero de 3 o más.", "Valor inválido",
                    MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            string tema = RbTemaOscuro.IsChecked == true ? "Oscuro"
                : RbTemaTextura.IsChecked == true ? "Textura"
                : "Claro";

            if (tema == "Textura" && !File.Exists(TxtRutaTextura.Text))
            {
                MessageBox.Show(this, "Elegí una imagen válida para el tema de textura.", "Valor inválido",
                    MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            _settings.FilasVisiblesEnGrid = filas;
            _settings.Tema = tema;
            _settings.RutaTextura = string.IsNullOrWhiteSpace(TxtRutaTextura.Text) ? null : TxtRutaTextura.Text;
            DialogResult = true;
        }

        private void BtnCancelar_Click(object sender, RoutedEventArgs e)
        {
            DialogResult = false;
        }
    }
}
