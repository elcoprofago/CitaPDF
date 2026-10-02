using System.Diagnostics;
using System.IO;
using System.Windows;
using CitaPDF.Servicios;

namespace CitaPDF
{
    // Corrige la ruta del PDF de un documento: eligiendo el archivo a mano o
    // buscándolo en una carpeta por su contenido. Modifica los registros de
    // la lista que recibe (las mismas instancias que muestra el llamador) y
    // la guarda; si guardar falla, deja las rutas como estaban.
    public partial class UbicarPdfWindow : Window
    {
        private readonly DocumentoRecord _doc;
        private readonly List<DocumentoRecord> _todos;
        private CancellationTokenSource? _cts;
        private bool _cerrada;

        public UbicarPdfWindow(DocumentoRecord doc, List<DocumentoRecord> todos)
        {
            InitializeComponent();
            _doc = doc;
            _todos = todos;

            string titulo = string.IsNullOrWhiteSpace(doc.Titulo) ? doc.DocumentoId : $"«{doc.Titulo}»";
            if (Ubicador.FaltaArchivo(doc))
                TxtMensaje.Text = $"No se encontró el PDF de {titulo} en la ruta guardada. Si lo moviste o renombraste, indicá dónde está ahora:";
            else if (string.IsNullOrWhiteSpace(doc.RutaArchivoOriginal))
                TxtMensaje.Text = $"{titulo} no tiene un PDF local asociado. Podés indicar dónde está:";
            else
                TxtMensaje.Text = $"Ubicación actual del PDF de {titulo}:";
            TxtRuta.Text = string.IsNullOrWhiteSpace(doc.RutaArchivoOriginal) ? "(sin ruta guardada)" : doc.RutaArchivoOriginal;

            if (string.IsNullOrWhiteSpace(doc.HashSha256))
            {
                BtnBuscar.IsEnabled = false;
                BtnBuscar.ToolTip = "Este registro no tiene la huella del PDF: sólo se puede elegir el archivo a mano.";
            }
        }

