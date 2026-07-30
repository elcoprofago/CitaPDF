using System.ComponentModel;
using System.IO;
using System.Windows;
using System.Windows.Documents;
using System.Windows.Media;
using CitaPDF.Servicios;
using Microsoft.Win32;

namespace CitaPDF
{
    // Fila del DataGrid: envuelve un DocumentoRecord con propiedades de sólo
    // lectura para mostrar (autores unidos, estado legible) sin ensuciar el
    // modelo persistido.
    public class DocumentoRow
    {
        public DocumentoRecord Documento { get; }
        public DocumentoRow(DocumentoRecord documento) => Documento = documento;

        public string Titulo => Documento.Titulo;
        public string Anio => Documento.Anio;
        public string Editorial => Documento.Editorial;
        public string AutoresTexto => string.Join("; ", Documento.AutoresApa);
        public string Estado => Documento.ExtraidoAutomaticamente
            ? "Guardado"
            : "Guardado (revisar datos)";
    }

    public partial class MainWindow : Window
    {
        private AppSettings _settings = new();
        private List<DocumentoRecord> _documentos = new();
        private readonly bool _autoScrollLog = true;

        public MainWindow()
        {
            InitializeComponent();
            _settings = Biblioteca.CargarSettings();
            _documentos = Biblioteca.CargarDocumentos();
            ActualizarGrid();
            LlamaServerProceso.PurgarHuerfanos();
            Log("CitaPDF listo.", "OK");
        }

        // ===================== Grid principal =====================

        private void ActualizarGrid()
        {
            var recientes = _documentos
                .OrderByDescending(d => d.FechaAdquisicion)
                .Take(Math.Max(3, _settings.FilasVisiblesEnGrid))
                .Select(d => new DocumentoRow(d))
                .ToList();

            GridDocumentos.ItemsSource = recientes;
            TxtEstadoVacio.Visibility = _documentos.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        }

        private void GridDocumentos_MouseDoubleClick(object sender, System.Windows.Input.MouseButtonEventArgs e)
        {
            if (GridDocumentos.SelectedItem is DocumentoRow fila)
            {
                new CitacionWindow(fila.Documento) { Owner = this }.ShowDialog();
                // La ventana de cita puede haber corregido datos -- releer y
                // refrescar por si cambió algo.
                _documentos = Biblioteca.CargarDocumentos();
                ActualizarGrid();
            }
        }

        // ===================== Adquisición =====================

        private bool _procesando;

        private async void BtnAgregarArchivos_Click(object sender, RoutedEventArgs e)
        {
            var dlg = new OpenFileDialog
            {
                Filter = "Archivos PDF (*.pdf)|*.pdf",
                Multiselect = true,
            };
            if (dlg.ShowDialog() != true) return;

            await ProcesarLoteAsync(dlg.FileNames.Select(p => (RutaLocal: (string?)p, Url: (string?)null)).ToList());
        }

        private async void BtnAgregarCarpeta_Click(object sender, RoutedEventArgs e)
        {
            using var dlg = new System.Windows.Forms.FolderBrowserDialog();
            if (dlg.ShowDialog() != System.Windows.Forms.DialogResult.OK) return;

            var opcion = ChkSubcarpetas.IsChecked == true ? SearchOption.AllDirectories : SearchOption.TopDirectoryOnly;
            string[] archivos;
            try
            {
                archivos = Directory.GetFiles(dlg.SelectedPath, "*.pdf", opcion);
            }
            catch (Exception ex)
            {
                Log($"No se pudo leer la carpeta: {ex.Message}", "ERROR");
                return;
            }

            if (archivos.Length == 0)
            {
                Log("No se encontraron archivos .pdf en la carpeta elegida.", "WARN");
                return;
            }

            await ProcesarLoteAsync(archivos.Select(p => (RutaLocal: (string?)p, Url: (string?)null)).ToList());
        }

