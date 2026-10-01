using System.Diagnostics;
using System.IO;
using System.Windows;
using CitaPDF.Servicios;

namespace CitaPDF
{
    public partial class CitacionWindow : Window
    {
        private readonly DocumentoRecord _documento;
        private bool _enEdicion;

        // El llamador (MainWindow/BibliotecaWindow) lo consulta al cerrar el
        // diálogo para saber si corresponde pasar la fila de "recién
        // agregado" (rojo) a "corregido" (verde oscuro).
        public bool SeGuardo { get; private set; }

        public CitacionWindow(DocumentoRecord documento)
        {
            InitializeComponent();
            _documento = documento;
            CargarCampos();

            if (!_documento.ExtraidoAutomaticamente)
            {
                TxtAviso.Visibility = Visibility.Visible;
                HabilitarEdicion(true);
            }
        }

        private void CargarCampos()
        {
            TxtTitulo.Text = _documento.Titulo;
            TxtAutores.Text = string.Join(Environment.NewLine, _documento.AutoresApa);
            TxtAnio.Text = _documento.Anio;
            TxtEditorial.Text = _documento.Editorial;
            TxtUrl.Text = _documento.OrigenUrl ?? "";
            TxtCita.Text = _documento.CitaApa;
        }

        private void HabilitarEdicion(bool habilitar)
        {
            _enEdicion = habilitar;
            TxtTitulo.IsReadOnly = !habilitar;
            TxtAutores.IsReadOnly = !habilitar;
            TxtAnio.IsReadOnly = !habilitar;
            TxtEditorial.IsReadOnly = !habilitar;
            TxtUrl.IsReadOnly = !habilitar;
            BtnReconstruir.IsEnabled = habilitar;
            BtnCorregir.Content = habilitar ? "Cancelar corrección" : "Corregir";
        }

        private void BtnCorregir_Click(object sender, RoutedEventArgs e)
        {
            if (_enEdicion)
            {
                // Cancelar: descarta ediciones no guardadas y vuelve a los
                // valores persistidos.
                CargarCampos();
            }
            HabilitarEdicion(!_enEdicion);
        }

        private void BtnReconstruir_Click(object sender, RoutedEventArgs e)
        {
            var autores = TxtAutores.Text
                .Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)
                .ToList();

            string firmaAntes = Unificador.Firma(_documento);
            _documento.Titulo = TxtTitulo.Text.Trim();
            _documento.AutoresApa = autores;
            _documento.Anio = TxtAnio.Text.Trim();
            _documento.Editorial = TxtEditorial.Text.Trim();
            _documento.OrigenUrl = string.IsNullOrWhiteSpace(TxtUrl.Text) ? null : TxtUrl.Text.Trim();
            _documento.CitaApa = CitaApa.Construir(autores, _documento.Anio, _documento.Titulo, _documento.Editorial, _documento.OrigenUrl);
            _documento.ExtraidoAutomaticamente = false;
            if (Unificador.Firma(_documento) != firmaAntes) _documento.FechaModificacion = DateTime.Now;

            TxtCita.Text = _documento.CitaApa;
            TxtAviso.Visibility = Visibility.Collapsed;

            var documentos = Biblioteca.CargarDocumentos();
            int idx = documentos.FindIndex(d => d.DocumentoId == _documento.DocumentoId);
            if (idx >= 0) documentos[idx] = _documento;
            else documentos.Add(_documento);
            Biblioteca.GuardarDocumentos(documentos);
            SeGuardo = true;

            HabilitarEdicion(false);
        }

        private void BtnCopiarCita_Click(object sender, RoutedEventArgs e)
        {
            if (!string.IsNullOrWhiteSpace(TxtCita.Text))
                Clipboard.SetText(TxtCita.Text);
        }

        private void BtnCopiarEnlace_Click(object sender, RoutedEventArgs e)
        {
            if (!string.IsNullOrWhiteSpace(_documento.OrigenUrl))
                Clipboard.SetText(_documento.OrigenUrl);
        }

        private void BtnAbrir_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                if (!string.IsNullOrWhiteSpace(_documento.OrigenUrl))
                {
                    Process.Start(new ProcessStartInfo(_documento.OrigenUrl) { UseShellExecute = true });
                }
                else if (!string.IsNullOrWhiteSpace(_documento.RutaArchivoOriginal))
                {
                    if (!File.Exists(_documento.RutaArchivoOriginal))
                    {
                        MessageBox.Show(this, "No se encontró el archivo en la ruta guardada:\n" + _documento.RutaArchivoOriginal,
                            "Archivo no disponible", MessageBoxButton.OK, MessageBoxImage.Warning);
                        return;
                    }
                    Process.Start(new ProcessStartInfo(_documento.RutaArchivoOriginal) { UseShellExecute = true });
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, "No se pudo abrir: " + ex.Message, "Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void BtnCerrar_Click(object sender, RoutedEventArgs e)
        {
            Close();
        }
    }
}