        // Abre el documento desde el enlace "Abrir": la URL de origen si
        // tiene (como siempre), si no el PDF local. Si el PDF ya no está en la
        // ruta guardada, o no hay nada que abrir, ofrece ubicarlo.
        // 'puedeEditar' lo consulta antes de ofrecerlo (MainWindow no deja
        // tocar el catálogo con un lote en curso).
        public static void AbrirDocumento(Window owner, DocumentoRecord doc, List<DocumentoRecord> todos, Func<bool> puedeEditar)
        {
            try
            {
                if (!string.IsNullOrWhiteSpace(doc.OrigenUrl))
                {
                    Process.Start(new ProcessStartInfo(doc.OrigenUrl) { UseShellExecute = true });
                    return;
                }
                if (!string.IsNullOrWhiteSpace(doc.RutaArchivoOriginal) && File.Exists(doc.RutaArchivoOriginal))
                {
                    Process.Start(new ProcessStartInfo(doc.RutaArchivoOriginal) { UseShellExecute = true });
                    return;
                }
                if (!puedeEditar()) return;
                var ventana = new UbicarPdfWindow(doc, todos) { Owner = owner };
                if (ventana.ShowDialog() == true && File.Exists(doc.RutaArchivoOriginal))
                    Process.Start(new ProcessStartInfo(doc.RutaArchivoOriginal!) { UseShellExecute = true });
            }
            catch (Exception ex)
            {
                MessageBox.Show(owner, "No se pudo abrir: " + ex.Message, "Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void BtnElegir_Click(object sender, RoutedEventArgs e)
        {
            var dlg = new Microsoft.Win32.OpenFileDialog
            {
                Title = "Elegir el PDF de este documento",
                Filter = "PDF (*.pdf)|*.pdf|Todos los archivos (*.*)|*.*",
                InitialDirectory = Ubicador.CarpetaExistenteMasCercana(_doc.RutaArchivoOriginal) ?? "",
            };
            if (dlg.ShowDialog(this) != true) return;
            string ruta = dlg.FileName;

            string hash;
            try { hash = Ubicador.CalcularHash(ruta); }
            catch (Exception ex)
            {
                MessageBox.Show(this, "No se pudo leer el archivo: " + ex.Message, "Ubicar PDF", MessageBoxButton.OK, MessageBoxImage.Error);
                return;
            }

            string? hashNuevo = null;
            if (!string.Equals(hash, _doc.HashSha256, StringComparison.OrdinalIgnoreCase))
            {
                var otro = _todos.FirstOrDefault(d => !ReferenceEquals(d, _doc) &&
                    string.Equals(d.HashSha256, hash, StringComparison.OrdinalIgnoreCase));
                if (otro != null)
                {
                    MessageBox.Show(this, $"Ese PDF ya está catalogado como {otro.DocumentoId} ({otro.Titulo}). Elegí el archivo de este documento.",
                        "Ubicar PDF", MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }
                var r = MessageBox.Show(this,
                    "El archivo elegido no tiene el mismo contenido que el PDF que se catalogó (puede ser otra edición o una copia modificada).\n\n¿Asociarlo igual a este documento?",
                    "Ubicar PDF", MessageBoxButton.YesNo, MessageBoxImage.Warning);
                if (r != MessageBoxResult.Yes) return;
                // Desde ahora la huella del documento es la de este archivo:
                // si se vuelve a mover, la búsqueda por carpeta lo encuentra.
                hashNuevo = hash;
            }

            if (Guardar(new[] { (_doc, ruta, hashNuevo) })) DialogResult = true;
        }

        private async void BtnBuscar_Click(object sender, RoutedEventArgs e)
        {
            string carpeta;
            using (var fb = new System.Windows.Forms.FolderBrowserDialog
            {
                Description = "Carpeta donde buscar el PDF (se revisan también sus subcarpetas)",
                UseDescriptionForTitle = true,
                SelectedPath = Ubicador.CarpetaExistenteMasCercana(_doc.RutaArchivoOriginal) ?? "",
            })
            {
                if (fb.ShowDialog() != System.Windows.Forms.DialogResult.OK) return;
                carpeta = fb.SelectedPath;
            }

            // Una sola recorrida sirve para todos los que falten: los demás
            // documentos cuyo PDF tampoco está en su ruta se buscan a la vez.
            var faltantes = _todos.Where(d => !string.IsNullOrWhiteSpace(d.HashSha256) &&
                (ReferenceEquals(d, _doc) || Ubicador.FaltaArchivo(d))).ToList();
            var buscados = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var d in faltantes)
                buscados.TryAdd(d.HashSha256, Path.GetFileName(d.RutaArchivoOriginal ?? ""));

            Dictionary<string, string> hallados;
            _cts = new CancellationTokenSource();
            EnBusqueda(true);
            try
            {
                var progreso = new Progress<string>(t => TxtProgreso.Text = t);
                var token = _cts.Token;
                hallados = await Task.Run(() => Ubicador.Buscar(carpeta, buscados, progreso, token));
            }
            catch (OperationCanceledException)
            {
                if (!_cerrada) TxtProgreso.Text = "Búsqueda cancelada.";
                return;
            }
            catch (Exception ex)
            {
                TxtProgreso.Text = "No se pudo recorrer la carpeta: " + ex.Message;
                return;
            }
            finally
            {
                if (!_cerrada) EnBusqueda(false);
                _cts.Dispose();
                _cts = null;
            }

            // Se cerró la ventana mientras buscaba: no se aplica nada.
            if (_cerrada) return;

            bool esteHallado = hallados.TryGetValue(_doc.HashSha256, out var rutaEste);
            var otros = faltantes.Where(d => !ReferenceEquals(d, _doc) && hallados.ContainsKey(d.HashSha256)).ToList();

            var cambios = new List<(DocumentoRecord, string, string?)>();
            if (esteHallado) cambios.Add((_doc, rutaEste!, null));
            if (otros.Count > 0)
            {
                string lista = string.Join("\n", otros.Take(10).Select(d => $"  {d.DocumentoId}  {d.Titulo}"));
                if (otros.Count > 10) lista += $"\n  ... y {otros.Count - 10} más";
                var r = MessageBox.Show(this,
                    $"{(esteHallado ? "Además, se" : "No se encontró este PDF, pero se")} encontraron en esa carpeta {otros.Count} documento(s) más cuyo PDF se había movido:\n\n{lista}\n\n¿Actualizar también sus rutas?",
                    "Ubicar PDF", MessageBoxButton.YesNo, MessageBoxImage.Question);
                if (r == MessageBoxResult.Yes)
                    cambios.AddRange(otros.Select(d => (d, hallados[d.HashSha256], (string?)null)));
            }

            if (cambios.Count > 0 && !Guardar(cambios)) return;
            if (esteHallado)
            {
                DialogResult = true;
                return;
            }
            TxtProgreso.Text = $"No se encontró este PDF en {carpeta}." +
                (cambios.Count > 0 ? $" Se actualizaron las rutas de {cambios.Count} documento(s) más." : "") +
                " Podés probar con otra carpeta o elegir el archivo a mano.";
            TxtProgreso.Visibility = Visibility.Visible;
        }

        private bool Guardar(IEnumerable<(DocumentoRecord Doc, string Ruta, string? Hash)> cambios)
        {
            var anteriores = new List<(DocumentoRecord Doc, string? Ruta, string Hash)>();
            foreach (var (doc, ruta, hash) in cambios)
            {
                anteriores.Add((doc, doc.RutaArchivoOriginal, doc.HashSha256));
                doc.RutaArchivoOriginal = ruta;
                if (hash != null) doc.HashSha256 = hash;
            }
            try
            {
                Biblioteca.GuardarDocumentos(_todos);
                return true;
            }
            catch (Exception ex)
            {
                foreach (var (doc, ruta, hash) in anteriores)
                {
                    doc.RutaArchivoOriginal = ruta;
                    doc.HashSha256 = hash;
                }
                MessageBox.Show(this, "No se pudo guardar la biblioteca: " + ex.Message + "\n\nNo se cambió ninguna ruta.",
                    "Ubicar PDF", MessageBoxButton.OK, MessageBoxImage.Error);
                return false;
            }
        }

        private void EnBusqueda(bool buscando)
        {
            BtnElegir.IsEnabled = !buscando;
            BtnBuscar.IsEnabled = !buscando && !string.IsNullOrWhiteSpace(_doc.HashSha256);
            BtnCancelar.Content = buscando ? "Detener búsqueda" : "Cancelar";
            TxtProgreso.Visibility = Visibility.Visible;
            if (buscando) TxtProgreso.Text = "Listando PDFs...";
        }

        private void BtnCancelar_Click(object sender, RoutedEventArgs e)
        {
            if (_cts != null) { _cts.Cancel(); return; }
            Close();
        }

        private void Window_Closing(object? sender, System.ComponentModel.CancelEventArgs e)
        {
            _cerrada = true;
            _cts?.Cancel();
        }

        // Escape: detiene la búsqueda si hay una en curso; si no, cierra.
        private void Window_PreviewKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
        {
            if (e.Key != System.Windows.Input.Key.Escape) return;
            e.Handled = true;
            BtnCancelar_Click(sender, e);
        }
    }
}