        private async void BtnAgregarUrl_Click(object sender, RoutedEventArgs e)
        {
            var urls = TxtUrl.Text
                .Split(new[] { ';', '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)
                .Select(u => u.Trim())
                .Where(u => u.Length > 0)
                .ToList();

            if (urls.Count == 0)
            {
                Log("Ingresá al menos una URL.", "WARN");
                return;
            }

            await ProcesarLoteAsync(urls.Select(u => (RutaLocal: (string?)null, Url: (string?)u)).ToList());
            TxtUrl.Clear();
        }

        private void Window_DragOver(object sender, DragEventArgs e)
        {
            e.Effects = e.Data.GetDataPresent(DataFormats.FileDrop) ? DragDropEffects.Copy : DragDropEffects.None;
            e.Handled = true;
        }

        private async void Window_Drop(object sender, DragEventArgs e)
        {
            if (!e.Data.GetDataPresent(DataFormats.FileDrop)) return;
            var rutas = ((string[])e.Data.GetData(DataFormats.FileDrop))
                .Where(p => Path.GetExtension(p).Equals(".pdf", StringComparison.OrdinalIgnoreCase))
                .ToList();

            if (rutas.Count == 0)
            {
                Log("Sólo se aceptan archivos .pdf.", "WARN");
                return;
            }

            await ProcesarLoteAsync(rutas.Select(p => (RutaLocal: (string?)p, Url: (string?)null)).ToList());
        }

        // ===================== Pipeline de extracción automática =====================

        private async Task ProcesarLoteAsync(List<(string? RutaLocal, string? Url)> items)
        {
            if (_procesando)
            {
                Log("Ya hay un lote en proceso, esperá a que termine.", "WARN");
                return;
            }

            _procesando = true;
            BtnAgregarArchivos.IsEnabled = false;
            BtnAgregarCarpeta.IsEnabled = false;
            BtnAgregarUrl.IsEnabled = false;

            var agregados = new List<DocumentoRecord>();
            try
            {
                foreach (var item in items)
                    await ProcesarUnoAsync(item.RutaLocal, item.Url, agregados);
            }
            finally
            {
                _procesando = false;
                BtnAgregarArchivos.IsEnabled = true;
                BtnAgregarCarpeta.IsEnabled = true;
                BtnAgregarUrl.IsEnabled = true;
            }

            // Si se procesó un único archivo, se muestra directamente la
            // cita (éxito o fallback) en vez de obligar a buscarlo en el
            // grid -- para un lote de varios, cada uno queda visible en el
            // grid con "Ver cita" bajo demanda.
            if (items.Count == 1 && agregados.Count == 1)
                new CitacionWindow(agregados[0]) { Owner = this }.ShowDialog();

            _documentos = Biblioteca.CargarDocumentos();
            ActualizarGrid();
        }

        private async Task ProcesarUnoAsync(string? rutaLocal, string? url, List<DocumentoRecord> agregados)
        {
            string etiqueta = rutaLocal != null ? Path.GetFileName(rutaLocal) : url!;

            byte[] bytes;
            try
            {
                if (rutaLocal != null)
                {
                    Log($"Validando {etiqueta}...", "INFO");
                    bytes = await File.ReadAllBytesAsync(rutaLocal);
                }
                else
                {
                    Log($"Descargando {etiqueta}...", "SPINNER");
                    bytes = await PdfValidacion.DescargarAsync(url!);
                }
            }
            catch (Exception ex)
            {
                Log($"No se pudo obtener {etiqueta}: {ex.Message}", "ERROR");
                return;
            }

            if (!PdfValidacion.EsPdfValido(bytes))
            {
                Log($"{etiqueta} no parece ser un PDF válido (firma inválida).", "ERROR");
                return;
            }

            string hash = PdfValidacion.CalcularHashSha256(bytes);
            var existente = _documentos.FirstOrDefault(d => d.HashSha256 == hash);
            if (existente != null)
            {
                var confirmar = MessageBox.Show(this,
                    $"{etiqueta} ya está catalogado como {existente.DocumentoId} ({existente.Titulo}).\n\n¿Agregarlo de todos modos como una entrada nueva?",
                    "Documento duplicado", MessageBoxButton.YesNo, MessageBoxImage.Question);
                if (confirmar != MessageBoxResult.Yes)
                {
                    Log($"{etiqueta}: duplicado de {existente.DocumentoId}, no se agregó.", "WARN");
                    return;
                }
            }

            string texto;
            try
            {
                Log($"Extrayendo texto de {etiqueta}...", "INFO", overwrite: true);
                texto = PdfTexto.ExtraerPrimerasPaginas(bytes);
            }
            catch (Exception ex)
            {
                Log($"No se pudo leer el contenido de {etiqueta}: {ex.Message}", "WARN");
                texto = "";
            }

            var doc = new DocumentoRecord
            {
                DocumentoId = Biblioteca.GenerarNuevoId(_documentos),
                HashSha256 = hash,
                FechaAdquisicion = DateTime.Now,
                OrigenUrl = url,
                RutaArchivoOriginal = rutaLocal,
            };

            if (texto.Length < PdfTexto.TextoMinimoChars)
            {
                Log($"{etiqueta}: muy poco texto extraído (¿PDF escaneado?), se cataloga sin cita automática.", "WARN");
                doc.ExtraidoAutomaticamente = false;
            }
            else
            {
                Log($"Consultando modelo local para {etiqueta}...", "SPINNER", overwrite: true);
                bool listo = await LlamaServerProceso.AsegurarIniciadoAsync((msg, nivel, overwrite) => Log(msg, nivel, overwrite));

                ExtraccionResultado? resultado = null;
                if (listo)
                {
                    try
                    {
                        resultado = await ExtraccionLlm.ExtraerAsync(texto);
                    }
                    catch (Exception ex)
                    {
                        Log($"Error consultando el modelo local para {etiqueta}: {ex.Message}", "ERROR");
                    }
                }

                if (resultado != null && resultado.Exito)
                {
                    doc.Titulo = resultado.Titulo;
                    doc.AutoresApa = resultado.AutoresApa;
                    doc.Anio = resultado.Anio;
                    doc.Editorial = resultado.Editorial;
                    doc.ExtraidoAutomaticamente = true;
                }
                else
                {
                    Log($"{etiqueta}: no se pudo extraer la cita automáticamente, queda para completar a mano.", "WARN");
                    doc.ExtraidoAutomaticamente = false;
                }
            }

            doc.CitaApa = CitaApa.Construir(doc.AutoresApa, doc.Anio, doc.Titulo, doc.Editorial, doc.OrigenUrl);

            _documentos.Add(doc);
            Biblioteca.GuardarDocumentos(_documentos);
            agregados.Add(doc);

            Log($"{doc.DocumentoId} guardado: {(string.IsNullOrWhiteSpace(doc.Titulo) ? etiqueta : doc.Titulo)}",
                doc.ExtraidoAutomaticamente ? "OK" : "WARN", overwrite: true);
        }

        // ===================== Biblioteca completa / Configuración =====================

        private void BtnVerBiblioteca_Click(object sender, RoutedEventArgs e)
        {
            new BibliotecaWindow() { Owner = this }.ShowDialog();
        }

        private void BtnConfiguracion_Click(object sender, RoutedEventArgs e)
        {
            var wnd = new SettingsWindow(_settings) { Owner = this };
            if (wnd.ShowDialog() == true)
            {
                Biblioteca.GuardarSettings(_settings);
                ActualizarGrid();
            }
        }

        // ===================== Log =====================

        private void Log(string msg, string level = "INFO", bool overwrite = false, bool protect = false)
        {
            if (!Dispatcher.CheckAccess())
            {
                Dispatcher.BeginInvoke(new Action(() => Log(msg, level, overwrite, protect)));
                return;
            }

            Brush color = level switch
            {
                "OK" => Brushes.LightGreen,
                "WARN" => Brushes.Khaki,
                "ERROR" => Brushes.OrangeRed,
                "SPINNER" => Brushes.Cyan,
                _ => Brushes.White,
            };

            var paragraph = new Paragraph { Margin = new Thickness(0), LineHeight = 14 };
            paragraph.Tag = protect ? "PROTECTED" : null;
            paragraph.Inlines.Add(new Run($"{DateTime.Now:HH:mm:ss} - {msg}") { Foreground = color });

            if (overwrite && TxtLog.Document.Blocks.LastBlock is Paragraph last && last.Tag?.ToString() != "PROTECTED")
                TxtLog.Document.Blocks.Remove(last);

            TxtLog.Document.Blocks.Add(paragraph);

            if (_autoScrollLog)
                TxtLog.ScrollToEnd();
        }

        // ===================== Cierre =====================

        private void Window_Closing(object sender, CancelEventArgs e)
        {
            LlamaServerProceso.Detener();
        }
    }
}
