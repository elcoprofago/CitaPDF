using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
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

        // Marca los documentos incorporados en el último lote procesado --
        // sólo en memoria (se pierde al reiniciar la app) para que el
        // usuario tenga a la vista cuáles todavía no revisó. Una vez que el
        // usuario corrige/edita uno de esos, pasa de "nuevo" (rojo) a
        // "corregido" (verde oscuro) en vez de perder el resaltado.
        public bool EsNuevo { get; }
        public bool EsCorregido { get; }

        public DocumentoRow(DocumentoRecord documento, bool esNuevo = false, bool esCorregido = false)
        {
            Documento = documento;
            EsCorregido = esCorregido;
            EsNuevo = esNuevo && !esCorregido;
        }

        public string Titulo => Documento.Titulo;
        public string Anio => Documento.Anio;
        public string Editorial => Documento.Editorial;
        public string AutoresTexto => string.Join("; ", Documento.AutoresApa ?? new List<string>());
    }

    public partial class MainWindow : Window
    {
        private AppSettings _settings = new();
        private List<DocumentoRecord> _documentos = new();
        private readonly HashSet<string> _idsRecienAgregados = new();
        // IDs corregidos/editados desde que se agregaron -- también en
        // memoria, no persiste al reiniciar. Prevalece sobre EsNuevo (ver
        // DocumentoRow) para pasar de rojo a verde oscuro sin perder el
        // resaltado de "algo pasó con este documento".
        private readonly HashSet<string> _idsCorregidos = new();
        // "Saltear todo" en el diálogo de duplicado se aplica al resto del
        // lote en curso -- se resetea en cada ProcesarLoteAsync, no persiste
        // entre lotes distintos.
        private bool _saltearDuplicadosEnLote;
        private readonly bool _autoScrollLog = true;

        // Spinner estilo consola (mismo patrón que PostOCRNormalizer) para
        // los pasos del pipeline que pueden demorar: descarga/lectura del
        // PDF, extracción de texto y consulta al modelo local.
        private static readonly string[] SpinnerFrames = { "/", "-", "\\", "|" };
        private DispatcherTimer? _spinnerTimer;
        private int _spinnerFrame;

        // Segundos promedio por documento (carga/descarga + extracción de
        // texto + consulta al modelo local), usado sólo para el mensaje de
        // "tiempo estimado" del lote -- es una heurística, no una medición.
        private const int SegundosEstimadosPorDocumento = 90;

        private GridLength? _alturaLogGuardada;

        public MainWindow()
        {
            InitializeComponent();
            _settings = Biblioteca.CargarSettings();
            _documentos = Biblioteca.CargarDocumentos();
            ActualizarGrid();
            CargarIconoConfiguracion();
            ActualizarEstadoServidor();
            AplicarTema();
            LlamaServerProceso.PurgarHuerfanos();
            Log("CitaPDF listo.", "OK");
        }

        // ===================== Tema (misma lógica que CONSULTOR-GUI) =====================

        private void AplicarTema()
        {
            switch (_settings.Tema)
            {
                case "Oscuro":
                    Background = new SolidColorBrush(Color.FromRgb(0x1A, 0x1A, 0x1A));
                    Foreground = Brushes.White;
                    break;
                case "Textura":
                    Brush? fondoMosaico = null;
                    if (!string.IsNullOrEmpty(_settings.RutaTextura) && File.Exists(_settings.RutaTextura))
                    {
                        try
                        {
                            var bmp = new BitmapImage(new Uri(_settings.RutaTextura));
                            fondoMosaico = new ImageBrush(bmp)
                            {
                                TileMode = TileMode.Tile,
                                Viewport = new Rect(0, 0, bmp.Width, bmp.Height),
                                ViewportUnits = BrushMappingMode.Absolute
                            };
                        }
                        catch (Exception ex)
                        {
                            Log($"No se pudo cargar la imagen de textura: {ex.Message}", "WARN");
                        }
                    }
                    fondoMosaico ??= new SolidColorBrush(Color.FromRgb(0x1A, 0x1A, 0x1A));
                    Background = fondoMosaico;
                    Foreground = Brushes.White;
                    break;
                default: // "Claro"
                    Background = new SolidColorBrush(Color.FromRgb(0xEC, 0xEC, 0xEC));
                    Foreground = Brushes.Black;
                    break;
            }

            // La leyenda vive sobre el fondo de ventana (no sobre el log,
            // que siempre es oscuro) -- sigue el mismo criterio claro/oscuro.
            TxtLegend.Foreground = _settings.Tema == "Claro" ? Brushes.Black : Brushes.White;
        }

        // ===================== Panel de log colapsable =====================

        private void BtnToggleLog_Click(object sender, RoutedEventArgs e)
        {
            bool colapsar = TxtLog.Visibility == Visibility.Visible;
            if (colapsar)
            {
                _alturaLogGuardada = RowLog.Height;
                TxtLog.Visibility = Visibility.Collapsed;
                RowLog.Height = GridLength.Auto;
                BtnToggleLog.Content = "▶";
            }
            else
            {
                TxtLog.Visibility = Visibility.Visible;
                RowLog.Height = _alturaLogGuardada ?? new GridLength(180);
                BtnToggleLog.Content = "▼";
            }
        }

        private void IniciarSpinner(string mensajeBase)
        {
            _spinnerTimer?.Stop();
            _spinnerFrame = 0;
            _spinnerTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(120) };
            _spinnerTimer.Tick += (s, e) =>
            {
                _spinnerFrame = (_spinnerFrame + 1) % SpinnerFrames.Length;
                Log($"{mensajeBase} {SpinnerFrames[_spinnerFrame]}", "SPINNER", overwrite: true);
            };
            _spinnerTimer.Start();
        }

        private void DetenerSpinner()
        {
            _spinnerTimer?.Stop();
            _spinnerTimer = null;
        }

        // ===================== Configuración / llama-server =====================

        private void CargarIconoConfiguracion()
        {
            ImgConfiguracion.Source = new BitmapImage(new Uri("pack://application:,,,/assets/rueda.png"));
        }

        private void ActualizarEstadoServidor()
        {
            bool corriendo = LlamaServerProceso.EstaCorriendo;
            TxtEstadoServidor.Text = corriendo ? "● Modelo local: corriendo" : "● Modelo local: detenido";
            TxtEstadoServidor.Foreground = corriendo ? Brushes.LightGreen : Brushes.Gray;
            BtnIniciarLlama.IsEnabled = !corriendo;
        }

        private async void BtnIniciarLlama_Click(object sender, RoutedEventArgs e)
        {
            BtnIniciarLlama.IsEnabled = false;
            bool listo = await LlamaServerProceso.AsegurarIniciadoAsync((msg, nivel, overwrite) => Log(msg, nivel, overwrite));
            ActualizarEstadoServidor();
            if (!listo) Log("No se pudo iniciar el modelo local.", "ERROR");
        }

        private void BtnPurgar_Click(object sender, RoutedEventArgs e)
        {
            LlamaServerProceso.PurgarHuerfanos();
            ActualizarEstadoServidor();
            Log("Purga de procesos huérfanos completada.", "OK");
            // La propia instancia nunca se purga (PurgarHuerfanos la excluye
            // por PID), pero vale avisar que sigue corriendo para que no se
            // confunda con un huérfano que debería haber desaparecido.
            if (LlamaServerProceso.EstaCorriendo)
                Log("Servidor funcionando.", "WARN");
        }

        // ===================== Grid principal =====================

        private void ActualizarGrid()
        {
            var recientes = _documentos
                .OrderByDescending(d => d.FechaAdquisicion)
                .Take(Math.Max(3, _settings.FilasVisiblesEnGrid))
                .Select(d => new DocumentoRow(d, _idsRecienAgregados.Contains(d.DocumentoId), _idsCorregidos.Contains(d.DocumentoId)))
                .ToList();

            GridDocumentos.ItemsSource = recientes;
            TxtEstadoVacio.Visibility = _documentos.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        }

        // Mientras hay un lote en proceso, el catálogo no debe editarse --
        // la ventana de biblioteca y la de detalle de cita escriben directo
        // sobre biblioteca.json, y podrían pisar lo que el lote está
        // guardando en paralelo.
        private bool AvisarSiIndexando()
        {
            if (!_procesando) return false;
            MessageBox.Show(this,
                "Indexación en curso: esperá a que termine el proceso por lotes antes de editar el catálogo.",
                "Indexación en curso", MessageBoxButton.OK, MessageBoxImage.Information);
            return true;
        }

        private void GridDocumentos_MouseDoubleClick(object sender, System.Windows.Input.MouseButtonEventArgs e)
        {
            if (AvisarSiIndexando()) return;
            if (GridDocumentos.SelectedItem is DocumentoRow fila)
            {
                var ventana = new CitacionWindow(fila.Documento) { Owner = this };
                ventana.ShowDialog();
                if (ventana.SeGuardo) _idsCorregidos.Add(fila.Documento.DocumentoId);
                // La ventana de cita puede haber corregido datos -- releer y
                // refrescar por si cambió algo.
                _documentos = Biblioteca.CargarDocumentos();
                ActualizarGrid();
            }
        }

        // Reemplaza la columna "Estado" (poco útil: el usuario ya ve si algo
        // quedó "para revisar" por el resaltado en rojo) por un enlace directo
        // al documento -- mismo criterio de apertura que CitacionWindow.
        private void LinkAbrirDocumento_Click(object sender, RoutedEventArgs e)
        {
            if (sender is not Hyperlink link || link.DataContext is not DocumentoRow fila) return;
            var documento = fila.Documento;

            try
            {
                if (!string.IsNullOrWhiteSpace(documento.OrigenUrl))
                {
                    Process.Start(new ProcessStartInfo(documento.OrigenUrl) { UseShellExecute = true });
                }
                else if (!string.IsNullOrWhiteSpace(documento.RutaArchivoOriginal))
                {
                    if (!File.Exists(documento.RutaArchivoOriginal))
                    {
                        MessageBox.Show(this, "No se encontró el archivo en la ruta guardada:\n" + documento.RutaArchivoOriginal,
                            "Archivo no disponible", MessageBoxButton.OK, MessageBoxImage.Warning);
                        return;
                    }
                    Process.Start(new ProcessStartInfo(documento.RutaArchivoOriginal) { UseShellExecute = true });
                }
                else
                {
                    MessageBox.Show(this, "Este documento no tiene una ruta local ni una URL de origen guardada.",
                        "Sin origen", MessageBoxButton.OK, MessageBoxImage.Information);
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, "No se pudo abrir: " + ex.Message, "Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        // El clic derecho no mueve la selección por defecto en un DataGrid
        // -- sin esto, "Borrar registro" podría borrar una fila distinta de
        // la que el usuario clickeó.
        private void GridDocumentos_PreviewMouseRightButtonDown(object sender, System.Windows.Input.MouseButtonEventArgs e)
        {
            var dep = (DependencyObject)e.OriginalSource;
            while (dep != null && dep is not DataGridRow)
                dep = VisualTreeHelper.GetParent(dep);
            if (dep is DataGridRow row) row.IsSelected = true;
        }

        private void MenuBorrarRegistro_Click(object sender, RoutedEventArgs e)
        {
            if (AvisarSiIndexando()) return;
            if (GridDocumentos.SelectedItem is not DocumentoRow fila) return;

            var confirmar = MessageBox.Show(this,
                $"¿Borrar definitivamente el registro {fila.Documento.DocumentoId} ({fila.Titulo})?\n\nEsta acción no se puede deshacer.",
                "Borrar registro", MessageBoxButton.YesNo, MessageBoxImage.Warning);
            if (confirmar != MessageBoxResult.Yes) return;

            _documentos.RemoveAll(d => d.DocumentoId == fila.Documento.DocumentoId);
            Biblioteca.GuardarDocumentos(_documentos);
            ActualizarGrid();
            Log($"{fila.Documento.DocumentoId} borrado del catálogo.", "WARN");
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
            _saltearDuplicadosEnLote = false;
            BtnAgregarArchivos.IsEnabled = false;
            BtnAgregarCarpeta.IsEnabled = false;
            BtnAgregarUrl.IsEnabled = false;

            int total = items.Count;
            int minutosEstimados = Math.Max(1, (int)Math.Round(total * SegundosEstimadosPorDocumento / 60.0));
            Log($"Procesando {total} documento(s) — tiempo estimado: {minutosEstimados} minuto(s).", "INFO");
            BarraProgreso.Value = 0;
            TxtProgreso.Text = $"0 / {total} (0%)";
            PanelProgreso.Visibility = Visibility.Visible;

            var agregados = new List<DocumentoRecord>();
            try
            {
                int procesados = 0;
                foreach (var item in items)
                {
                    await ProcesarUnoAsync(item.RutaLocal, item.Url, agregados);
                    procesados++;
                    int pct = (int)Math.Round(100.0 * procesados / total);
                    BarraProgreso.Value = pct;
                    TxtProgreso.Text = $"{procesados} / {total} ({pct}%)";
                }
            }
            finally
            {
                _procesando = false;
                BtnAgregarArchivos.IsEnabled = true;
                BtnAgregarCarpeta.IsEnabled = true;
                BtnAgregarUrl.IsEnabled = true;
                PanelProgreso.Visibility = Visibility.Collapsed;
            }

            // Si se procesó un único archivo, se muestra directamente la
            // cita (éxito o fallback) en vez de obligar a buscarlo en el
            // grid -- para un lote de varios, cada uno queda visible en el
            // grid con "Ver cita" bajo demanda.
            if (items.Count == 1 && agregados.Count == 1)
                new CitacionWindow(agregados[0]) { Owner = this }.ShowDialog();

            foreach (var doc in agregados)
                _idsRecienAgregados.Add(doc.DocumentoId);

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
                    IniciarSpinner($"Cargando {etiqueta}...");
                    bytes = await File.ReadAllBytesAsync(rutaLocal);
                }
                else
                {
                    IniciarSpinner($"Descargando {etiqueta}...");
                    bytes = await PdfValidacion.DescargarAsync(url!);
                }
            }
            catch (Exception ex)
            {
                DetenerSpinner();
                Log($"No se pudo obtener {etiqueta}: {ex.Message}", "ERROR");
                return;
            }
            DetenerSpinner();

            if (!PdfValidacion.EsPdfValido(bytes))
            {
                Log($"{etiqueta} no parece ser un PDF válido (firma inválida).", "ERROR");
                return;
            }

            string hash = PdfValidacion.CalcularHashSha256(bytes);
            var existente = _documentos.FirstOrDefault(d => d.HashSha256 == hash);
            if (existente != null)
            {
                if (_saltearDuplicadosEnLote)
                {
                    Log($"{etiqueta}: duplicado de {existente.DocumentoId}, salteado (Saltear todo).", "WARN");
                    return;
                }

                var dialogo = new DuplicadoWindow(etiqueta, existente) { Owner = this };
                dialogo.ShowDialog();

                if (dialogo.Resultado == ResultadoDuplicado.SaltearTodo)
                    _saltearDuplicadosEnLote = true;

                if (dialogo.Resultado != ResultadoDuplicado.AgregarIgual)
                {
                    Log($"{etiqueta}: duplicado de {existente.DocumentoId}, no se agregó.", "WARN");
                    return;
                }
            }

            string texto;
            IniciarSpinner($"Extrayendo texto de {etiqueta}...");
            try
            {
                // PdfPig es sincrónico/CPU-bound -- se corre en Task.Run para
                // no bloquear el hilo de UI y dejar que el spinner anime.
                texto = await Task.Run(() => PdfTexto.ExtraerPrimerasPaginas(bytes));
            }
            catch (Exception ex)
            {
                Log($"No se pudo leer el contenido de {etiqueta}: {ex.Message}", "WARN");
                texto = "";
            }
            finally
            {
                DetenerSpinner();
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
                bool listo = await LlamaServerProceso.AsegurarIniciadoAsync((msg, nivel, overwrite) => Log(msg, nivel, overwrite));
                ActualizarEstadoServidor();

                ExtraccionResultado? resultado = null;
                if (listo)
                {
                    IniciarSpinner($"Consultando modelo local para {etiqueta}...");
                    try
                    {
                        resultado = await ExtraccionLlm.ExtraerAsync(texto);
                    }
                    catch (Exception ex)
                    {
                        Log($"Error consultando el modelo local para {etiqueta}: {ex.Message}", "ERROR");
                    }
                    finally
                    {
                        DetenerSpinner();
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
            if (AvisarSiIndexando()) return;
            var ventana = new BibliotecaWindow() { Owner = this };
            ventana.ShowDialog();
            foreach (var id in ventana.IdsModificados) _idsCorregidos.Add(id);
            // La biblioteca completa permite editar y borrar registros --
            // releer y refrescar por si el catálogo cambió.
            _documentos = Biblioteca.CargarDocumentos();
            ActualizarGrid();
        }

        private void BtnConfiguracion_Click(object sender, RoutedEventArgs e)
        {
            var wnd = new SettingsWindow(_settings) { Owner = this };
            if (wnd.ShowDialog() == true)
            {
                Biblioteca.GuardarSettings(_settings);
                ActualizarGrid();
                AplicarTema();
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
